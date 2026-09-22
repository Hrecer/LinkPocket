using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinkPocket.Theming;
using LinkPocket.Theming.Color;
using LinkPocket.Theming.Themes;
using LinkPocket.Theming.Tokens;
using Material3.Core;
using LinkPocket.I18n;
using LinkPocket.UIKit;

namespace LinkPocket.Views;

/// <summary>
/// 自研取色盘（方案 §7.3）：**零第三方依赖**，SV 方块 + H 竖滑条 + HEX 输入 + 对比度体检。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么自研</b>：本仓纪律是不引第三方颜色/主题/取色库（<c>Material3.Core</c> 的 HCT 原语够用）。
/// 取色盘只是一块渐变矩形 + 一个色相条 + 鼠标位置换算，没有必要为此引依赖。
/// </para>
/// <para>
/// <b>HSV 而不是 HCT</b>：取色盘是**用户操作面**，需要"二维直觉"（上下=明度、左右=饱和度）——
/// HSV 的 V/S 恰好对应这两个手势。HCT 的 V 不是这样的二维结构，做不出方块。
/// 故这里用标准 HSV↔RGB 换算，而**主题派生**仍走 HCT（两者各司其职）。
/// </para>
/// <para>
/// <b>对比度体检</b>：把当前色当"强调填充"（对白字）与"强调图标"（对卡面）实时算 WCAG，
/// 让用户在选色时就看见后果——这是"任何色相都能用"这套体系的可用性落点。
/// </para>
/// <para>
/// <b>边界</b>：不做屏幕吸管（避免引入本机截图依赖）；不做透明度（主题色全部不透明）。
/// </para>
/// </remarks>
public partial class ColorPickerPopup : UserControl
{
    // ── 当前编辑态（草稿）──────────────────────────────────────────────
    private double _hue;          // 0–360
    private double _saturation;   // 0–1
    private double _value;        // 0–1
    private bool _suppressHex;    // 防止"程序改文本 → TextChanged → 再改色"的回环

    /// <summary>颜色已确认（事件载荷 = 确认的颜色）。</summary>
    public event EventHandler<Color>? ColorConfirmed;

    /// <summary>
    /// 用户要把**该槽清回空槽**（不是设成黑色）。
    /// </summary>
    /// <remarks>
    /// 空槽在本仓是一个**真实的语义**（虚线空心环 + 「+」+「未选」，见 <c>ColorSlotViewModel</c>），
    /// 因此清除必须是"移除颜色"而不是"换一个颜色"；由宿主（外观面板）把它落到
    /// <c>AppearanceViewModel.ClearSlot</c>——与改色走同一条草稿路径。
    /// </remarks>
    public event EventHandler? Cleared;

    /// <summary>用户取消了本次取色（不改调用方的值）。</summary>
    public event EventHandler? Cancelled;

    public ColorPickerPopup()
    {
        InitializeComponent();

        // 令牌色 → Freezable 子属性（**只能在 code-behind 赋**，根因见 ApplyTokenColors）。
        // 先赋一次：XAML 里没有这些值，不设就是"黑投影 / 空心圆点"的错样子。
        ApplyTokenColors();

        // 两个 SV 叠加层同理：不在这里建，控件在 Open() 之前是两块**空 Fill** 的矩形
        // （面板一构造出来就摆在那里，用户看得见 —— 不能等到第一次取色才有渐变）。
        ApplySvGradients();

        // 取色盘打开期间主题可能被切换（外观面板允许实时预览）→ 重新取一次令牌值。
        // 为什么用事件而不是资源引用：这三个属性是 Color 型（Freezable 子属性），吃不了资源引用。
        ThemeService.Changed += OnThemeChanged;

        IsVisibleChanged += (_, e) =>
        {
            // 与 InlineNameEditor / BreadcrumbBar 同源：聚焦这类"作用于可视状态"的动作要挂在可见性上，
            // 不能赌属性通知与可视状态的落地时序（WARNINGS 43）。
            if (e.NewValue is true)
            {
                // 上次打开之后可能换过主题 → 每次显示都重新对齐令牌色（覆盖式，不留旧值）
                ApplyTokenColors();
                HexBox.Focus();
                HexBox.SelectAll();
            }
        };
    }

