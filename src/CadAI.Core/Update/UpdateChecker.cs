using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CadAI.Core
{
    /// <summary>
    /// 四产品共用的自动更新检查参数（2026-10-24 由四份近乎相同的实现合并而来，
    /// 产品侧差异只剩这一组常量：发布目录、版本文件名、安装包模式、回退 DLL、弹窗标题、日志通道）。
    /// </summary>
    public sealed class UpdateCheckOptions
    {
        /// <summary>弹窗标题（与安装包 / 发布目录同名）。</summary>
        public string? ProductCaption;

        /// <summary>本产品的发布目录（产品侧自行解析环境变量覆盖后传入）。</summary>
        public string? SharePath;

        /// <summary>发布目录下的纯文本版本文件名（内容为一行版本号）。</summary>
        public string? VersionFileName;

        /// <summary>安装包文件名模式（回退方案一：从文件名里取第一段版本号）。
        /// 套件包发布后，翻译助手也要能匹配智能助手名字的套件安装包，所以是数组。</summary>
        public string[] SetupExePatterns = Array.Empty<string>();

        /// <summary>回退方案二：读发布目录里该 DLL 的文件版本（FileVersionInfo 不加载程序集）。</summary>
        public string? RemoteDllName;

        /// <summary>本地版本提供者（通常 = 宿主程序集的 Assembly.GetName().Version）。</summary>
        public Func<Version?>? LocalVersion;

        /// <summary>可选日志通道（如宿主启动日志）；为空则不写。</summary>
        public Action<string>? ProgressLog;

        /// <summary>
        /// 弹窗回调：发现新版本时由后台线程调用（本类不直接依赖 WPF 的 UpdatePrompt，
        /// 由各宿主注入 <c>ChatCore.UpdatePrompt.Show</c>，保持本文件纯 BCL、可被测试工程链接编译）。
        /// </summary>
        public Action<string?, string, string?, bool>? ShowPrompt;

        /// <summary>幂等闸门：每份 options 的检查每进程只发起一次。</summary>
        internal volatile bool Started;
    }

    /// <summary>
    /// 四产品共用的自动更新检查实现。
    /// 版本判定优先级：<c>UpdateCheckOptions.VersionFileName</c>（一行纯版本号）→ 安装包文件名里的版本段 →
    /// 发布目录里宿主 DLL 的文件版本。仅读元数据，不加载任何程序集。
    ///
    /// 线程与阻塞纪律（改这里前务必先读，自四份原实现逐字搬迁）：
    ///  1. 所有 IO（含 <c>Directory.Exists</c>）都在**后台线程**上做。共享盘不可达时这些调用会一直等
    ///     SMB 超时，放到 CAD 主线程上就是整个界面卡死。
    ///  2. 检查有 8 秒上限；超时后仍挂在死共享盘上的探测任务必须挂观察者吃掉迟到的异常，
    ///     否则会变成 UnobservedTaskException（跨线程未处理异常会带走 CAD 进程）。
    ///  3. 弹窗与打开目录经 <see cref="UpdateCheckOptions.ShowPrompt"/> 交给宿主
    ///     （宿主应接入 <see cref="ChatCore.UpdatePrompt"/>：它优先用宿主 UI 线程，
    ///     取不到就自建 STA 线程，且自身绝不抛异常）。
    ///  检查过程中的任何异常（网络不通、路径不可达等）一律静默忽略，绝不影响插件加载。
    /// </summary>
    public static class UpdateChecker
    {
        private const int ProbeTimeoutSeconds = 8;

        /// <summary>文件名里的版本号形状（2.0 / 2.0.0 / 2.0.0.0）。</summary>
        private static readonly Regex VersionToken = new Regex(@"\d+(?:\.\d+){1,3}", RegexOptions.Compiled);

        /// <summary>启动后台检查。立即返回，不阻塞 CAD 启动；每份 options 只生效一次。</summary>
        public static void BeginCheckAsync(UpdateCheckOptions options)
        {
            if (options == null) return;
            if (options.Started) return;
            options.Started = true;

            Task.Run(() =>
            {
                string? remote = null;
                string? directory = null;
                bool reachable = false;
                try
                {
                    // 网络共享可能不可达，给一次短超时兜底；超时按"无新版本"处理。
                    var probe = Task.Run(() =>
                    {
                        string? found;
                        string? version = CheckForUpdate(options, out found);
                        return new UpdateCandidate(version, found);
                    });

                    var finished = Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(ProbeTimeoutSeconds)))
                                       .GetAwaiter().GetResult();
                    if (ReferenceEquals(finished, probe))
                    {
                        UpdateCandidate candidate = probe.GetAwaiter().GetResult()!;
                        remote = candidate.Version;
                        directory = candidate.Directory;
                    }
                    else
                    {
                        // 超时胜出：probe 还在跑（可能卡在死共享盘的 SMB 超时上）。
                        // 不能就这么丢下它——它的异常没人接会变成 UnobservedTaskException，
                        // 所以挂一个观察者把迟到的异常吃掉。
                        Observe(probe);
                    }

                    if (!string.IsNullOrEmpty(remote))
                    {
                        // 可达性判定放在后台线程：不可达时 Directory.Exists 会等 SMB 超时，
                        // 放到 UI 线程就是整个宿主界面卡住。
                        reachable = SafeDirectoryExists(directory);
                    }
                }
                catch (Exception)
                {
                    // 静默：共享不可达、权限不足、版本文件损坏都不打扰用户。
                }

                if (string.IsNullOrEmpty(remote)) return;
                if (reachable && options.ProgressLog != null)
                    options.ProgressLog("检查更新：发现新版本 v" + remote + "（当前 v" + LocalVersionString(options) + "）。");

                string message = "发现新版本 " + remote + "（当前版本 " + LocalVersionString(options) + "）。\n\n"
                    + "是否打开更新目录？\n" + directory;
                options.ShowPrompt?.Invoke(options.ProductCaption, message, directory, reachable);
            });
        }

        /// <summary>
        /// 执行一次检查。有新版本时返回版本号并把发布目录写进 <paramref name="updateDirectory"/>；
        /// 无新版本或无法判定时返回 null。
        /// </summary>
        public static string? CheckForUpdate(UpdateCheckOptions options, out string? updateDirectory)
        {
            updateDirectory = null;
            if (options == null) return null;

            Version? local = ResolveLocalVersion(options);
            if (local == null) return null;

            Version? remote = ResolveRemoteVersion(options, out string? root);
            if (remote == null) return null;
            if (remote <= local) return null;

            updateDirectory = root;
            return remote.ToString();
        }

        /// <summary>宽松解析版本号（"1.4" / "1.4.0" / "v1.4.0" 都行）；失败返回 null。</summary>
        public static Version? ParseVersion(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            text = text.Trim().TrimStart('v', 'V');
            return Version.TryParse(text, out Version? v) ? v : null;
        }

        private static Version? ResolveLocalVersion(UpdateCheckOptions options)
        {
            try { return options.LocalVersion != null ? options.LocalVersion() : null; }
            catch (Exception) { return null; }
        }

        private static string LocalVersionString(UpdateCheckOptions options)
        {
            Version? v = ResolveLocalVersion(options);
            return v == null ? "未知" : v.ToString();
        }

        /// <summary>
        /// 检查远程版本：版本文件 → 安装包文件名 → DLL 文件版本；均失败返回 null。
        /// 命中的目录经 <paramref name="root"/> 回传，供"打开更新目录"使用（读到哪个目录就开哪个）。
        /// </summary>
        private static Version? ResolveRemoteVersion(UpdateCheckOptions options, out string? root)
        {
            string? share = options.SharePath;

            Version? v = TryReadVersionFile(options, share);
            if (v != null) { root = share; return v; }

            v = TryReadSetupExeVersion(options, share);
            if (v != null) { root = share; return v; }

            v = TryReadRemoteDllVersion(options, share);
            if (v != null) { root = share; return v; }

            root = null;
            return null;
        }

        /// <summary>读取发布目录下的纯文本版本文件（内容为一行版本号，允许前后空白与 v 前缀）。</summary>
        private static Version? TryReadVersionFile(UpdateCheckOptions options, string? root)
        {
            try
            {
                if (root == null || options.VersionFileName == null) return null;
                string path = Path.Combine(root, options.VersionFileName);
                if (!File.Exists(path)) return null;
                return ParseVersion(File.ReadAllText(path));
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 回退方案一：从安装包文件名解析版本——<c>CADAI-2.0.0-setup.exe</c> 与历史名都认。
        /// 不按固定前缀截取：产品名里带 "智能助手" 这类非 ASCII 段，截前缀会截出 "智能助手-2.0.0" 而解析失败；
        /// 这里直接取文件名里第一段版本号，两种命名都稳。
        /// </summary>
        private static Version? TryReadSetupExeVersion(UpdateCheckOptions options, string? root)
        {
            try
            {
                if (root == null || options.SetupExePatterns == null) return null;
                if (!Directory.Exists(root)) return null;
                foreach (string pattern in options.SetupExePatterns)
                foreach (string file in Directory.GetFiles(root, pattern))
                {
                    Match match = VersionToken.Match(Path.GetFileNameWithoutExtension(file));
                    if (!match.Success) continue;
                    Version? parsed = ParseVersion(match.Value);
                    if (parsed != null) return parsed;
                }
            }
            catch (Exception ex) { Trace.TraceWarning("[UpdateChecker] update share unavailable: " + ex.Message); }
            return null;
        }

        /// <summary>回退方案二：读发布目录里宿主 DLL 的文件版本（FileVersionInfo 不加载程序集）。</summary>
        private static Version? TryReadRemoteDllVersion(UpdateCheckOptions options, string? root)
        {
            try
            {
                if (root == null || options.RemoteDllName == null) return null;
                string path = Path.Combine(root, options.RemoteDllName);
                if (!File.Exists(path)) return null;
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return ParseVersion(info.FileVersion);
            }
            catch (Exception) { return null; }
        }

        /// <summary>目录可达性判定（只读，供后台线程调用）。</summary>
        private static bool SafeDirectoryExists(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;
            try { return Directory.Exists(directory); }
            catch (Exception) { return false; }
        }

        /// <summary>挂一个观察者吞掉迟到任务的异常（超时后 probe 仍在跑，没人接就会变成未观察异常）。</summary>
        private static void Observe(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; },
                              TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>一次检查的结果：远端版本号 + 判定所用的发布目录。</summary>
        private sealed class UpdateCandidate
        {
            internal UpdateCandidate(string? version, string? directory)
            {
                Version = version;
                Directory = directory;
            }

            internal string? Version { get; }
            internal string? Directory { get; }
        }
    }
}
