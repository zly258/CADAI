#nullable enable
using System;
using System.IO;
using System.Text;

#if AUTOCAD_MCP
namespace CadAI.Mcp.AutoCAD.Core
#else
namespace CadAI.Core
#endif
{
    /// <summary>
    /// 跨宿主交换目录：把"一个宿主提取、另一个宿主消费"的中间数据落到**双方都能读写**的位置。
    ///
    /// 为什么必须有它：AutoCAD 插件与 AutoCAD 插件是两个独立进程，工作区互不可见
    /// （AutoCAD 端在 <c>Documents\CADAI</c>，AutoCAD 端在
    /// <c>Documents\CADAI</c>）。没有共享目录时，"DWG 提取全部信息 →
    /// AutoCAD 建模"只能靠用户手工复制粘贴 JSON——大图必然出错，且无法追溯用的是哪一版数据。
    ///
    /// 目录解析顺序（与技能库同一口径，便于统一配置）：
    ///   CADAI_EXCHANGE_DIR      —— 通用名；指向共享盘即可多人/多机共用
    ///   CADAI_EXCHANGE_DIR  —— 兼容名
    ///   %APPDATA%\CADAI\Exchange （同机两个宿主默认就是同一个目录）
    ///
    /// **写入失败一律向上抛**：跨宿主交接最怕"以为写进去了"。这里刻意不做静默回退——
    /// 环境变量指向不可达的共享盘时，宁可让调用方看到明确错误，也不要偷偷写到本机
    /// 再让另一端永远读不到。
    ///
    /// 纯 BCL 实现（只用 System.IO / System.Text），因此 net48 的 Add-in 与 net8 的 MCP
    /// 可以共用同一份逻辑，也能被单元测试直接链接覆盖。
    /// </summary>
#if AUTOCAD_MCP
    internal static class ExchangeStore
#else
    public static class ExchangeStore
#endif
    {
        /// <summary>目录名的环境变量，按顺序取第一个非空的。</summary>
        private static readonly string[] DirectoryVariables = { "CADAI_EXCHANGE_DIR" };

        /// <summary>单个交换文件的大小上限（64 MB）：超过就拒绝读取，避免把宿主拖到 OOM。</summary>
        public const long MaxBytes = 64L * 1024 * 1024;

        /// <summary>
        /// 文件名前缀里不允许出现的字符（路径分隔符、通配符、以及点号——点号参与
        /// ".." 这类相对路径构造，干脆一起换成下划线，扩展名由调用方另外拼）。
        /// </summary>
        private const string UnsafeNameCharacters = "\\/:*?\"<>|.";

        /// <summary>
        /// 交换目录（绝对路径）。环境变量为空则用 <c>%APPDATA%\CADAI\Exchange</c>；
        /// 环境变量非法（含非法字符等）时退回默认值，避免一个手滑的变量让功能整个不可用。
        /// **不创建目录**，只解析路径。
        /// </summary>
        public static string ResolveDirectory()
        {
            foreach (string variable in DirectoryVariables)
            {
                string? candidate = ReadEnvironment(variable);
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    return Path.GetFullPath(candidate!.Trim());
                }
                catch
                {
                    // 路径非法就试下一个候选。
                }
            }

            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrWhiteSpace(appData))
                    return Path.Combine(appData, "CADAI", "Exchange");
            }
            catch
            {
                // 拿不到 AppData 时退到当前用户目录下的同名目录。
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CADAI", "Exchange");
        }

        /// <summary>确保交换目录存在并返回它；目录建不出来时抛出带路径的异常。</summary>
        public static string EnsureDirectory()
        {
            string directory = ResolveDirectory();
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    "无法创建跨宿主交换目录：" + directory
                    + "。请检查 CADAI_EXCHANGE_DIR 是否指向可达的共享目录。（" + ex.Message + "）", ex);
            }
            return directory;
        }

        /// <summary>生成一个按时间排序、只含安全字符的交换文件名：<c>&lt;prefix&gt;-yyyyMMdd-HHmmss-fff.json</c>。</summary>
        public static string BuildFileName(string prefix)
        {
            string safe = Sanitize(prefix);
            if (safe.Length == 0) safe = "exchange";
            return safe + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json";
        }

        /// <summary>
        /// 把一个交换文件写进交换目录（UTF-8，无 BOM），返回**绝对路径**供另一端使用。
        /// 文件重名不覆盖不了：文件名带毫秒时间戳，且这里用 CreateNew 失败后追加序号重试。
        /// </summary>
        public static string WriteJson(string prefix, string content)
        {
            string directory = EnsureDirectory();
            string name = BuildFileName(prefix);
            string path = Path.Combine(directory, name);

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(content ?? string.Empty);
                    }
                    return path;
                }
                catch (IOException) when (attempt <= 5)
                {
                    // 同毫秒内撞名（或杀毒软件短暂占用）：换个序号再来。
                    name = Path.GetFileNameWithoutExtension(BuildFileName(prefix)) + "-" + attempt.ToString() + ".json";
                    path = Path.Combine(directory, name);
                }
            }
        }

        /// <summary>
        /// 解析一个交换文件路径：绝对路径原样使用；相对路径/纯文件名则相对交换目录解析。
        /// 目录名不存在时抛错，不发散到别处。
        /// </summary>
        public static string ResolveFile(string pathOrName)
        {
            if (string.IsNullOrWhiteSpace(pathOrName))
                throw new ArgumentException("交换文件路径不能为空。", nameof(pathOrName));

            string value = pathOrName.Trim();
            try
            {
                return Path.IsPathRooted(value) ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(ResolveDirectory(), value));
            }
            catch (Exception ex)
            {
                throw new IOException("交换文件路径非法：" + value + "。（" + ex.Message + "）", ex);
            }
        }

        /// <summary>读取交换文件全文；文件不存在/过大都会抛出明确的异常（绝不返回半截内容）。</summary>
        public static string ReadText(string pathOrName)
        {
            string path = ResolveFile(pathOrName);
            if (!File.Exists(path))
                throw new FileNotFoundException("交换文件不存在：" + path + "。请先在 AutoCAD 侧导出图纸 JSON 再交给 AutoCAD。", path);

            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
                throw new IOException("交换文件过大（" + info.Length.ToString() + " 字节，上限 " + MaxBytes.ToString() + "）：" + path);

            return File.ReadAllText(path, Encoding.UTF8);
        }

        /// <summary>把前缀里的路径分隔符等不安全字符换成下划线。</summary>
        private static string Sanitize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var builder = new StringBuilder(value!.Length);
            foreach (char c in value.Trim())
                builder.Append(UnsafeNameCharacters.IndexOf(c) >= 0 || c < ' ' ? '_' : c);
            return builder.ToString().Trim().TrimEnd('.');
        }

        private static string? ReadEnvironment(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); }
            catch { return null; }
        }
    }
}