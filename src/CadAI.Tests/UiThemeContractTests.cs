using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// UI 主题契约：守住 2026-10-19 统一两套宿主 UI 时定下的三条底线。
///
/// 1) 禁用态不得用 Opacity 压暗——它会把背景、边框、文字一起变淡，
///    实测文字对比度掉到 2.2:1 左右，禁用按钮几乎看不清。
/// 2) 禁用文字对禁用底/窗口底必须 ≥3:1（WCAG 非文本对比度）。
/// 3) 业务 XAML 不写死颜色；色值只能出现在 Colors.xaml。
/// </summary>
public sealed class UiThemeContractTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Regex HexColor = new("#[0-9A-Fa-f]{6,8}", RegexOptions.Compiled);

    [Fact]
    public void NoXamlUsesOpacityForInteractionStates()
    {
        var offenders = XamlFiles()
            .Where(path => File.ReadAllText(path).Contains("Property=\"Opacity\"", StringComparison.Ordinal))
            .Select(Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "禁用/悬停态请改用 Background/Foreground/BorderBrush，不要用 Opacity:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void DisabledTextContrastIsAtLeastThreeToOne()
    {
        Dictionary<string, string> colors = Colors();
        string disabledText = colors["Color.TextDisabled"];
        string disabledBackground = colors["Color.ControlDisabled"];
        string windowBackground = colors["Color.WindowBackground"];

        double onDisabled = Contrast(disabledText, disabledBackground);
        double onWindow = Contrast(disabledText, windowBackground);

        Assert.True(onDisabled >= 3.0, $"禁用文字/禁用底对比度 {onDisabled:F2} < 3.0");
        Assert.True(onWindow >= 3.0, $"禁用文字/窗口底对比度 {onWindow:F2} < 3.0");
    }

    [Fact]
    public void BusinessXamlContainsNoColorLiterals()
    {
        string colorsFile = RepoPaths.Resolve("src", "CadAI.Core", "UI", "Themes", "Colors.xaml");
        var offenders = new List<string>();

        foreach (string path in XamlFiles())
        {
            if (string.Equals(path, colorsFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HexColor.IsMatch(File.ReadAllText(path)))
            {
                offenders.Add(Relative(path));
            }
        }

        Assert.True(offenders.Count == 0,
            "业务 XAML 里出现了颜色字面量，请改用 Colors.xaml 的 Brush.* token:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void AutoCadWindowsNoLongerUseTheirOwnStyleDictionary()
    {
        string legacy = RepoPaths.Resolve("src", "CadAI.Host.AutoCAD", "Windows", "Styles.xaml");
        Assert.False(File.Exists(legacy), "AutoCAD 旧版 Styles.xaml 已并入 ChatCore 主题，不应再出现。");

        var offenders = XamlFiles()
            .Where(path => File.ReadAllText(path).Contains("Windows/Styles.xaml", StringComparison.Ordinal))
            .Select(Relative)
            .ToList();
        Assert.True(offenders.Count == 0,
            "仍有窗口引用已删除的 Styles.xaml:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void AboutWindowsUseCenterScreenStartupLocation()
    {
        var aboutWindows = XamlFiles()
            .Where(path => Path.GetFileName(path).Equals("AboutWindow.xaml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(aboutWindows);
        foreach (string path in aboutWindows)
        {
            string content = File.ReadAllText(path);
            Assert.Contains("WindowStartupLocation=\"CenterScreen\"", content);
            Assert.DoesNotContain("WindowStartupLocation=\"CenterOwner\"", content);
        }
    }

    [Fact]
    public void MaterialSymbolsPathDataAreValid()
    {
        string symbolsFile = RepoPaths.Resolve("src", "CadAI.Core", "UI", "MaterialSymbols.cs");
        Assert.True(File.Exists(symbolsFile), "MaterialSymbols.cs 不存在");

        string code = File.ReadAllText(symbolsFile);
        var constMatches = Regex.Matches(code, @"public const string (\w+) = ""([^""]+)"";");
        var pathMatches = Regex.Matches(code, @"\[""([^""]+)""\] = ""([^""]+)"",");

        Assert.NotEmpty(constMatches);
        Assert.NotEmpty(pathMatches);

        var definedSymbols = constMatches.Select(m => m.Groups[2].Value).ToHashSet();
        var pathDict = pathMatches.ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        foreach (string symbol in definedSymbols)
        {
            Assert.True(pathDict.ContainsKey(symbol), $"MaterialSymbols 未收录常量 symbol: {symbol}");
            string path = pathDict[symbol];
            Assert.False(string.IsNullOrWhiteSpace(path), $"Symbol {symbol} 路径为空");
            Assert.True(path.StartsWith("M", StringComparison.OrdinalIgnoreCase), $"Symbol {symbol} 路径未以 M 开头: {path.Substring(0, Math.Min(10, path.Length))}");
            Assert.True(path.EndsWith("Z", StringComparison.OrdinalIgnoreCase), $"Symbol {symbol} 路径未以 Z 结尾");
        }
    }

    [Fact]
    public void DataGridColumnHeaderHasVerticalDividerStyle()
    {
        string themeXaml = RepoPaths.Resolve("src", "CadAI.Core", "UI", "Themes", "Theme.xaml");
        Assert.True(File.Exists(themeXaml), "Theme.xaml 不存在");

        string content = File.ReadAllText(themeXaml);
        Assert.Contains("PART_RightHeaderGripper", content);
        Assert.Contains("DataGridColumnHeader", content);
    }

    [Fact]
    public void AssistantAboutWindowsDescribeAssistantFeatures()
    {
        string autoCadAboutCs = RepoPaths.Resolve("src", "CadAI.Host.AutoCAD", "Windows", "AboutWindow.xaml.cs");

        Assert.True(File.Exists(autoCadAboutCs), autoCadAboutCs + " 不存在");
        string cadCode = File.ReadAllText(autoCadAboutCs);
        Assert.Contains("VersionInfo.License", cadCode);
        Assert.Contains("VersionInfo.Developer", cadCode);
    }

    private static IEnumerable<string> XamlFiles()
    {
        string src = RepoPaths.Resolve("src");
        return Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(@"\obj\") && !path.Contains(@"\bin\"));
    }

    private static Dictionary<string, string> Colors()
    {
        string path = RepoPaths.Resolve("src", "CadAI.Core", "UI", "Themes", "Colors.xaml");
        return XDocument.Load(path).Root!.Elements()
            .Where(e => e.Name.LocalName == "Color" && e.Attribute(X + "Key") != null)
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value);
    }

    private static string Relative(string path)
        => path.Substring(RepoPaths.FindRepoRoot().Length + 1);

    /// <summary>WCAG 2.x 相对亮度对比度。</summary>
    private static double Contrast(string foreground, string background)
    {
        double a = Luminance(foreground);
        double b = Luminance(background);
        double lighter = Math.Max(a, b);
        double darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(string hex)
    {
        string value = hex.TrimStart('#');
        if (value.Length == 8)
        {
            value = value.Substring(2);
        }

        static double Channel(string part)
        {
            double c = int.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(value.Substring(0, 2))
             + 0.7152 * Channel(value.Substring(2, 2))
             + 0.0722 * Channel(value.Substring(4, 2));
    }
}
