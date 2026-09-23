using System;
using System.Globalization;
using System.Windows.Data;

namespace LinkPocket.Views;

/// <summary>
/// 列宽（<see cref="System.Windows.GridLength"/>）或表头单元格实测宽（<see cref="double"/>）→
/// 该列表头**文字的可用宽**。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：表头文案宽度随语言变（中文「按查看次数排序」13pt 需 84px，而该列只有 72px），
/// 而列宽是冻结几何——放不下时该让位的是字号（见 <c>UI-SPEC §3</c> 的"几何冻结 + 字号自适应"）。
/// 自适应要有一个"这一格有多少"的输入，而表头按钮自己没有显式 <c>Width</c>（宽度来自 Grid 列定义），
/// 于是 <c>LocFit.AvailableWidth</c> 读不到任何外部约束 → 文字照原字号画出去。
/// </para>
/// <para>
/// 现行输入 = **表头单元格的实测宽**（<c>SortableDataTable</c> 绑上来）：星号列在冻结之前
/// <c>GridLength.Value</c> 是**权重**（1 / 2）而不是像素，拿它算会得出 0（把表头文字夹成 0 宽、
/// 屏幕上一个字都不画）；单元格实测宽在冻结前后都是真像素。
/// </para>
/// <para>
/// 扣减 = <see cref="SortableHeaderButton.TextInsets"/>（药丸内距 + 排序箭头留白），
/// 与表头模板里那枚药丸的内距一致。
/// </para>
/// </remarks>
public sealed class ColumnTextWidthConverter : IValueConverter
{
    public static ColumnTextWidthConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value switch
        {
            // 星号 / 自动列：`Value` 是权重或 0，都不是像素 ⇒ 不约束（NaN = 不写 MaxWidth）
            System.Windows.GridLength g when g.IsStar || g.IsAuto => double.NaN,
            System.Windows.GridLength g => g.Value,
            double d => d,
            _ => double.NaN,
        };
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return double.NaN;
        return Math.Max(0, width - SortableHeaderButton.TextInsets);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("usable text width is one-way: layout is not state.");
}
