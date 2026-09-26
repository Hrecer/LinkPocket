using System.Windows;
using System.Windows.Media;

namespace LinkPocket.Views;

/// <summary>
/// MD3 Expressive 波浪进度条（替换原生方形 ProgressBar）：
/// 已走部分 = 持续起伏的<b>波浪线</b>（Primary 色），剩余部分 = 直线轨道（浅色），
/// 进度点 = 竖向小滑标 —— 参考 MD3E wavy progress indicator 规范
/// （"Do: break from the surrounding shape style to draw attention to a particular element"）。
/// 相位经 CompositionTarget.Rendering 逐帧推进；仅在可见时渲染。
/// 用法（备份进度遮罩）：
/// <code>
/// &lt;views:WavyProgressBar ActiveBrush="{DynamicResource App.Accent.Fill}" TrackBrush="{DynamicResource App.Line.Variant}"/&gt;
/// </code>
/// </summary>
public class WavyProgressBar : FrameworkElement
{
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(WavyProgressBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(WavyProgressBar),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(WavyProgressBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActiveBrushProperty = DependencyProperty.Register(
        nameof(ActiveBrush), typeof(Brush), typeof(WavyProgressBar),
        new FrameworkPropertyMetadata(null, OnBrushChanged));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(WavyProgressBar),
        new FrameworkPropertyMetadata(null, OnBrushChanged));

    /// <summary>不确定态（进度未知）：忽略 <see cref="Value"/>，画一段**行进中的波浪**（MD3 indeterminate wavy）；
    /// 不给滑标（滑标表示确定位置）。用于"正在导入 / 正在导出"这类只知道在跑、不知道跑到哪的操作。</summary>
    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(WavyProgressBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>已走部分（波浪线 + 滑标）的颜色。</summary>
    public Brush? ActiveBrush
    {
        get => (Brush?)GetValue(ActiveBrushProperty);
        set => SetValue(ActiveBrushProperty, value);
    }

    /// <summary>剩余轨道（直线）的颜色。</summary>
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>不确定态：忽略 <see cref="Value"/>，画一段行进中的波浪（不给滑标）。</summary>
    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    /// <summary>不确定态里波浪段的长度（占轨道宽度的比例）。</summary>
    public double IndeterminateSpanRatio { get; set; } = 0.34;

    /// <summary>波长（px）：一个完整正弦周期的横向长度。</summary>
    public double WaveLength { get; set; } = 16.0;

    /// <summary>波幅（px）：波浪偏离中线的幅度。</summary>
    public double Amplitude { get; set; } = 3.2;

    /// <summary>线宽（px）。</summary>
    public double StrokeWidth { get; set; } = 2.5;

    /// <summary>进度点竖向滑标的高度（px）。</summary>
    public double ThumbHeight { get; set; } = 16.0;

    private const double PhaseSpeed = 5.5;   // 波浪行进速度（rad/s）

    /// <summary>
    /// 重绘的最小间隔（毫秒）：合成帧回调按显示器刷新率来（60/120/144Hz），
    /// 逐帧重绘会把 GPU 白烧在一条装饰性波浪上（见 <see cref="OnRendering"/> 注释）。
    /// 40ms = 25fps —— 缓慢起伏的波浪在这个帧率下与逐帧无可见差别。
    /// </summary>
    private const double MinFrameIntervalMs = 40;
    private const double SampleStep = 1.5;   // 波形采样步长（px）

    private double _phase;
    private TimeSpan? _lastRenderTime;
    private bool _hooked;
    private bool _pensDirty = true;
    private Pen? _activePen, _trackPen, _thumbPen;

    public WavyProgressBar()
    {
        Loaded += (_, _) => Hook(true);
        Unloaded += (_, _) => Hook(false);
    }

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((WavyProgressBar)d)._pensDirty = true;
    }

    private void Hook(bool on)
    {
        if (on && !_hooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _lastRenderTime = null;
            _hooked = true;
        }
        else if (!on && _hooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            // 隐藏期间持续刷新基准时间：恢复可见时相位不出现整段隐藏时长的跳变
            if (e is RenderingEventArgs hidden) _lastRenderTime = hidden.RenderingTime;
            return;
        }
        if (e is RenderingEventArgs re)
        {
            // ⚠️ **按帧节流**：CompositionTarget.Rendering 是合成帧回调（显示器多少 Hz 就来多少次，60/120/144…），
            // 每帧 InvalidateVisual 会让整窗按显示器刷新率持续重新合成——云模型场景下界面基本静止（只有这条波在动），
            // 于是 GPU 占用被这一条波吃满（用户实测 3D 占用 65% / 本进程 44%）。波浪是装饰性的缓慢起伏，
            // 25fps 已看不出差别；低于目标间隔的帧直接跳过（相位由**真实经过时间**推进，跳过帧不会让波变慢）。
            if (_lastRenderTime is { } last
                && (re.RenderingTime - last).TotalMilliseconds < MinFrameIntervalMs)
            {
                return;
            }
            if (_lastRenderTime.HasValue && re.RenderingTime > _lastRenderTime.Value)
                _phase += (re.RenderingTime - _lastRenderTime.Value).TotalSeconds * PhaseSpeed;
            _lastRenderTime = re.RenderingTime;
        }
        InvalidateVisual();   // 推进波形相位
    }

    private void EnsurePens()
    {
        if (!_pensDirty && _activePen != null) return;
        _activePen = new Pen(ActiveBrush ?? Brushes.Transparent, StrokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        _trackPen = new Pen(TrackBrush ?? Brushes.Transparent, StrokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        _thumbPen = new Pen(ActiveBrush ?? Brushes.Transparent, 3.0)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        _pensDirty = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        EnsurePens();
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0 || _activePen == null || _trackPen == null || _thumbPen == null) return;

        var cy = h / 2;

        // 不确定态：不画滑标、不看 Value —— 轨道铺满直线，上面跑一段波浪（行进 + 起伏都取自 _phase）
        if (IsIndeterminate)
        {
            dc.DrawLine(_trackPen, new Point(0, cy), new Point(w, cy));

            var span = Math.Max(StrokeWidth * 2, w * Math.Clamp(IndeterminateSpanRatio, 0.05, 1.0));
            // 行进周期 = 2 个起伏周期（避免"跑得太快像故障"，也别慢到看着停了）
            var travel = (_phase / (Math.PI * 4)) % 1.0;
            var head = travel * (w + span) - span;          // 从左侧屏外进、右侧屏外出
            var from = Math.Max(0.0, head);
            var to = Math.Min(w, head + span);
            if (to > from)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    var started = false;
                    for (var x = from; x <= to; x += SampleStep)
                    {
                        var y = cy + Amplitude * Math.Sin(x / WaveLength * 2 * Math.PI - _phase);
                        if (!started)
                        {
                            ctx.BeginFigure(new Point(x, y), false, false);
                            started = true;
                        }
                        else
                        {
                            ctx.LineTo(new Point(x, y), true, false);
                        }
                    }

                    if (started)
                    {
                        var yEnd = cy + Amplitude * Math.Sin(to / WaveLength * 2 * Math.PI - _phase);
                        ctx.LineTo(new Point(to, yEnd), true, false);
                    }
                }

                geometry.Freeze();
                dc.DrawGeometry(null, _activePen, geometry);
            }
            return;
        }

        var spanValue = Maximum - Minimum;
        var p = spanValue > 0 ? Math.Clamp((Value - Minimum) / spanValue, 0.0, 1.0) : 0.0;
        var px = Math.Max(StrokeWidth, p * (w - StrokeWidth));   // 滑标不贴边被裁

        // 剩余轨道：直线
        if (px < w - 1)
            dc.DrawLine(_trackPen, new Point(px, cy), new Point(w, cy));

        // 已走部分：正弦波浪线
        if (px > 0)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                var started = false;
                for (var x = 0.0; x < px; x += SampleStep)
                {
                    var y = cy + Amplitude * Math.Sin(x / WaveLength * 2 * Math.PI - _phase);
                    if (!started)
                    {
                        ctx.BeginFigure(new Point(0, y), false, false);
                        started = true;
                    }
                    else
                    {
                        ctx.LineTo(new Point(x, y), true, false);
                    }
                }

                var yEnd = cy + Amplitude * Math.Sin(px / WaveLength * 2 * Math.PI - _phase);
                if (!started) ctx.BeginFigure(new Point(0, yEnd), false, false);
                else ctx.LineTo(new Point(px, yEnd), true, false);
            }

            geo.Freeze();
            dc.DrawGeometry(null, _activePen, geo);
        }

        // 进度点：竖向小滑标
        dc.DrawLine(_thumbPen, new Point(px, cy - ThumbHeight / 2), new Point(px, cy + ThumbHeight / 2));
    }
}
