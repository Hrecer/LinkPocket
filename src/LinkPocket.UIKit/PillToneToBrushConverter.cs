using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LinkPocket.ViewModels;

/// <summary>
/// 药丸色调 → 图标/文字强调色（唯一映射点）：同一套色调既驱动药丸样式（<see cref="PillToneToStyleConverter"/>），
/// 也驱动同一动作以**图标钮**呈现时的颜色（右栏动作面）——色调是单一事实源，外观不跟着页面复制。
/// 图标色按"工具栏同色"取：**还原 / 还原到根目录 = 定稿紫**（与工具栏「还原」主按钮同一填充色，用户定稿），
/// 破坏性动作 = 兼容层的奶油黄（T3 起改走次强调容器，与次操作共用呈现）。
/// ⚠️ 不取主题 `Primary`：它是亮紫（#6750A4），在 TintCard 上比定稿紫更刺眼，与工具栏按钮不同色
/// （用户报障"最左边那枚是亮紫色"）。也不取 `SecondaryContainer`：那是极浅的填充色，做描边在 TintCard 上几乎不可见。
/// </summary>
/// <remarks>
/// ⚠️ 这里的键名必须与 <c>LinkPocket.Theming.Tokens.AppTokens</c> 一致：T2 把旧画刷键
/// （<c>AccentBtn</c> / <c>WarnBg</c>）搬进了令牌体系，**代码里的字符串字面量不会跟着 XAML 一起被改名**——
/// 漏改就会让绑定静默找不到资源（<c>TryFindResource</c> 返回 null → Foreground 为 null → 图标不显色，
/// 实测被探针"右栏还原图标钮取色"断言抓到）。<c>ThemeRulesTests.禁止再引用旧画刷键名</c> 已把这类漏改机器化。
/// </remarks>
public sealed class PillToneToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            PillTone.Warn => Theming.Tokens.AppTokens.SupportOnContainer,
            _ => Theming.Tokens.AppTokens.AccentFill,
        };
        return Application.Current?.TryFindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
