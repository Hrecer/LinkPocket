using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using LinkPocket.Services;

namespace LinkPocket.Views;

/// <summary>
/// 搜索结果行的 favicon：一个元素同时承担“图标”和“地球占位”。
/// 图标解码/磁盘读取在后台完成，首帧只画地球；模板虚拟化下每行只多一个绘制元素。
/// </summary>
public sealed class LpFaviconImage : FrameworkElement
{
    private static readonly Geometry Earth = Geometry.Parse(
        "M17.9,17.39C17.64,16.59 16.89,16 16,16H15V13A1,1 0 0,0 14,12H8V10H10A1,1 0 0,0 11,9V7H13A2,2 0 0,0 15,5V4.59" +
        "C17.93,5.77 20,8.64 20,12C20,14.08 19.2,15.97 17.9,17.39M11,19.93C7.05,19.44 4,16.08 4,12C4,11.38 4.08,10.78 4.21,10.21" +
        "L9,15V16A2,2 0 0,0 11,18M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z");

    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(LpFaviconImage),
        new PropertyMetadata(string.Empty, (d, _) => ((LpFaviconImage)d).Reload()));

    public string Url
    {
        get => (string)GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    private ImageSource? _icon;
    private int _generation;

    public LpFaviconImage()
    {
        Width = 18;
        Height = 18;
        Unloaded += (_, _) => Theming.ThemeService.Changed -= OnThemeChanged;
        Loaded += (_, _) =>
        {
            Theming.ThemeService.Changed -= OnThemeChanged;
            Theming.ThemeService.Changed += OnThemeChanged;
        };
    }

    private void OnThemeChanged(object? sender, Theming.Themes.ThemeDefinition e) => InvalidateVisual();

    private void Reload()
    {
        var generation = ++_generation;
        var url = Url;
        _icon = null;
        InvalidateVisual();
        if (string.IsNullOrWhiteSpace(url)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var loaded = FaviconService.LoadFromCache(url);
                if (loaded == null)
                {
                    await FaviconService.PrefetchAndCacheAsync(url);
                    loaded = FaviconService.LoadFromCache(url);
                }
                if (loaded == null) return;
                Dispatcher.Invoke(() =>
                {
                    if (generation != _generation || !string.Equals(Url, url, StringComparison.Ordinal)) return;
                    _icon = loaded;
                    InvalidateVisual();
                });
            }
            catch
            {
                // 图标加载失败：保留地球占位，不影响行内容
            }
        });
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsNaN(Width) ? 18 : Width, double.IsNaN(Height) ? 18 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        if (_icon is { } icon)
        {
            dc.DrawImage(icon, new Rect(0, 0, ActualWidth, ActualHeight));
            return;
        }

        if (TryFindResource("App.Text.Muted") is not Brush brush) return;
        dc.PushTransform(new ScaleTransform(ActualWidth / 24.0, ActualHeight / 24.0));
        dc.DrawGeometry(brush, null, Earth);
        dc.Pop();
    }
}
