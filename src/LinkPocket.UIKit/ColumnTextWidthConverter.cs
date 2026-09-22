using System;
using System.Globalization;
using System.Windows.Data;

namespace LinkPocket.Views;

/// <summary>
/// 列宽（<see cref="System.Windows.GridLength"/>）→ 该列表头**文字的可用宽**。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：表头文案宽度随语言变（中文「按查看次数排序」13pt 需 84px，而该列只有 72px），
/// 而列宽是冻结几何——放不下时该让位的是字号（见 <c>UI-SPEC §3</c> 的"几何冻结 + 字号自适应"）。
/// 自适应要有一个"这一格有多少"的输入，而这个输入只有列宽单一数据源知道：
/// 表头按钮自己没有显式 <c>Width</c>（宽度来自 Grid 列定义），
/// 于是 <c>LocFit.AvailableWidth</c> 读不到任何外部约束 → 文字照原字号画出去、压在相邻列上。
/// </para>
/// <para>
/// 扣减 = <see cref="SortableHeaderButton.TextInsets"/>（药丸内距 + 排序箭头留白），
/// 与表头模板里那枚药丸的内距一致；星号列（<c>GridLength.IsStar</c>）在冻结后也会走到这里
/// （<c>ColumnWidths</c> 被替换成实测像素），所以两种列都拿得到具体数字。
/// </para>
/// </remarks>
public sealed class ColumnTextWidthConverter : IValueConverter
{
    public static ColumnTextWidthConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value switch
        {
            System.Windows.GridLength g => g.Value,
            double d => d,
            _ => double.NaN,
        };
        // 星号列在冻结之前拿不到像素值 → 给 NaN（= 不约束，按原字号画）
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return double.NaN;
        return Math.Max(0, width - SortableHeaderButton.TextInsets);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("usable text width is one-way: layout is not state.");
}
