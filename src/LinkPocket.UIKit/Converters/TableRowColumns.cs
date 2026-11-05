using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace LinkPocket.Views;

/// <summary>
/// 模板模式行的一次性列宽同步：把 <c>SortableDataTable.ColumnWidths</c> 挂到行根 Grid，
/// 由本行为把宽度逐列写给该 Grid 的 <see cref="ColumnDefinition"/>。
///
/// <para>存在的理由：给每个 <c>ColumnDefinition</c> 单独绑一次祖先索引器（6 列 × 每行）在
/// 首次实现视口行时实测吃掉 10 万行智能列表首帧 40ms 以上——<c>ColumnDefinition</c> 是
/// FrameworkContentElement，每条祖先绑定都要自己解决继承上下文。行根 Grid 只绑一次集合，
/// 列宽变化经 <see cref="INotifyCollectionChanged"/> 推给已实现的行，拖拽表头仍然逐行实时同步。</para>
/// </summary>
public static class TableRowColumns
{
    public static readonly DependencyProperty WidthsProperty = DependencyProperty.RegisterAttached(
        "Widths", typeof(IList<GridLength>), typeof(TableRowColumns),
        new PropertyMetadata(null, OnWidthsChanged));

    public static IList<GridLength>? GetWidths(DependencyObject d) => (IList<GridLength>?)d.GetValue(WidthsProperty);

    public static void SetWidths(DependencyObject d, IList<GridLength>? value) => d.SetValue(WidthsProperty, value);

    // 记录当前集合的订阅（附加属性换源时精确退订；集合本身是表的长期对象）
    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(EventHandler<NotifyCollectionChangedEventArgs>), typeof(TableRowColumns),
        new PropertyMetadata(null));

    private static void OnWidthsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid) return;

        if (e.OldValue is INotifyCollectionChanged oldSource
            && d.GetValue(HandlerProperty) is EventHandler<NotifyCollectionChangedEventArgs> oldHandler)
            CollectionChangedEventManager.RemoveHandler(oldSource, oldHandler);

        if (e.NewValue is not IList<GridLength> widths)
        {
            d.ClearValue(HandlerProperty);
            return;
        }

        EventHandler<NotifyCollectionChangedEventArgs> handler = (_, _) => Apply(grid, widths);
        if (widths is INotifyCollectionChanged source)
            CollectionChangedEventManager.AddHandler(source, handler);
        d.SetValue(HandlerProperty, handler);
        Apply(grid, widths);
    }

    private static void Apply(Grid grid, IList<GridLength> widths)
    {
        var defs = grid.ColumnDefinitions;
        var count = defs.Count < widths.Count ? defs.Count : widths.Count;
        for (var i = 0; i < count; i++)
        {
            var width = widths[i];
            if (!defs[i].Width.Equals(width)) defs[i].Width = width;
        }
    }
}
