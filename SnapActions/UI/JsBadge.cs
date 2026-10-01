using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SnapActions.UI;

/// <summary>
/// 「这是自定义 JS 脚本动作」的统一标记。之所以用文字徽标而不是字形：Segoe Fluent Icons 没有
/// JavaScript 专属码点，唯一的 Code 字形（<see cref="IconGlyphs.Encode"/>）已被「编码/解码」类别占用，
/// 复用它只会让两类动作更混淆。
/// 工具栏固定区、工具栏子菜单与设置「固定在工具栏的动作」共用这一个工厂；颜色走主题资源引用，
/// 深浅主题自动跟随（各窗口不需要传入自己的画刷）。
/// </summary>
internal static class JsBadge
{
    /// <summary>设置列表行内的徽标（跟随内容尺寸，自带与名字之间的间距）。</summary>
    internal static Border CreateListItem()
    {
        var badge = Create(fontSize: 10, padding: new Thickness(4, 0, 4, 1));
        badge.Margin = new Thickness(0, 0, 6, 0);
        badge.VerticalAlignment = VerticalAlignment.Center;
        return badge;
    }

    /// <summary>工具栏图标位：调用方会把图标统一成 20×20，所以字号与左右内边距都更紧，避免文字被裁。</summary>
    internal static Border CreateToolbarIcon() => Create(fontSize: 9, padding: new Thickness(2, 0, 2, 1));

    private static Border Create(double fontSize, Thickness padding)
    {
        var text = new TextBlock
        {
            Text = "JS",
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var badge = new Border
        {
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding = padding,
            Child = text,
        };
        badge.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        badge.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        // 读屏用：徽标本身没有文字语义之外的说明。
        AutomationProperties.SetName(badge, "JS 脚本动作");
        return badge;
    }
}
