using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LinkPocket.ViewModels;

/// <summary>
/// 高度 → 圆角（= 高度的一半）：用于「药丸 / 胶囊」形卡片——内容高度变化时仍保持胶囊形
/// （写死圆角会在内容变高时退化成圆角矩形）。
/// </summary>
public sealed class HalfCornerRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double h && h > 0 ? new CornerRadius(h / 2) : new CornerRadius(0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
