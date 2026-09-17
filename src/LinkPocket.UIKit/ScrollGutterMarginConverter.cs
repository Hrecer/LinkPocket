using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LinkPocket.Views.Browser;

/// <summary>
/// 「居中定宽内容列」的外边距补偿转换器，用来消除纵向滚动条带来的整页横移。
///
/// 问题：<c>ScrollViewer</c> 的纵向滚动条会占掉 viewport 宽度，于是同样是
/// <c>HorizontalAlignment=Center</c> 的 760 宽内容列，在有滚动条的页面里被居中到更窄的区域，
/// 左边界会比没有滚动条的页面少 滚动条宽/2（本机 8.67px）。详情页与编辑页内容高度不同，
/// 一个出现滚动条、一个没有，从详情页切到编辑页就会看到整块内容"整体向右挪了一点"。
///
/// 做法：出现纵向滚动条时把内容列的右侧外距减掉一个滚动条宽，
/// 两种情形下内容列的中轴都正好落在 ScrollViewer 的中轴上（等价于 CSS 的 scrollbar-gutter 补偿）。
/// ConverterParameter 传基础外边距，形如 <c>"36,28,36,28"</c>。
/// </summary>
public class ScrollGutterMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var m = new double[4];
        var parts = (parameter as string ?? string.Empty).Split(',');
        for (var i = 0; i < 4 && i < parts.Length; i++)
            double.TryParse(parts[i], NumberStyles.Any, CultureInfo.InvariantCulture, out m[i]);

        // 只有纵向滚动条可见时才占宽；HorizontalScrollBarVisibility 在本项目里恒为 Disabled。
        if (value is Visibility.Visible)
            m[2] -= SystemParameters.VerticalScrollBarWidth;

        return new Thickness(m[0], m[1], m[2], m[3]);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
