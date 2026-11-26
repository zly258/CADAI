using CadAI.Core;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// 跨宿主交换目录（AutoCAD 导出 → AutoCAD 读取）的路径约定与落盘。
///
/// 这是"两个进程之间唯一的交接面"，出错方式都很安静：路径解析跑偏会让另一端
/// 读不到文件，静默回退会让人"以为写进去了"。这里锁住三件事——环境变量优先、
/// 文件名只含安全字符、写入与读取能对上。
/// </summary>
public sealed class ExchangeStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _previous;

    public ExchangeStoreTests()
    {
        // 与 CaptureRetentionPolicyTests 同约定：临时目录放仓库 .build 下（%TEMP% 在沙箱里会 ACL 失败）。
        _dir = Path.Combine(RepoPaths.FindRepoRoot(), ".build", "test-scratch", "Exchange-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _previous = Environment.GetEnvironmentVariable("CADAI_EXCHANGE_DIR");
        Environment.SetEnvironmentVariable("CADAI_EXCHANGE_DIR", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CADAI_EXCHANGE_DIR", _previous);
        // 只删自己这一层：test-scratch 是多个测试类共用的父目录，删它会顺手清掉
        // 并行运行的其他用例（2026-10-16 实测过一次 CaptureRetentionPolicyTests 因此假红）。
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ResolveDirectory_HonoursTheEnvironmentVariable()
        => Assert.Equal(Path.GetFullPath(_dir), ExchangeStore.ResolveDirectory());

    [Fact]
    public void ResolveDirectory_FallsBackWhenTheVariableIsBlank()
    {
        Environment.SetEnvironmentVariable("CADAI_EXCHANGE_DIR", "   ");
        Environment.SetEnvironmentVariable("CADAI_EXCHANGE_DIR", null);

        string resolved = ExchangeStore.ResolveDirectory();

        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.False(resolved.StartsWith("   ", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildFileName_IsTimestampedAndFreeOfPathSeparators()
    {
        string name = ExchangeStore.BuildFileName(@"..\..\evil/name");

        Assert.EndsWith(".json", name, StringComparison.Ordinal);
        Assert.DoesNotContain("/", name, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", name, StringComparison.Ordinal);
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        Assert.True(name.Length > 5);
    }

    [Fact]
    public void BuildFileName_UsesAFallbackPrefixWhenBlank()
        => Assert.StartsWith("exchange-", ExchangeStore.BuildFileName("  "), StringComparison.Ordinal);

    [Fact]
    public void WriteJson_ThenReadText_RoundTripsThroughTheExchangeDirectory()
    {
        string path = ExchangeStore.WriteJson("drawing", "{\"ok\":true}");

        Assert.Equal(Path.GetFullPath(_dir), Path.GetDirectoryName(path));
        Assert.Equal("{\"ok\":true}", ExchangeStore.ReadText(path));
        // 相对名字按交换目录解析，与绝对路径等价。
        Assert.Equal("{\"ok\":true}", ExchangeStore.ReadText(Path.GetFileName(path)));
    }

    [Fact]
    public void WriteJson_DoesNotOverwriteAnExistingFile()
    {
        string first = ExchangeStore.WriteJson("drawing", "{\"n\":1}");
        string firstContents = File.ReadAllText(first);

        // 同一毫秒内反复写：文件名必须继续发散，绝不能覆盖前一份。
        for (int i = 0; i < 3; i++) ExchangeStore.WriteJson("drawing", "{\"n\":2}");

        Assert.Equal(firstContents, File.ReadAllText(first));
        Assert.Equal(4, Directory.GetFiles(_dir, "*.json").Length);
    }

    [Fact]
    public void ReadText_ThrowsForAMissingFileInsteadOfReturningEmpty()
    {
        Assert.ThrowsAny<IOException>(() => ExchangeStore.ReadText("nope.json"));
    }

    [Fact]
    public void ResolveFile_KeepsAbsolutePathsAndAnchorsRelativeOnes()
    {
        string absolute = Path.Combine(_dir, "x.json");

        Assert.Equal(Path.GetFullPath(absolute), ExchangeStore.ResolveFile(absolute));
        Assert.Equal(Path.GetFullPath(absolute), ExchangeStore.ResolveFile("x.json"));
    }

    [Fact]
    public void ResolveFile_RejectsBlankInput()
        => Assert.ThrowsAny<ArgumentException>(() => ExchangeStore.ResolveFile("  "));
}