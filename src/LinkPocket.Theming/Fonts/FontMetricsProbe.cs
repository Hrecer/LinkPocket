using System.Globalization;

namespace LinkPocket.Theming.Fonts;

/// <summary>
/// 字体**度量自检**（方案 §6.2）：换字体前用一组基准串在当前字号下测宽/行高，
/// 与默认字体比较，超出阈值就**提示**（不阻止应用）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么只提示不阻止</b>：版式已冻结（字号/字重不随字体缩放），故"某个字体在固定尺寸下会截断"
/// 是真实风险（按钮/行高都是固定像素）。但禁用该字体同样不合理（用户可能就是要用）。
/// 正确处置 = **如实告知 + 由用户决定**，这正是本仓"失败要暴露、但不替用户做决定"的一贯口径。
/// </para>
/// <para>
/// 阈值取自方案：宽度 ±15%、行高 &gt; 1.1 倍。
/// </para>
/// </remarks>
public static class FontMetricsProbe
{
    /// <summary>宽度容差（相对默认字体）。</summary>
    public const double WidthTolerance = 0.15;

    /// <summary>行高上限倍率（相对默认字体）。</summary>
    public const double HeightRatioLimit = 1.1;

    /// <summary>基准串：混排中英数 + 界面里最常见的字符形态（书签标题、按钮文案、ID）。</summary>
    public const string SampleText = "LinkPocket 书签管理 0123";

    /// <summary>一次度量结果。</summary>
    /// <param name="Text">被测文本。</param>
    /// <param name="Width">排版宽度（设备无关像素）。</param>
    /// <param name="Height">排版高度。</param>
    public readonly record struct Measurement(string Text, double Width, double Height);

    /// <summary>一次自检结论。</summary>
    /// <param name="Ok">是否在容差内。</param>
    /// <param name="Message">给用户看的一句话（Ok 时为空）。</param>
    /// <param name="Candidate">候选字体度量。</param>
    /// <param name="Baseline">基准（默认字体）度量。</param>
    public readonly record struct Verdict(bool Ok, string Message, Measurement Candidate, Measurement Baseline)
    {
        /// <summary>宽度相对偏差（候选/基准 − 1）。</summary>
        public double WidthDelta => Baseline.Width <= 0 ? 0 : Candidate.Width / Baseline.Width - 1.0;

        /// <summary>行高倍率（候选/基准）。</summary>
        public double HeightRatio => Baseline.Height <= 0 ? 0 : Candidate.Height / Baseline.Height;
    }

    /// <summary>
    /// 在给定字号下度量一段文本（用 <see cref="System.Windows.Media.FormattedText"/>，与真实排版同源）。
    /// </summary>
    /// <param name="family">字体族令牌值（含回退链）。</param>
    /// <param name="fontSize">字号（设备无关像素）。</param>
    /// <param name="text">被测文本（缺省 = <see cref="SampleText"/>）。</param>
    public static Measurement Measure(string family, double fontSize, string? text = null)
    {
        var probe = text ?? SampleText;
        var formatted = new System.Windows.Media.FormattedText(
            probe,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface(FontLoader.BuildFontFamily(family), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal),
            fontSize,
            System.Windows.Media.Brushes.Black,
            1.0);
        return new Measurement(probe, formatted.Width, formatted.Height);
    }

    /// <summary>
    /// 自检：候选字体相对默认字体的宽度/行高是否超阈值。
    /// </summary>
    /// <param name="candidateFamily">候选字体族令牌值。</param>
    /// <param name="fontSize">界面主字号（实测大量使用 12 / 12.5 / 13）。</param>
    public static Verdict Inspect(string candidateFamily, double fontSize = 12.5)
    {
        var candidate = Measure(candidateFamily, fontSize);
        var baseline = Measure(FontLoader.BuildTokenValue(FontLoader.DefaultUiFamily), fontSize);

        var w = candidate.Width / baseline.Width - 1.0;
        var h = candidate.Height / baseline.Height;

        if (Math.Abs(w) > WidthTolerance)
        {
            var dir = w > 0 ? "偏宽" : "偏窄";
            return new Verdict(false,
                $"该字体在此尺寸下宽度{dir} {Math.Abs(w) * 100:F0}%（按钮与列宽为固定值，可能截断或留白）",
                candidate, baseline);
        }
        if (h > HeightRatioLimit)
            return new Verdict(false,
                $"该字体行高为默认的 {h:F2} 倍（行高为固定值，可能截断）", candidate, baseline);

        return new Verdict(true, string.Empty, candidate, baseline);
    }
}
