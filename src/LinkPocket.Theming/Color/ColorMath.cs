using Material3.Core;

namespace LinkPocket.Theming.Color;

/// <summary>
/// 颜色科学原语的**唯一封装处**：HCT 读写、对比度、色相圆均值、状态层叠层、α 保留。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这一层</b>：全仓除本程序集外禁止出现 <c>Hct</c> / <c>TonalPalette</c> / <c>ColorScheme</c>
/// 引用（架构测试 <c>ThemeRulesTests</c> 卡住）。把库原语收在唯一类型里，升级库时只需改这里。
/// </para>
/// <para>
/// <b>α 的处置（实测教训）</b>：HCT 不含 α，<c>Hct.FromColor → ToColor</c> 会把 α 拉成 FF。
/// 实测 49 个库键里有 1 个（<c>Scrim</c>，α=A8）因此往返破相；故**派生一律走
/// <see cref="FromAlphaRgb"/>，α 从调用方原样带回**（禁止直接 <c>ToColor()</c> 后再取色）。
/// </para>
/// </remarks>
public static class ColorMath
{
    /// <summary>通道分量（0–255）。<c>Argb</c> 是 int 背书的紧凑类型，需按 AARRGGBB 解包。</summary>
    public readonly record struct Channels(byte A, byte R, byte G, byte B);

    /// <summary>HCT 三分量（H 0–360、C 0–~150、T 0–100）。</summary>
    public readonly record struct Hct3(double H, double C, double T);

