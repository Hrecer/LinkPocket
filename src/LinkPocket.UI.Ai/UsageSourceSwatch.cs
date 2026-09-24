using System.Windows;
using System.Windows.Media;

namespace LinkPocket.UI.Ai;

/// <summary>
/// 分项列表前的小色块（与分段条同档色阶）：方角小块，悬浮面板里给"这一行对应哪一段"的视觉锚点。
/// </summary>
public sealed class UsageSourceSwatch : FrameworkElement
{
    public static readonly DependencyProperty ToneIndexProperty = DependencyProperty.Register(
        nameof(ToneIndex), typeof(int), typeof(UsageSourceSwatch),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>色阶档位（与同行的分段条一致）。</summary>
    public int ToneIndex
    {
        get => (int)GetValue(ToneIndexProperty);
        set => SetValue(ToneIndexProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var accent = UsageTone.Resolve(this, "App.Accent.Fill");
        var surface = UsageTone.Resolve(this, "App.Surface.Base");
        dc.DrawRectangle(UsageTone.Brush(ToneIndex, accent, surface), null,
            new Rect(0, 0, ActualWidth, ActualHeight));
    }
}
