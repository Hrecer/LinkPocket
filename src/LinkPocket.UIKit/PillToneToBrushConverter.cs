using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LinkPocket.ViewModels;

/// <summary>
/// 药丸色调 → 图标/文字强调色（唯一映射点）：同一套色调既驱动药丸样式（<see cref="PillToneToStyleConverter"/>），
/// 也驱动同一动作以**图标钮**呈现时的颜色（右栏动作面）——色调是单一事实源，外观不跟着页面复制。
/// 图标档位按"强调度"取色：Primary = 主题 Primary（深紫）＞ Tonal = AccentBtn（浅紫）＞ 无。
/// ⚠️ 与药丸档位不共用色键是有意的：药丸的 Primary 外观是 AccentBtn **填充**（白字），
/// 图标没有填充，直接画同一色会显得比 Tonal 更淡，主次就反了。
/// </summary>
public sealed class PillToneToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            PillTone.Tonal => "AccentBtn",
            PillTone.Warn => "WarnBg",
            _ => "Primary",
        };
        return Application.Current?.TryFindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
