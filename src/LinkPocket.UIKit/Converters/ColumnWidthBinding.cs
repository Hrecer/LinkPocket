using System.Windows;
using System.Windows.Controls;

namespace LinkPocket.Views;

/// <summary>
/// 把 <see cref="ColumnDefinition.Width"/> 变成可绑定属性（WPF 原生 <c>Width</c> 不是依赖属性，无法 Binding）。
/// 共享数据表控件 <c>SortableDataTable</c> 的基础设施：表头与每一行的 Grid 列都通过本附加属性
/// 绑定到控件的 <c>ColumnWidths</c>（单一数据源），拖拽表头调整宽度时各行实时同步。
/// 用法：<c>&lt;ColumnDefinition views:ColumnWidthBinding.Width="{Binding ColumnWidths[0]}"/&gt;</c>
/// （索引器绑定会响应 ObservableCollection 的 Replace 通知）。
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
