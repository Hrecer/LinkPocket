using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace LinkPocket.UI.Ai;

/// <summary>
/// 上下文占用条（悬浮面板顶部）：按分项占比把一整条画成若干段，颜色取同一强调色的五档深浅。
/// <para><b>为什么一次性画整条而不是每段一个控件</b>：段宽依赖"整条宽度"，用等宽格子 + 段内百分比
/// 会算成"占本格"的错口径（段与段相互挤压没法定量）；直接在这里按整条宽度分段才与读数一致。</para>
/// <para><see cref="Segments"/> 只接 <c>AiContextSourceRow</c> 集合（<c>Percent</c> 0–100 / <c>ToneIndex</c>）。</para>
/// </summary>
public sealed class UsageSegmentStrip : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable), typeof(UsageSegmentStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSegmentsChanged));

    /// <summary>分项行集合（按占比降序，占比之和 ≈ 100）。</summary>
    public IEnumerable? Segments
    {
        get => (IEnumerable?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var strip = (UsageSegmentStrip)d;
        // 集合换了（或换成同一实例但重填过）→ 重画；VM 每次刷新都给新 List，够用。
        if (e.OldValue is INotifyCollectionChanged oldNotify) oldNotify.CollectionChanged -= strip.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNotify) newNotify.CollectionChanged += strip.OnCollectionChanged;
        strip.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var rows = Segments?.OfType<AiContextSourceRow>().ToList();
        if (rows is not { Count: > 0 }) return;

        var accent = UsageTone.Resolve(this, "App.Accent.Fill");
        var surface = UsageTone.Resolve(this, "App.Surface.Base");

        var x = 0.0;
        var remaining = ActualWidth;
        for (var i = 0; i < rows.Count; i++)
        {
            var percent = Math.Clamp(rows[i].Percent, 0, 100);
            var width = i == rows.Count - 1
                ? remaining                                     // 末段吃掉舍入余量，右端不留缝
                : Math.Max(2, percent / 100.0 * ActualWidth);   // 极小段留 2px，别抹成看不见
            width = Math.Min(width, remaining);
            if (width <= 0) break;

            dc.DrawRectangle(UsageTone.Brush(rows[i].ToneIndex, accent, surface), null,
                new Rect(x, 0, width, ActualHeight));
            x += width;
            remaining -= width;
        }
    }
}
