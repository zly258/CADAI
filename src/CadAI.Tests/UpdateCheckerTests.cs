using System.IO;
using CadAI.Core;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// 四产品共用更新检查器（ChatCore.UpdateChecker）的判定逻辑。
/// 2026-10-24 由四份近乎相同的实现合并而来——合并的前提是行为不变，
/// 所以这里用真实文件系统守三级回退的判定与"远端不比本地新就不报"的边界。
/// </summary>
public class UpdateCheckerTests
{
    private static string ScratchDir(string name)
    {
        var dir = Path.Combine(RepoPaths.FindRepoRoot(), ".build", "test-scratch", "UpdateChecker-" + name + "-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static UpdateCheckOptions Options(string dir, string localVersion)
        => new UpdateCheckOptions
        {
            ProductCaption = "测试产品",
            SharePath = dir,
            VersionFileName = "test.version.txt",
            SetupExePatterns = new[] { "测试产品-*-setup.exe" },
            RemoteDllName = "test.dll",
            LocalVersion = () => CadAI.Core.UpdateChecker.ParseVersion(localVersion),
        };

    [Theory]
    [InlineData("1.4", "1.4")]
    [InlineData("v2.1.0", "2.1.0")]
    [InlineData("  2.0  ", "2.0")]
    [InlineData("2.1.0.0", "2.1.0.0")]
    public void ParseVersion_accepts_loose_formats(string text, string expected)
    {
        var parsed = CadAI.Core.UpdateChecker.ParseVersion(text);
        Assert.Equal(expected, parsed?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData(null)]
    public void ParseVersion_returns_null_on_garbage(string? text)
    {
        Assert.Null(CadAI.Core.UpdateChecker.ParseVersion(text));
    }

    [Fact]
    public void Version_file_beats_local_when_remote_is_newer()
    {
        string dir = ScratchDir("file-newer");
        File.WriteAllText(Path.Combine(dir, "test.version.txt"), "9.9.9\n");

        string? updateDir;
        string? remote = CadAI.Core.UpdateChecker.CheckForUpdate(Options(dir, "1.0.0"), out updateDir);

        Assert.Equal("9.9.9", remote);
        Assert.Equal(dir, updateDir);
    }

    [Fact]
    public void No_update_reported_when_remote_is_not_newer()
    {
        string dir = ScratchDir("file-older");
        File.WriteAllText(Path.Combine(dir, "test.version.txt"), "0.0.9");

        string? updateDir;
        string? remote = CadAI.Core.UpdateChecker.CheckForUpdate(Options(dir, "1.0.0"), out updateDir);

        Assert.Null(remote);
        Assert.Null(updateDir);
    }

    [Fact]
    public void Setup_exe_filename_is_second_fallback()
    {
        string dir = ScratchDir("setup-exe");
        File.WriteAllText(Path.Combine(dir, "测试产品-3.2.1-setup.exe"), "dummy");

        string? updateDir;
        string? remote = CadAI.Core.UpdateChecker.CheckForUpdate(Options(dir, "3.0.0"), out updateDir);

        Assert.Equal("3.2.1", remote);
        Assert.Equal(dir, updateDir);
    }

    [Fact]
    public void Missing_share_reports_no_update()
    {
        string dir = Path.Combine(RepoPaths.FindRepoRoot(), ".build", "test-scratch", "no-such-share-dir");

        string? updateDir;
        string? remote = CadAI.Core.UpdateChecker.CheckForUpdate(Options(dir, "1.0.0"), out updateDir);

        Assert.Null(remote);
        Assert.Null(updateDir);
    }
}
