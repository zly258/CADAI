using System.Xml.Linq;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// 主题「默认前景色」契约。
///
/// 防的是 2026-10-16 那次事故：`Theme.xaml` 的全局隐式 <c>Style TargetType="TextBlock"</c>
/// 把 <c>Foreground</c> 硬写成 <c>Brush.Text.Primary</c>。隐式样式（Setter）优先级**高于**属性继承，
/// 而 <c>ContentPresenter</c> 为字符串内容自动生成的 <c>TextBlock</c>/<c>AccessText</c> 正好会被这条
/// 全局样式命中，于是控件自己声明的前景被静默盖成正文深灰：主按钮（<c>Button.Primary</c>）
/// 从「蓝底白字」变成「蓝底深灰字」，危险按钮的红字、状态标签的语义色、选中页签的 Accent 色一并失效。
///
/// 修法是把默认前景改成「就近内容宿主优先」：绑定 <c>(TextElement.Foreground)</c>（<c>Control.Foreground</c>
/// 就是它，会继承进 <c>ContentPresenter</c>），没有内容宿主时才回退正文色。
/// 谁把这一行改回静态色值，主按钮就会再次变脏——所以钉在这里。
/// </summary>
public sealed class ThemeForegroundTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string ThemeXaml => RepoPaths.Resolve("src", "CadAI.Core", "UI", "Themes", "Theme.xaml");
    private static string ButtonsXaml => RepoPaths.Resolve("src", "CadAI.Core", "UI", "Themes", "Buttons.xaml");

    [Fact]
    public void GlobalTextBlockStyle_ForegroundFollowsContentHostInsteadOfHardcodedBrush()
    {
        string foreground = ImplicitStyleSetterValue(ThemeXaml, "TextBlock", "Foreground");

        Assert.Contains("(TextElement.Foreground)", foreground);
        Assert.Contains("FallbackValue=", foreground);
    }

    [Fact]
    public void GlobalAccessTextStyle_ForegroundFollowsContentHostInsteadOfHardcodedBrush()
    {
        // RecognizesAccessKey=True 的 ContentPresenter 生成的是 AccessText：它不受 TextBlock
        // 隐式样式约束，所以要有一条同样规则的 AccessText 隐式样式（否则回到主题默认深灰）。
        string foreground = ImplicitStyleSetterValue(ThemeXaml, "AccessText", "Foreground");

        Assert.Contains("(TextElement.Foreground)", foreground);
        Assert.Contains("FallbackValue=", foreground);
    }

    [Fact]
    public void PrimaryButtonStyle_IsWhiteOnAccent()
    {
        // 白字走 Brush.OnAccent token，业务与主题里不再出现字面量 White。
        Assert.Equal("{StaticResource Brush.OnAccent}", KeyedStyleSetterValue(ButtonsXaml, "Button.Primary", "Foreground"));
        Assert.Equal("{StaticResource Brush.Accent}", KeyedStyleSetterValue(ButtonsXaml, "Button.Primary", "Background"));
    }

    [Fact]
    public void TextPrimaryBrush_IsTheDeepNavyTextColor()
    {
        // 回退色必须仍是正文色（Brush.Text.Primary → Color.TextPrimary），
        // 否则普通 TextBlock 会掉到 WPF 默认黑。
        string colorsXaml = RepoPaths.Resolve("src", "CadAI.Core", "UI", "Themes", "Colors.xaml");
        string color = Brushes(colorsXaml)["Brush.Text.Primary"];

        Assert.Equal("{StaticResource Color.TextPrimary}", color);
    }

    private static Dictionary<string, string> Brushes(string xamlPath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in Load(xamlPath).Root!.Elements())
        {
            var key = (string?)e.Attribute(X + "Key");
            var color = (string?)e.Attribute("Color");
            if (key != null && color != null) map[key] = color;
        }

        return map;
    }

    private static string ImplicitStyleSetterValue(string xamlPath, string targetType, string property)
    {
        var style = Load(xamlPath).Root!.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Style"
            && (string?)e.Attribute("TargetType") == targetType
            && e.Attribute(X + "Key") == null);
        Assert.True(style != null, $"{xamlPath}: 找不到 TargetType=\"{targetType}\" 的隐式样式");

        return SetterValue(style!, property, $"{targetType} 隐式样式");
    }

    private static string KeyedStyleSetterValue(string xamlPath, string styleKey, string property)
    {
        var style = Load(xamlPath).Root!.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Style"
            && (string?)e.Attribute(X + "Key") == styleKey);
        Assert.True(style != null, $"{xamlPath}: 找不到样式 x:Key=\"{styleKey}\"");

        return SetterValue(style!, property, styleKey);
    }

    private static string SetterValue(XElement style, string property, string label)
    {
        var setter = style.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Setter"
            && (string?)e.Attribute("Property") == property);
        Assert.True(setter != null, $"{label}: 缺少 {property} 的 Setter");

        return (string?)setter!.Attribute("Value") ?? string.Empty;
    }

    private static XDocument Load(string path) => XDocument.Load(path);
}
