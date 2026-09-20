using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LinkPocket.ViewModels;

/// <summary>
/// <see cref="Color"/> → <see cref="SolidColorBrush"/>（唯一映射点）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：「外观」面板要用**用户选的颜色本身**（身份色圆点、派生示意条、色槽色块）作画刷，
/// 而这些都是运行时才知道的值，不是资源键 → 不能用 <c>DynamicResource</c>，必须走绑定 + 转换器。
/// </para>
/// <para>
/// <b>颜色从哪来</b>：值由 <c>LinkPocket.Theming</c> 给出（<c>Argb</c>）；
/// 界面把 <c>Argb</c> 转成 WPF <c>Color</c> 只发生在 ViewModel 的边界上
/// （架构护栏：<c>Hct</c> / <c>TonalPalette</c> / <c>ColorScheme</c> 只许出现在 Theming）。
/// </para>
/// <para>
/// <b>画刷冻结</b>：与 <c>ThemePublisher</c> 同口径——冻结后可跨线程读取，且堵住"某处偷改颜色"的旁路。
/// 每次调用新建一个画刷（这里调用量很小：卡片与色槽，不是逐行渲染路径）。
/// </para>
/// </remarks>
public sealed class ColorToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color color) return null;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
