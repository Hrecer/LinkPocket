using System;
using System.Globalization;
using System.Windows.Data;
using MaterialDesignThemes.Wpf;

namespace LinkPocket.ViewModels;

public class IdToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int id)
        {
            return id == 0 ? PackIconKind.BookmarkOutline : PackIconKind.FolderOutline;
        }
        return PackIconKind.FolderOutline;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
