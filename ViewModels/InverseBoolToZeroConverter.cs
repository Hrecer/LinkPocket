using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LinkPocket.ViewModels;

public class InverseBoolToZeroConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int id)
        {
            // 所有项目都应该可交互（包括Id=0的"全部书签"）
            return true;
        }
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
