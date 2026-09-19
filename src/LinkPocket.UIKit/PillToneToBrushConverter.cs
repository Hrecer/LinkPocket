using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LinkPocket.ViewModels;

/// <summary>
/// 药丸色调 → 图标/文字强调色（唯一映射点）：同一套色调既驱动药丸样式（<see cref="PillToneToStyleConverter"/>），
/// 也驱动同一动作以**图标钮**呈现时的颜色（右栏动作面）——色调是单一事实源，外观不跟着页面复制。
/// 图标色按"工具栏同色"取：**还原 / 还原到根目录 = AccentBtn**（与工具栏「还原」主按钮同一填充色，用户定稿），
/// 删除 = WarnBg 奶油黄（与工具栏「永久删除」同一填充色）。
/// ⚠️ 不取主题 `Primary`：它是亮紫（#6750A4），在 TintCard 上比 AccentBtn 更刺眼，与工具栏按钮不同色
/// （用户报障"最左边那枚是亮紫色"）。也不取 `SecondaryContainer`：那是极浅的填充色，做描边在 TintCard 上几乎不可见。
/// </summary>
public sealed class PillToneToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            PillTone.Warn => "WarnBg",
            _ => "AccentBtn",
        };
        return Application.Current?.TryFindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
