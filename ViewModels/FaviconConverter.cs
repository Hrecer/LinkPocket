using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace LinkPocket.ViewModels;

public class FaviconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string url && !string.IsNullOrEmpty(url))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.DecodePixelWidth = 48;
                bmp.UriSource = new Uri(url, UriKind.Absolute);
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
