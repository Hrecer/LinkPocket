using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinkPocket.Theming.Color;
using LinkPocket.Theming.Themes;
using Material3.Core;

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

    /// <summary>用户确认了一个颜色。</summary>
    public event EventHandler<Color>? ColorConfirmed;

    /// <summary>用户取消了本次取色（不改调用方的值）。</summary>
    public event EventHandler? Cancelled;

    public ColorPickerPopup()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            // 与 InlineNameEditor / BreadcrumbBar 同源：聚焦这类"作用于可视状态"的动作要挂在可见性上，
            // 不能赌属性通知与可视状态的落地时序（WARNINGS 43）。
            if (e.NewValue is true)
            {
                HexBox.Focus();
                HexBox.SelectAll();
            }
        };
    }

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
            SetHexError(true, "请输入 #RRGGBB / RRGGBB / #RGB");
        }
    }

    private void SetHexError(bool invalid, string? message)
    {
        HexFieldShell.BorderThickness = invalid ? new Thickness(0, 0, 0, 2) : new Thickness(0);
        HexFieldShell.BorderBrush = (Brush)FindResource("App.Line.Invalid");
        HexErrorText.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        HexErrorText.Text = message ?? string.Empty;
    }

    private void SyncHexText()
    {
        _suppressHex = true;
        try { HexBox.Text = ThemeValidator.ToHex(Argb.FromArgb(255, Current.R, Current.G, Current.B)); }
        finally { _suppressHex = false; }
    }

    // ── 视觉刷新 ─────────────────────────────────────────────────────

    private void UpdateVisuals()
    {
        var current = Current;

        // SV 方块的底色 = 当前色相的纯色（白→透明 横渐变叠饱和；透明→黑 竖渐变叠明度）
        SvHost.Background = new SolidColorBrush(ColorMath.ToMedia(ColorMath.FromHsv(_hue, 1, 1)));

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
        var cardColor = ResolveToken(AppTokensLocal.SurfaceCard, CardFallback);
        var card = Argb.FromArgb(255, cardColor.R, cardColor.G, cardColor.B);
        var asFill = ColorMath.ContrastRatio(argb, white);
        var asIcon = ColorMath.ContrastRatio(argb, card);

        var s = CultureInfo.CurrentCulture;
        ContrastOnFillText.Text = $"当主按钮底（配白字）：{asFill.ToString("F2", s)}  {(asFill >= 4.5 ? "✓ 达标" : "✗ 需 4.5")}";
        ContrastAsIconText.Text = $"当图标色（配卡面）：{asIcon.ToString("F2", s)}  {(asIcon >= 3.0 ? "✓ 达标" : "✗ 需 3.0")}";
    }

    /// <summary>
    /// 卡面兜底色（主题未装配时的回落）。
    /// </summary>
    /// <remarks>
    /// 值取自 Theming 的**锚定表**（"今天卡面"的唯一定义处），而不是在界面层写色值 ——
    /// 界面层零颜色字面量是硬性护栏（<c>ThemeRulesTests</c>），兜底也不能例外。
    /// 正常路径总能取到 <c>App.Surface.Card</c> 令牌；这个兜底只为"资源尚未发布就开面板"（预览/开窗早期）而存在。
    /// </remarks>
    private static readonly Color CardFallback = BuildCardFallback();

    private static Color BuildCardFallback()
    {
        var anchor = SurfaceAnchors.Find("SurfaceContainerHigh");
        var argb = anchor?.Today ?? Argb.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        return ColorMath.ToMedia(argb);
    }

    private Color ResolveToken(string token, Color fallback) =>
        TryFindResource(token) switch
        {
            SolidColorBrush b => b.Color,
            Color c => c,
            _ => fallback,
        };

    // ── 按钮 ─────────────────────────────────────────────────────────

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);

    private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ThemeValidator.TryParseHex(HexBox.Text, out _))
        {
            SetHexError(true, "颜色格式不正确，无法应用");
            return;
        }
        ColorConfirmed?.Invoke(this, Current);
    }
}

/// <summary>取色盘需要的令牌键名（避免 UIKit 直接依赖 Theming 的令牌类而产生循环感）。</summary>
internal static class AppTokensLocal
{
    internal const string SurfaceCard = "App.Surface.Card";
}
