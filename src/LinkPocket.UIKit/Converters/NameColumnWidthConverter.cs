using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace LinkPocket.Views.Browser;

/// <summary>
/// 「名称」列文本可用宽度（像素）= <b>名称列实宽</b> − 固定占位（图标 + 文本左距 + 计数药丸）。
///
/// 为什么必须这么算（曾经的缺陷）：名称列是 <c>GridLength</c> 的 <b>Star</b> 列，
/// 而 <see cref="GridLength.Value"/> 在 Star 列上是 <b>权重</b>（1），不是像素——
/// 直接拿它减占位会得到负数并被下限截断，名称文本的 MaxWidth 于是恒为 60px，
/// 表现为"空间明明够却总是省略号"（只有用户拖过一次列头、Star 被冻结成像素后才偶然正常）。
///
/// 输入（MultiBinding，顺序固定）：
/// ① 名称列列宽（<see cref="GridLength"/>，Star 或像素）；
/// ② 全部列宽集合（<see cref="GridLength"/> 序列，用于把 Star 权重换算成像素）；
/// ③ 行内容区实宽（行列容器 <c>ActualWidth</c>）。
/// 任一输入缺失/未布局（宽度 ≤ 0）→ 返回 <see cref="double.PositiveInfinity"/>（不设上限，绝不误截断）。
/// </summary>
public sealed class NameColumnWidthConverter : IMultiValueConverter
{
    /// <summary>固定占位：图标 20 + 文本左距 10 + 药丸与其左距约 48。</summary>
    public double Reserved { get; set; } = 78;

    /// <summary>结果下限（列极窄时避免算出负宽）。</summary>
    public double Min { get; set; } = 60;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return double.PositiveInfinity;
        if (values[0] is not GridLength column) return double.PositiveInfinity;
        if (values[1] is not IEnumerable<GridLength> allWidths) return double.PositiveInfinity;

        var rowWidth = values[2] switch
        {
            double d => d,
            int i => i,
            _ => 0d
        };
        if (rowWidth <= 0) return double.PositiveInfinity;   // 尚未布局：不设上限

        var widths = allWidths.ToList();
        double pixelWidth;
        if (column.IsStar)
        {
            // Star 换算成像素：与 Grid 的分配口径一致（固定列先占，剩余按权重比例分）
            var starWeightSum = widths.Where(w => w.IsStar).Sum(w => w.Value);
            var fixedSum = widths.Where(w => !w.IsStar).Sum(w => w.Value);
            var starSpace = Math.Max(0, rowWidth - fixedSum);
            pixelWidth = starWeightSum > 0 ? starSpace * column.Value / starWeightSum : starSpace;
        }
        else
        {
            pixelWidth = column.Value;
        }

        return Math.Max(Min, pixelWidth - Reserved);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
