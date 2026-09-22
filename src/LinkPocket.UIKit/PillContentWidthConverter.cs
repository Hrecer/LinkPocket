using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace LinkPocket.Views;

/// <summary>
/// 药丸（胶囊按钮）<b>内容的可用宽</b>：控件实测宽 − 左右内距 − 左右描边。
/// </summary>
/// <remarks>
/// <para>
/// <b>它存在的原因</b>：药丸的宽度是**冻结几何**（如命令栏那几枚写死 `Width=112/99/80/80`），
/// 但内容默认按"我要多大就多大"排——英文比中文宽，于是文字**画到药丸外面**（实测「重命名」的
/// 英文 `Rename` 溢出 17.6px、「新建文件夹」溢出 4px）。壳没动、字出去了，光看壳的尺寸发现不了。
/// </para>
/// <para>
/// 把它绑到内容容器的 <c>MaxWidth</c> 上，内容就**再也宽不过药丸的内宽**；
/// 于是"文字放不下"这件事会如实传到自适应通道（<c>LocFit</c> 量到的可用宽就是真的可用宽），
/// 由它把字号缩到放得下——**壳一个像素都不动，让位的是字**（见 `UI-SPEC §3`）。
/// </para>
/// <para>
/// <b>为什么写成转换器而不是在模板里写死数字</b>：写死等于把"内宽"复制一份到模板里，
/// 药丸宽度一改就失配，而失配是静默的（文字又开始画出去）。这里让它**按控件的真实宽度现算**，
/// 单一事实来源仍是药丸自己的 `Width`。
/// </para>
/// </remarks>
public sealed class PillContentWidthConverter : IValueConverter
{
    public static PillContentWidthConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Control control) return double.PositiveInfinity;

        var width = control.ActualWidth;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return double.PositiveInfinity;

        var insets = control.Padding.Left + control.Padding.Right
                     + control.BorderThickness.Left + control.BorderThickness.Right;
        return Math.Max(0, width - insets);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("usable content width is one-way: layout is not state.");
}
