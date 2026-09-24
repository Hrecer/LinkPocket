using System.Windows;
using System.Windows.Media;

namespace LinkPocket.UI.Ai;

/// <summary>
/// 上下文占用环（输入区右端）：一条轨道 + 一段进度弧。
/// 只画"占了多少"这一件事——**完成不切绿、超限不换色**（与波浪进度条同口径）；
/// 读数来自 AI 运行时的只读读数，没有读数 = 空环（界面不自己估）。
/// </summary>
public sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsNaN(Width) ? 26 : Width;
        var height = double.IsNaN(Height) ? 26 : Height;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var thickness = Math.Min(RingThickness, size / 3);
        var radius = (size - thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(TrackBrush, thickness), center, radius, radius);

        var percent = Math.Clamp(Percent, 0, 100);
        if (percent <= 0) return;

        var pen = new Pen(FillBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
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

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
