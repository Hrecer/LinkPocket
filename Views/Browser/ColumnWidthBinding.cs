using System.Windows;
using System.Windows.Controls;

namespace LinkPocket.Views.Browser;

/// <summary>
/// 把 <see cref="ColumnDefinition.Width"/> 变成可绑定属性（WPF 原生 <c>Width</c> 不是依赖属性，无法 Binding）。
/// 列表的列头与每一行共用同一份列宽（BrowserViewModel.ColumnWidths），
/// 这样拖拽列头调整宽度时各行会同步变化，无需遍历可视树。
/// 用法：<c>&lt;ColumnDefinition local:ColumnWidthBinding.Width="{Binding ColumnWidths[0]}"/&gt;</c>
/// </summary>
public static class ColumnWidthBinding
{
    public static readonly DependencyProperty WidthProperty =
        DependencyProperty.RegisterAttached(
            "Width",
            typeof(GridLength),
            typeof(ColumnWidthBinding),
            new PropertyMetadata(new GridLength(1, GridUnitType.Star), OnWidthChanged));

    public static GridLength GetWidth(DependencyObject d) => (GridLength)d.GetValue(WidthProperty);

    public static void SetWidth(DependencyObject d, GridLength value) => d.SetValue(WidthProperty, value);

    private static void OnWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColumnDefinition column && e.NewValue is GridLength width)
            column.Width = width;
    }
}