    /// <summary>按 AARRGGBB 解包（<c>Argb</c> 无通道属性）。</summary>
    public static Channels Unpack(Argb c)
    {
        var v = c.ToInt();
        return new Channels((byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }

    /// <summary>打包为 <see cref="Argb"/>。</summary>
    public static Argb Pack(byte a, byte r, byte g, byte b) => Argb.FromArgb(a, r, g, b);

    /// <summary>取 HCT 三分量。</summary>
    public static Hct3 Measure(Argb c)
    {
        var h = Hct.FromColor(c);
        return new Hct3(h.Hue, h.Chroma, h.Tone);
    }

    /// <summary>
    /// 由 HCT + α 重建颜色（<b>唯一合法出口</b>：α 由调用方带回，不取自 HCT）。
    /// </summary>
    public static Argb FromAlphaHct(byte alpha, double hue, double chroma, double tone)
    {
        var rgb = Hct.From(hue, chroma, tone).ToColor();
        var v = rgb.ToInt();
        return Argb.FromInt((alpha << 24) | (v & 0x00FFFFFF));
    }

    /// <summary>同色相/彩度/明度，仅换 α。</summary>
    public static Argb WithAlpha(Argb c, byte alpha) =>
        Argb.FromInt((alpha << 24) | (c.ToInt() & 0x00FFFFFF));

    // ── WPF 媒体层适配（Argb ↔ System.Windows.Media.Color）────────────────────
    //
    // **唯一转换点**：界面层不允许出现 `Color.FromRgb` / `Color.FromArgb`（零颜色字面量护栏），
    // 否则"颜色怎么从令牌变成画刷"这件事会散落在各页面里、再次长出第二份实现。
    // 界面要用 WPF Color 时一律经这两个方法（Theming 是本仓唯一可以触碰媒体类型的地方）。
    //
    // 注意：不写成扩展方法 —— Material3.Wpf 已经为 Argb 提供了 ToMedia()/ToArgb()，
    // 同名扩展会与之冲突；改名 + 显式调用最清楚。

    /// <summary><see cref="Argb"/> → WPF <c>Color</c>（α 原样保留）。</summary>
    public static System.Windows.Media.Color ToMedia(Argb c)
    {
        var (a, r, g, b) = Unpack(c);
        return System.Windows.Media.Color.FromArgb(a, r, g, b);
    }

    /// <summary>WPF <c>Color</c> → <see cref="Argb"/>。</summary>
    public static Argb FromMedia(System.Windows.Media.Color c) => Pack(c.A, c.R, c.G, c.B);

    /// <summary>色相旋转（结果规范到 [0,360)）。</summary>
    public static double RotateHue(double hue, double delta) => NormalizeHue(hue + delta);

    /// <summary>把色相规范到 [0,360)。</summary>
    public static double NormalizeHue(double hue)
    {
        var h = hue % 360.0;
        return h < 0 ? h + 360.0 : h;
    }

    /// <summary>
    /// 色相差（取圆上的短弧，0–180）。用于"两个颜色是否几乎一样"的判定。
    /// </summary>
    public static double HueDistance(double a, double b)
    {
        var d = Math.Abs(NormalizeHue(a) - NormalizeHue(b)) % 360.0;
        return d > 180.0 ? 360.0 - d : d;
    }

    /// <summary>
    /// 色相**圆均值**（按权重加权）——族色相与中性色相的唯一算法。
    /// </summary>
    /// <remarks>
    /// 普通算术平均在 0°/360° 边界上会给出 180° 的错误结果，故必须走单位圆向量和。
    /// 权重之和为 0（或向量抵消）时回落到第一个色相，避免 atan2(0,0)。
    /// </remarks>
    public static double CircularMeanHue(IReadOnlyList<(double Hue, double Weight)> samples)
    {
        if (samples.Count == 0) return 0.0;
        double x = 0, y = 0;
        foreach (var (hue, weight) in samples)
        {
            var rad = NormalizeHue(hue) * Math.PI / 180.0;
            x += Math.Cos(rad) * weight;
            y += Math.Sin(rad) * weight;
        }
        if (Math.Abs(x) < 1e-9 && Math.Abs(y) < 1e-9) return NormalizeHue(samples[0].Hue);
        return NormalizeHue(Math.Atan2(y, x) * 180.0 / Math.PI);
    }

    /// <summary>WCAG 2.x 对比度（1–21）；参数顺序无关。</summary>
    public static double ContrastRatio(Argb a, Argb b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var hi = Math.Max(la, lb);
        var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>WCAG 2.x 相对亮度（sRGB 线性化 + Rec.709 权重）。</summary>
    public static double RelativeLuminance(Argb c)
    {
        var ch = Unpack(c);
        return 0.2126 * Linearize(ch.R) + 0.7152 * Linearize(ch.G) + 0.0722 * Linearize(ch.B);
    }

    private static double Linearize(byte v)
    {
        var s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// 状态层叠层（hover/pressed/disabled 的统一算法）：把 <paramref name="layer"/> 按
    /// <paramref name="opacity"/> 叠在 <paramref name="baseColor"/> 上。
    /// </summary>
    /// <remarks>
    /// 库提供 <c>ColorScheme.Overlay</c>，但它要求两侧都是 HCT 可表达的不透明色；
    /// 这里给出等价的纯通道混合实现（唯一出处），使"底层是深色（强调填充）时叠白"这条方向规则
    /// 也能在同一处表达。
    /// </remarks>
    public static Argb Overlay(Argb baseColor, Argb layer, double opacity)
    {
        var b = Unpack(baseColor);
        var l = Unpack(layer);
        var t = Math.Clamp(opacity, 0.0, 1.0);
        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * t, MidpointRounding.AwayFromZero);
        return Pack(255, Mix(b.R, l.R), Mix(b.G, l.G), Mix(b.B, l.B));
    }

    /// <summary>
    /// 明度（Tone）分档插值：在实测锚点的 T 之上按主题整体明度偏移平移，并夹在 [0,100]。
    /// </summary>
    public static double ShiftTone(double tone, double delta) => Math.Clamp(tone + delta, 0.0, 100.0);

    // ── HSV 互算（**用户操作面**专用，与派生链路的 HCT 分工）──────────────────
    //
    // 为什么另有一组 HSV：取色盘需要"二维直觉"——左右 = 饱和度、上下 = 明度。HSV 的 S/V 恰好
    // 对应这两个手势；HCT 的 T 不是这种二维结构，做不出方块。故**取色盘用 HSV、主题派生仍走 HCT**。
    // 放在本类里是因为它同样是"颜色数学"，不该散落在界面层（架构护栏：颜色计算只许在 Theming）。

    /// <summary>HSV → ARGB（H 0–360、S/V 0–1、α 0–255）。</summary>
    public static Argb FromHsv(double hue, double saturation, double value, byte alpha = 0xFF)
    {
        hue = NormalizeHue(hue);
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        var m = value - c;

        double r, g, b;
        if (hue < 60) (r, g, b) = (c, x, 0);
        else if (hue < 120) (r, g, b) = (x, c, 0);
        else if (hue < 180) (r, g, b) = (0, c, x);
        else if (hue < 240) (r, g, b) = (0, x, c);
        else if (hue < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);

        static byte Q(double t) => (byte)Math.Clamp(Math.Round(t * 255), 0, 255);
        return Pack(alpha, Q(r + m), Q(g + m), Q(b + m));
    }

    /// <summary>ARGB → HSV（S/V 0–1；灰阶时色相取 0）。</summary>
    public static void ToHsv(Argb color, out double hue, out double saturation, out double value)
    {
        var ch = Unpack(color);
        double r = ch.R / 255.0, g = ch.G / 255.0, b = ch.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;

        value = max;
        saturation = max <= 0 ? 0 : d / max;

        if (d <= 0) { hue = 0; return; }

        if (max == r) hue = 60 * (((g - b) / d) % 6);
        else if (max == g) hue = 60 * ((b - r) / d + 2);
        else hue = 60 * ((r - g) / d + 4);

        hue = NormalizeHue(hue);
    }
}
