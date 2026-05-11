using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinkPocket.ViewModels;

public class FaviconConverter : IValueConverter
{
    private static ImageSource? _defaultIcon;

    private static ImageSource DefaultIcon
    {
        get
        {
            if (_defaultIcon == null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.DecodePixelWidth = 16;
                bmp.DecodePixelHeight = 16;
                bmp.UriSource = new Uri("pack://application:,,,/Assets/default_favicon.png", UriKind.Absolute);
                try { bmp.EndInit(); } catch { }
                bmp.Freeze();
                _defaultIcon = bmp;
            }
            return _defaultIcon;
        }
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return DefaultIcon;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
