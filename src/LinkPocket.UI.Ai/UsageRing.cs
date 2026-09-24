using System.Windows;
using System.Windows.Media;

namespace LinkPocket.UI.Ai;

/// <summary>
/// 上下文占用环（输入区右端）：一条淡轨道 + 一段实进度弧，两段**同一个前景色**，
/// 只靠不透明度分主次（轨道 0.25 / 进度 0.7）——不额外引入第二种颜色，
/// 因此环在任何配色下都跟着所在容器（按钮）的前景色走。
/// <para>几何按 24 网格等比缩放：半径 10、描边 4、起点 -90°、圆头线帽；
/// 宿主只需给一个 14×14 的方框（与同排其它图标同高），不要为它单独放大尺寸。</para>
/// <para>只画"占了多少"这一件事——**完成不切绿、超限不换色**（与波浪进度条同口径）；
/// 读数来自 AI 运行时的只读读数，没有读数 = 空环（界面不自己估）。</para>
/// </summary>
public sealed class UsageRing : FrameworkElement
{
    /// <summary>参照实现的图标网格边长（viewBox 24）。</summary>
    private const double Grid = 24d;

    /// <summary>参照实现的轨道半径（网格单位）。</summary>
    private const double GridRadius = 10d;

    /// <summary>参照实现的描边宽度（网格单位）。</summary>
    private const double GridStroke = 4d;

    /// <summary>轨道不透明度。</summary>
    private const double TrackOpacity = 0.25d;

    /// <summary>进度弧不透明度。</summary>
    private const double FillOpacity = 0.7d;

    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>环的前景色（轨道与进度共用；通常绑定到所在按钮的前景 / 次级前景）。</summary>
    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
        nameof(IconBrush), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public Brush IconBrush
    {
        get => (Brush)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        const double fallback = 14d;   // 与同排图标的默认边长一致
        var width = double.IsNaN(Width) ? fallback : Width;
        var height = double.IsNaN(Height) ? fallback : Height;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        // 24 网格 → 实际边长：等比缩放半径与描边，环的粗细比例才与参照一致。
        var scale = size / Grid;
        var thickness = GridStroke * scale;
        var radius = GridRadius * scale;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        var track = new Pen(WithOpacity(IconBrush, TrackOpacity), thickness);
        dc.DrawEllipse(null, track, center, radius, radius);

        var percent = Math.Clamp(Percent, 0, 100);
        if (percent <= 0) return;

        var pen = new Pen(WithOpacity(IconBrush, FillOpacity), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        if (percent >= 100)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var sweep = percent / 100 * 360;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOnCircle(center, radius, -90), isFilled: false, isClosed: false);
            ctx.ArcTo(PointOnCircle(center, radius, -90 + sweep), new Size(radius, radius), 0,
                sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>取同一个前景色、换一个不透明度（画刷不可冻结时原样返回）。</summary>
    private static Brush WithOpacity(Brush brush, double opacity)
    {
        if (brush is null || brush == Brushes.Transparent) return Brushes.Transparent;
        if (brush is SolidColorBrush solid)
        {
            var faded = new SolidColorBrush(solid.Color) { Opacity = opacity };
            faded.Freeze();
            return faded;
        }

        // 非纯色画刷（渐变等）：退化为整体不透明度，不猜颜色。
        var clone = brush.Clone();
        clone.Opacity = opacity;
        if (clone.CanFreeze) clone.Freeze();
        return clone;
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
