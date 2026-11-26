using CadAI.Core;
using Xunit;

namespace CadAI.Tests;

public class CaptureRetentionPolicyTests : IDisposable
{
    private readonly string _dir;

    public CaptureRetentionPolicyTests()
    {
        // 不用 %TEMP%（testhost 在沙箱 ACL 环境下对 Temp 新目录 CreateDirectory
        // 会报 UnauthorizedAccessException，2026-10-14 回归实测）；仓库 .build
        // 下的临时区一定可写，Dispose 顺带清理，不污染仓库。
        _dir = Path.Combine(RepoPaths.FindRepoRoot(), ".build", "test-scratch", "CaptureRetention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        // 刻意不删父目录 test-scratch：它是多个测试类共用的临时区，删它会顺手清掉
        // 并行运行的其他用例（2026-10-16 实测过一次假红）。
    }

    private void CreateImage(string name, DateTime writeTime)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 0x89, 0x50 }); // PNG 魔数前缀即可，内容不重要
        File.SetLastWriteTimeUtc(path, writeTime);
    }

    [Fact]
    public void BelowLimit_DeletesNothing()
    {
        CreateImage("a.png", DateTime.UtcNow.AddHours(-1));
        CreateImage("b.png", DateTime.UtcNow);

        int deleted = CaptureRetentionPolicy.Enforce(_dir, maxFiles: 2);

        Assert.Equal(0, deleted);
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void OverLimit_DeletesOldestFirst()
    {
        for (int i = 0; i < 5; i++)
            CreateImage($"img{i}.png", DateTime.UtcNow.AddMinutes(-10 + i));

        int deleted = CaptureRetentionPolicy.Enforce(_dir, maxFiles: 2);

        Assert.Equal(3, deleted);
        string[] remaining = Directory.GetFiles(_dir).Select(p => Path.GetFileName(p)!).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "img3.png", "img4.png" }, remaining);
    }

    [Fact]
    public void NonPositiveLimit_FallsBackToDefault()
    {
        CreateImage("old.png", DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "keep me");
        File.WriteAllText(Path.Combine(_dir, "data.json"), "{}");

        // 非正上限归一到默认值（不删除任何东西），非图片文件永不删除。
        int deleted = CaptureRetentionPolicy.Enforce(_dir, maxFiles: 0);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(Path.Combine(_dir, "old.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "data.json")));
    }

    [Fact]
    public void MissingDirectory_ReturnsZeroWithoutThrowing()
    {
        int deleted = CaptureRetentionPolicy.Enforce(Path.Combine(_dir, "does-not-exist"), maxFiles: 5);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void Subdirectories_AreCovered()
    {
        string dialogs = Directory.CreateDirectory(Path.Combine(_dir, "Dialogs")).FullName;
        // main0..main3 依次更新（main3 最旧），dialogs 里的图更旧。
        for (int i = 0; i < 4; i++)
            CreateImage($"main{i}.png", DateTime.UtcNow.AddMinutes(-i));
        File.WriteAllBytes(Path.Combine(dialogs, "d0.png"), new byte[] { 0x89, 0x50 });
        File.SetLastWriteTimeUtc(Path.Combine(dialogs, "d0.png"), DateTime.UtcNow.AddMinutes(-10));

        int deleted = CaptureRetentionPolicy.Enforce(_dir, maxFiles: 3);

        Assert.Equal(2, deleted); // 删最旧的 2 张：d0 与 main3
        Assert.Equal(3, Directory.EnumerateFiles(_dir, "*.png", SearchOption.AllDirectories).Count());
    }
}
