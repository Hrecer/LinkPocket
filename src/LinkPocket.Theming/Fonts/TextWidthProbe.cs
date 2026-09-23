using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LinkPocket.Theming.Fonts;

/// <summary>
/// 文本排版度量的<b>唯一实现</b>：<c>FormattedText</c> 与真实排版同源，宽度即"这几个字要占多少像素"。
/// </summary>
/// <remarks>
/// <para>
/// 两个消费者，一份实现：运行时自适应（UIKit <c>LocFit</c> 判断"放不放得下"）与离线文案预算
/// （<c>渲染检查工具 --text-budget</c> 量每条英文文案要占多宽）。<b>不许各写一份</b>——两边量出不同的宽度，
/// "离线说放得下、运行时却缩了字号"这类分歧就再也解释不清（本仓"单一事实来源"的本行口径）。
/// </para>
/// <para>
/// 数字与业务无关：这里只回答"多宽"，不回答"够不够"（够不够是调用方的判据，阈值也只有调用方知道）。
/// </para>
/// </remarks>
public static class TextWidthProbe
{
    /// <summary>一次度量的入参（文字 + 字体族 + 字号 + 字形 + DPI）。</summary>
    /// <param name="Text">被测文本。</param>
    /// <param name="Family">字体族令牌值（可含回退链，<c>FontCatalog.BuildFontFamily</c> 会解析）。</param>
    /// <param name="Size">字号（设备无关像素 / pt）。</param>
    /// <param name="Weight">字重。</param>
    /// <param name="Stretch">字宽。</param>
    /// <param name="PixelsPerDip">每 DIP 的物理像素（真实元素取 <c>VisualTreeHelper.GetDpi</c>；离线工具按 96 DPI 取 1）。</param>
    public readonly record struct MetricsRequest(
        string Text, string Family, double Size, FontWeight Weight, FontStretch Stretch, double PixelsPerDip);

    /// <summary>度量一段文本的排版尺寸。</summary>
    public static Size Measure(in MetricsRequest request)
    {
        var typeface = new Typeface(
            FontCatalog.BuildFontFamily(request.Family), FontStyles.Normal, request.Weight, request.Stretch);
        var formatted = new FormattedText(
            request.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
            request.Size, Brushes.Black, request.PixelsPerDip <= 0 ? 1.0 : request.PixelsPerDip);
        return new Size(formatted.Width, formatted.Height);
    }

    /// <summary>只取宽度（自适应最常用的那一个数）。</summary>
    public static double Width(in MetricsRequest request) => Measure(request).Width;
}
