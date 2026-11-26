using System.Text;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// 脚本类文件的编码防线：<c>*.ps1</c> 与 <c>*.iss</c> 必须是「UTF-8 with BOM」。
///
/// 背景（2026-10-16 实测事故）：改写 build.ps1 时用无 BOM 的 UTF-8 写回，Windows
/// PowerShell 5.1 便按系统 ANSI（GBK）解析源码，中文注释变成乱码并抛 ParserError，
/// 整个构建当场跑不起来；Inno Setup 同样按系统 ANSI 读无 BOM 的 .iss。而 5.1 无法被
/// 彻底排除——<c>Host.AutoCAD.csproj</c> 的 post-build <c>Exec</c> 就是用 5.1 调 install.ps1。
///
/// 因此把"必须带 BOM"变成构建时断言：本用例扫全仓，缺 BOM 直接红，而不是等用户
/// 双击脚本才发现。修法：用 <c>New-Object System.Text.UTF8Encoding($true)</c> 写回，
/// 或 <c>Set-Content -Encoding utf8BOM</c>。
/// </summary>
public class ScriptEncodingTests
{
    private static readonly string[] RequiredBomExtensions = { ".ps1", ".iss" };

    /// <summary>不被扫描的目录（构建产物 / 版本库内部 / 外部依赖）。</summary>
    private static readonly string[] ExcludedDirectories =
    {
        ".git", ".build", "bin", "obj", "dist", "output", "TestResults", ".vs", ".reference"
    };

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    [Fact]
    public void ScriptsAndInstallerScripts_MustBeUtf8WithBom()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var offenders = new List<string>();
        var checkedFiles = 0;

        foreach (var file in EnumerateScriptFiles(repoRoot))
        {
            checkedFiles++;
            var head = new byte[3];
            using (var stream = File.OpenRead(file))
            {
                if (stream.Read(head, 0, 3) < 3) { offenders.Add(Relative(repoRoot, file)); continue; }
            }
            if (!head.SequenceEqual(Utf8Bom)) offenders.Add(Relative(repoRoot, file));
        }

        // 扫描本身要真的扫到东西：路径写错时不能"零文件通过"。
        Assert.True(checkedFiles > 0, "没有扫描到任何 .ps1 / .iss，说明扫目录的逻辑失效了。");
        Assert.True(offenders.Count == 0,
            "以下脚本缺少 UTF-8 BOM（Windows PowerShell 5.1 / Inno Setup 会按 ANSI 解析，中文必乱码）：\n  "
            + string.Join("\n  ", offenders)
            + "\n修法：以 UTF-8 with BOM 重新写回（PowerShell 用 [System.IO.File]::WriteAllText($p,$c,(New-Object System.Text.UTF8Encoding($true)))）。");
    }

    /// <summary>
    /// 带 BOM 的文件必须能按 UTF-8 完整解码——BOM 与内容编码不一致（例如 BOM 是 UTF-8
    /// 而正文是 GBK）比缺 BOM 更隐蔽：编辑器看着正常，PowerShell 读出来是乱码。
    /// </summary>
    [Fact]
    public void ScriptsAndInstallerScripts_DecodeAsUtf8()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var offenders = new List<string>();

        foreach (var file in EnumerateScriptFiles(repoRoot))
        {
            var bytes = File.ReadAllBytes(file);
            try
            {
                strictUtf8.GetString(bytes, 0, bytes.Length);
            }
            catch (DecoderFallbackException)
            {
                offenders.Add(Relative(repoRoot, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "以下脚本不是合法 UTF-8（大概率是 GBK/ANSI 存的）：\n  " + string.Join("\n  ", offenders));
    }

    private static IEnumerable<string> EnumerateScriptFiles(string repoRoot)
    {
        var pending = new Stack<string>();
        pending.Push(repoRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (ExcludedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (RequiredBomExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }

    private static string Relative(string repoRoot, string fullPath)
        => Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
}