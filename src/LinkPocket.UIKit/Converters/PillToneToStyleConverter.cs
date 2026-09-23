using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LinkPocket.ViewModels;

/// <summary>
/// 药丸色调 → UIKit 药丸样式（唯一映射点，样式仍只有一份、定义在 UIKit.xaml）：
/// 动作面各页只改能力位的 Tone，按钮外观不跟着页面复制。
/// </summary>
public sealed class PillToneToStyleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            PillTone.Tonal => "TonalButton",
            PillTone.Warn => "TonalButton",   // 破坏性动作与次操作共用同一套呈现（不再有专门警示色）
            _ => "PrimaryPillButton",
        };
        return Application.Current?.TryFindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
