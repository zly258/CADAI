using CadAI.Core;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// 验证 CADAI 用户配置目录路径约定：必须为绝对路径、位于 Roaming AppData 下，
/// 且由宿主环境通过 ProductDataPaths 路由而非硬编码。
/// </summary>
public class HostDataPathsTests
{
    [Fact]
    public void ProductFolderName_MustBeNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ProductDataPaths.AutoCadProductFolder));
    }

    [Fact]
    public void UserDataDirectory_MustBeAbsolute()
    {
        Assert.True(Path.IsPathRooted(ProductDataPaths.AutoCadUserData));
    }

    [Fact]
    public void UserDataDirectory_LivesUnderRoamingAppData()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) return;
        Assert.StartsWith(appData, ProductDataPaths.AutoCadUserData, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostEnvironment_MustRouteUserDataThroughProductDataPaths()
    {
        string autoCad = File.ReadAllText(RepoPaths.Resolve(
            "src", "CadAI.Host.AutoCAD", "DwgChat", "Hosting", "CadHostEnvironment.cs"));

        Assert.Contains("UserDataDirectory", autoCad, StringComparison.Ordinal);
        Assert.Contains("ProductDataPaths.AutoCadUserData", autoCad, StringComparison.Ordinal);
    }

    [Fact]
    public void AiSettings_MustRouteThroughHostUserDataDirectory()
    {
        string source = File.ReadAllText(RepoPaths.Resolve(
            "src", "CadAI.Core", "Agent", "AiSettings.cs"));

        Assert.Contains("UserDataDirectory", source, StringComparison.Ordinal);
        Assert.Contains("ProductDataPaths.AutoCadUserData", source, StringComparison.Ordinal);
    }
}
