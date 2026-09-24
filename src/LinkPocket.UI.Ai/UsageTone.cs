using System.Windows;
using System.Windows.Media;

namespace LinkPocket.UI.Ai;

/// <summary>
/// 上下文分项的**色阶**（参照实现口径：同一个强调色按 5 档深浅拉开，
/// 不做多色轮转——多彩会让人以为"不同颜色代表不同性质"，其实只是同一支色的浓淡）。
/// <para>档位越靠前（占比越大）越浓，靠后越淡，与背景同族，越往后越"退后"。</para>
/// </summary>
internal static class UsageTone
{
    /// <summary>每档相对强调色的保留比例（0 = 完全化成表面色）。</summary>
    private static readonly double[] MixWithSurface = [1.00, 0.78, 0.58, 0.42, 0.28];

    /// <summary>取第 <paramref name="index"/> 档的颜色（超界取最后一档，颜色缺失 = 透明）。</summary>
    public static Brush Brush(int index, Color accent, Color surface)
    {
        var mix = MixWithSurface[Math.Clamp(index, 0, MixWithSurface.Length - 1)];
        // 线性混色：mix=1 全是强调色，mix 越小越靠近表面色（"退后"）。
        var color = Color.FromRgb(
            (byte)Math.Round(accent.R * mix + surface.R * (1 - mix)),
            (byte)Math.Round(accent.G * mix + surface.G * (1 - mix)),
            (byte)Math.Round(accent.B * mix + surface.B * (1 - mix)));
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>从资源里取色（取不到 = 透明，不猜颜色）。</summary>
    public static Color Resolve(FrameworkElement element, string key)
        => element.TryFindResource(key) is SolidColorBrush brush ? brush.Color : Colors.Transparent;
}