    private void OnThemeChanged(object? sender, ThemeDefinition definition)
    {
        ApplyTokenColors();
        // 主题变了，叠加渐变的端点色也跟着变（Fill 上挂的是普通画刷，不吃资源引用 —— 重建一次）
        ApplySvGradients();
    }

    /// <summary>
    /// 把三个 **Color 型令牌** 的当前值赋给对应的 Freezable 子属性。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须在这里赋值，而不是在 XAML 里写资源引用（根因，实测）</b>：
    /// WPF 的 <c>Color</c> 型属性（<c>GradientStop.Color</c> / <c>DropShadowEffect.Color</c>）
    /// **既不接受 <c>StaticResource</c> 也不接受 <c>DynamicResource</c>**：
    /// </para>
    /// <list type="bullet">
    /// <item><c>StaticResource</c>：BAML 加载时把键名当字面字符串塞进 setter —— 实测
    /// <c>XamlParseException</c>：「"#FFFFFFFF" 不是属性 "Color" 的有效值」；</item>
    /// <item><c>DynamicResource</c>：求值出来的是**资源对象本身**（<c>SolidColorBrush</c>），
    /// 属性系统不做 Brush→Color 转换 —— 实测同一个 <c>XamlParseException</c>，
    /// 抛在 <c>ColorPickerPopup.xaml:36</c> 的 <c>GradientStop.Color</c> 上。</item>
    /// </list>
    /// <para>
    /// 换句话说：**这不是"令牌发布早了还是晚了"的问题** —— 令牌此刻已经发布（同一文件里的
    /// <c>Border.Background</c> 引同一个键完全正常）。真正的原因是**这两种属性介质不吃资源引用**。
    /// 界面层唯一干净的形态 = 在加载期之后（构造函数）**直接取令牌值**赋给它们。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="FrameworkElement.FindResource(string)"/> 而不是 TryFindResource + 兜底色</b>：
    /// 取不到令牌 = "主题尚未装配"这一真实故障，必须当场暴露（观测面纪律：禁止静默兜底）；
    /// 兜一个写死的白色会让故障表现成"一张看起来正常、实际不受主题控制的取色盘"。
    /// 宿主（<c>App.OnStartup</c> / 探针）都保证令牌在构建窗口之前发布。
    /// </para>
    /// </remarks>
    private void ApplyTokenColors()
    {
        var onAccent = TokenColor(AppTokens.TextOnAccent);
        var shadow = TokenColor(AppTokens.ShadowColor);

        ShellShadow.Color = shadow;
        KnobShadow.Color = shadow;
        SvKnob.Stroke = new SolidColorBrush(onAccent);
        HueKnob.Fill = new SolidColorBrush(onAccent);
    }

    /// <summary>取一个 **Color 型**令牌的当前值（取不到即抛，不兜底）。</summary>
    private Color TokenColor(string token) => FindResource(token) switch
    {
        Color c => c,
        SolidColorBrush b => b.Color,
        _ => throw new InvalidOperationException(
            $"token '{token}' is not published or is not a colour value -- the theme is not assembled yet (the host must call ThemeService before building windows)"),
    };

    /// <summary>当前颜色（草稿）。</summary>
    public Color Current => ColorMath.ToMedia(ColorMath.FromHsv(_hue, _saturation, _value));

    /// <summary>进入取色盘并载入一个初值。</summary>
    public void Open(Color initial)
    {
        SetFromColor(initial);
        UpdateVisuals();
    }

