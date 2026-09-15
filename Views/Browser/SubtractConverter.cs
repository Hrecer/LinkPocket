using System;
using System.Globalization;
using System.Windows.Data;

namespace LinkPocket.Views.Browser;

/// <summary>
/// 数值减法转换器：返回 <c>max(Min, value - ConverterParameter)</c>。
/// 用于让「名称」列文本的可显示宽度跟着列宽走——列宽变窄时文本按省略号收窄，
/// 保证紧跟其后的计数药丸不会溢出到相邻列（列宽拖拽时才需要）。
/// </summary>
public class SubtractConverter : IValueConverter
{
    /// <summary>结果下限，避免列极窄时算出负宽度。</summary>
    public double Min { get; set; } = 0;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var source = value switch
        {
            double d => d,
            int i => i,
            _ => 0d
        };

        var subtract = 0d;
        if (parameter != null) double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out subtract);

        return Math.Max(Min, source - subtract);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
