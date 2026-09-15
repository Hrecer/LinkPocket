using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LinkPocket.Views.Browser;

/// <summary>bool → Visibility 反转转换器：true → Collapsed，false → Visible。</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}
