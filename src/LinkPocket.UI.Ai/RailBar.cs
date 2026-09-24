using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LinkPocket.UI.Ai;

/// <summary>
/// 导航轨上的一根短横条。峰值推移**不用布局动画**：条的占位宽度恒为 <see cref="AiRailVisual.PeakWidth"/>，
/// 画出来的那一段只由 <see cref="StrokeScale"/>（横向倍数，<c>transform-origin: left</c>）与
/// <see cref="StrokeOpacity"/> 控制——布局宽度一变，整条轨的居中位置就会跟着抖，
/// 那正是"不丝滑"的成因。
/// </summary>
public sealed class RailBar : FrameworkElement
{
    /// <summary>横向倍数（1 = 静止宽度 12，2.6 = 峰值 ≈ 31.2）。</summary>
    public static readonly DependencyProperty StrokeScaleProperty =
        DependencyProperty.Register(nameof(StrokeScale), typeof(double), typeof(RailBar),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>条的不透明度（峰顶 1、其余一律 0.58；当前视口那根 0.9）。</summary>
    public static readonly DependencyProperty StrokeOpacityProperty =
        DependencyProperty.Register(nameof(StrokeOpacity), typeof(double), typeof(RailBar),
            new FrameworkPropertyMetadata(AiRailVisual.MutedOpacity, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>当前视口所在轮：走前景色（深）、不透明度 0.9。</summary>
    public static readonly DependencyProperty IsCurrentProperty =
        DependencyProperty.Register(nameof(IsCurrent), typeof(bool), typeof(RailBar),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// 悬浮山峰是否正在生效。生效时**当前轮不再取特殊色**——参照实现里
    /// <c>showScrollActiveColor</c> 只在没有悬浮焦点时才为真，否则悬浮那一根与"当前轮"两根会同时抢注意力。
    /// </summary>
    public static readonly DependencyProperty IsRailHoveredProperty =
        DependencyProperty.Register(nameof(IsRailHovered), typeof(bool), typeof(RailBar),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>条颜色（静止态 / 非当前轮 / 无悬浮焦点时的当前轮）。</summary>
    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(RailBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>前景强调色（当前轮 / 悬浮峰顶）。</summary>
    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(RailBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double StrokeScale
    {
        get => (double)GetValue(StrokeScaleProperty);
        set => SetValue(StrokeScaleProperty, value);
    }

    public double StrokeOpacity
    {
        get => (double)GetValue(StrokeOpacityProperty);
        set => SetValue(StrokeOpacityProperty, value);
    }

    public bool IsCurrent
    {
        get => (bool)GetValue(IsCurrentProperty);
        set => SetValue(IsCurrentProperty, value);
    }

    public bool IsRailHovered
    {
        get => (bool)GetValue(IsRailHoveredProperty);
        set => SetValue(IsRailHoveredProperty, value);
    }

    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    /// <summary>占位恒为峰值宽：任何档位都不改变轨的布局（居中不抖）。</summary>
    protected override Size MeasureOverride(Size availableSize)
        => new(AiRailVisual.PeakWidth, AiRailVisual.BarHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var scale = StrokeScale;
        if (double.IsNaN(scale) || scale <= 0) scale = 1;
        var maxScale = AiRailVisual.PeakWidth / AiRailVisual.BarWidth;
        if (scale > maxScale) scale = maxScale;   // 夹回占位宽以内，避免溢出被裁成平头

        var isPeak = scale > 1.01;                       // 只有峰与相邻 / 次相邻会变宽
        var showCurrent = IsCurrent && !IsRailHovered;   // 悬浮期间当前轮让位
        var brush = isPeak || showCurrent ? (AccentBrush ?? BarBrush) : BarBrush;
        if (brush is null) return;

        var opacity = showCurrent ? AiRailVisual.CurrentOpacity
            : isPeak ? AiRailVisual.PeakOpacity
            : StrokeOpacity;

        var width = AiRailVisual.BarWidth * scale;
        var radius = AiRailVisual.BarHeight / 2;
        dc.PushOpacity(Math.Clamp(opacity, 0, 1));
        // 左对齐 + 圆头（origin-left + scaleX）：峰从左侧"长出来"，视觉锚点落在轨道边线上
        dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, width, AiRailVisual.BarHeight), radius, radius);
        dc.Pop();
    }

    /// <summary>把横向倍数与不透明度一起推移到目标档位（150ms、缓出）。</summary>
    public void AnimateTo(double scale, double opacity, bool animate)
    {
        if (!animate)
        {
            BeginAnimation(StrokeScaleProperty, null);
            BeginAnimation(StrokeOpacityProperty, null);
            StrokeScale = scale;
            StrokeOpacity = opacity;
            return;
        }
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var ms = AiRailVisual.TravelMs;
        BeginAnimation(StrokeScaleProperty, new DoubleAnimation(scale, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        BeginAnimation(StrokeOpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
    }
}