    /// <summary>用外部颜色重置草稿（不改事件）。</summary>
    public void SetFromColor(Color c)
    {
        ColorMath.ToHsv(Argb.FromArgb(255, c.R, c.G, c.B), out _hue, out _saturation, out _value);
        SyncHexText();
        UpdateVisuals();
    }

    // ── 交互：SV 方块与色相条 ─────────────────────────────────────────

    private void SvHost_MouseDown(object sender, MouseButtonEventArgs e) => PickSv(e.GetPosition(SvHost), capture: true);

    private void SvHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) PickSv(e.GetPosition(SvHost), capture: false);
    }

    private void PickSv(Point p, bool capture)
    {
        var w = Math.Max(1.0, SvHost.ActualWidth);
        var h = Math.Max(1.0, SvHost.ActualHeight);
        _saturation = Math.Clamp(p.X / w, 0, 1);
        _value = Math.Clamp(1 - p.Y / h, 0, 1);
        if (capture) SvHost.CaptureMouse();
        else if (Mouse.LeftButton == MouseButtonState.Released) SvHost.ReleaseMouseCapture();
        SyncHexText();
        UpdateVisuals();
    }

    private void SvHost_MouseUp(object sender, MouseButtonEventArgs e) => SvHost.ReleaseMouseCapture();

    private void HueHost_MouseDown(object sender, MouseButtonEventArgs e) => PickHue(e.GetPosition(HueHost), capture: true);

    private void HueHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) PickHue(e.GetPosition(HueHost), capture: false);
    }

    private void PickHue(Point p, bool capture)
    {
        var h = Math.Max(1.0, HueHost.ActualHeight);
        _hue = Math.Clamp(p.Y / h, 0, 1) * 360.0;
        if (capture) HueHost.CaptureMouse();
        else if (Mouse.LeftButton == MouseButtonState.Released) HueHost.ReleaseMouseCapture();
        SyncHexText();
        UpdateVisuals();
    }

    private void HueHost_MouseUp(object sender, MouseButtonEventArgs e) => HueHost.ReleaseMouseCapture();

    // ── HEX 输入 ─────────────────────────────────────────────────────

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressHex) return;
        if (ThemeValidator.TryParseHex(HexBox.Text, out var argb))
        {
            // Argb → WPF Color 的唯一转换点在 Theming（ColorMath.ToMedia）
            SetFromColor(ColorMath.ToMedia(argb));
            SetHexError(false, null);
        }
        else
        {
            // 非法输入：文字区出 2px 描边 + 文案，**不猜不改**（零兼容口径）。
            // 注意"全站不使用红色"——校验错误描边 = 文字主色。
            SetHexError(true, Loc.K("picker.hexHint"));
        }
    }

    private void SetHexError(bool invalid, LocValue? message)
    {
        HexFieldShell.BorderThickness = invalid ? new Thickness(0, 0, 0, 2) : new Thickness(0);
        // 校验描边走**资源引用**（本文件里唯一一处 Brush 型取值；其余是 Color 型 Freezable 子属性，
        // 吃不了资源引用，见 ApplyTokenColors）：一次性取画刷赋值会在换主题后停在旧主题。
        HexFieldShell.SetResourceReference(Border.BorderBrushProperty, AppTokens.LineInvalid);
        HexErrorText.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        HexErrorText.SetText(message ?? LocValue.Empty);
    }

    private void SyncHexText()
    {
        _suppressHex = true;
        try { HexBox.Text = ThemeValidator.ToHex(Argb.FromArgb(255, Current.R, Current.G, Current.B)); }
        finally { _suppressHex = false; }
    }

    // ── 视觉刷新 ─────────────────────────────────────────────────────

    /// <summary>两个 SV 叠加渐变（饱和 / 明度）。值来自令牌，故每次都重建（主题可能已变）。</summary>
    private LinearGradientBrush? _saturationGradient;
    private LinearGradientBrush? _valueGradient;

    private void UpdateVisuals()
    {
        var current = Current;

        // SV 方块：底色 = 当前色相纯色；上面叠两个渐变层（饱和 / 明度）
        SvHost.Background = new SolidColorBrush(ColorMath.ToMedia(ColorMath.FromHsv(_hue, 1, 1)));
        ApplySvGradients();

        var w = Math.Max(1.0, SvHost.ActualWidth);
        var h = Math.Max(1.0, SvHost.ActualHeight);
        SvKnob.Margin = new Thickness(
            Math.Clamp(_saturation * w - 7, 0, Math.Max(0, w - 14)),
            Math.Clamp((1 - _value) * h - 7, 0, Math.Max(0, h - 14)), 0, 0);

        var hh = Math.Max(1.0, HueHost.ActualHeight);
        HueKnob.Margin = new Thickness(0, Math.Clamp(_hue / 360.0 * hh - 1, 0, Math.Max(0, hh - 3)), 0, 0);

        PreviewSwatch.Background = new SolidColorBrush(current);

        // 对比度体检：把当前色当"强调填充"（对白字）/ 当"强调图标"（对卡面）
        var argb = Argb.FromArgb(255, current.R, current.G, current.B);
        var white = Argb.FromArgb(255, 255, 255, 255);
        var card = ColorMath.FromMedia(TokenColor(AppTokens.SurfaceCard));
        var asFill = ColorMath.ContrastRatio(argb, white);
        var asIcon = ColorMath.ContrastRatio(argb, card);

        // 数字格式一律 Invariant（小数点是 "."，不随语言变）：进程 culture 只为排序钉在 zh-CN，
        // 但"数字跟着环境走"会让同一个对比度读数在不同机器上长得不一样。
        ContrastOnFillText.SetText(Loc.K(asFill >= 4.5 ? "picker.onFillPass" : "picker.onFillFail", asFill.ToString("F2", CultureInfo.InvariantCulture)));
        ContrastAsIconText.SetText(Loc.K(asIcon >= 3.0 ? "picker.asIconPass" : "picker.asIconFail", asIcon.ToString("F2", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// 构建两个叠加渐变（**在 code-behind 而不在 XAML**）。
    /// </summary>
    /// <remarks>
    /// 根因同 <see cref="ApplyTokenColors"/>：这三个值都是 <c>Color</c>，而
    /// <c>GradientStop.Color</c> 不吃资源引用（实测 BAML 加载期抛 <c>XamlParseException</c>）。
    /// 运行时用已取到的 <see cref="Color"/> 直接建画刷；每次都重建（令牌可能随主题变化）。
    /// </remarks>
    private void ApplySvGradients()
    {
        var white = TokenColor(AppTokens.TextOnAccent);
        var black = TokenColor(AppTokens.ShadowColor);
        var clear = TokenColor(AppTokens.SvTransparent);

        _saturationGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
        };
        _saturationGradient.GradientStops.Add(new GradientStop(white, 0));
        _saturationGradient.GradientStops.Add(new GradientStop(clear, 1));

        _valueGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        _valueGradient.GradientStops.Add(new GradientStop(clear, 0));
        _valueGradient.GradientStops.Add(new GradientStop(black, 1));

        SaturationLayer.Fill = _saturationGradient;
        ValueLayer.Fill = _valueGradient;
    }

    // ── 按钮 ─────────────────────────────────────────────────────────

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);

    /// <summary>清除 = 把该槽清回**空槽**（不校验 HEX：清除与"当前 HEX 是否合法"无关）。</summary>
    private void ClearBtn_Click(object sender, RoutedEventArgs e) => Cleared?.Invoke(this, EventArgs.Empty);

    private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ThemeValidator.TryParseHex(HexBox.Text, out _))
        {
            SetHexError(true, Loc.K("picker.hexInvalid"));
            return;
        }
        ColorConfirmed?.Invoke(this, Current);
    }
}
