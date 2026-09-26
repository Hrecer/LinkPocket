using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels;

/// <summary>
/// 整窗「右键必有刷新」：除标题栏 / 导航条（<c>excluded</c> 子树）以外，界面上任意位置右键都会带一项「刷新」。
/// <list type="bullet">
/// <item>原本**已有**右键菜单的（行、空白区、回收站菜单…）：在末尾追加「刷新」，**幂等**——菜单里已有同名项就不再加
/// （回收站页的菜单本来就自带「刷新」，不能变成两项）。</item>
/// <item>原本**没有**菜单的位置（页面空白、边角、滚动条…）：弹一个只含「刷新」的共享菜单；
/// 不往元素上挂 <c>ContextMenu</c>，避免改掉该元素原来的右键语义（右键拖拽、成环判定那些仍归各页自己管）。</item>
/// </list>
/// <para><b>刷新动作</b>取的是该元素所属页面登记在 <see cref="PageRefresh"/> 里的命令（与 F5 同一个对象）；
/// 该页没登记（设置页这类）就如实置灰。文本框自己没有菜单时不接管：原生剪切/复制/粘贴不能被顶掉。</para>
/// </summary>
public static class ContextRefresh
{
    /// <summary>共享菜单（只含刷新项）。必须留引用：菜单打开期间不能被 GC 回收。</summary>
    private static ContextMenu? _sharedMenu;

    /// <summary>装在窗口上（一次性）：<paramref name="excludedSubtree"/> 内（标题栏 + 胶囊导航）的右键不干预。</summary>
    public static void Install(Window window, DependencyObject excludedSubtree)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(excludedSubtree);
        window.ContextMenuOpening += (_, e) => OnOpening(e, excludedSubtree);
    }

    private static void OnOpening(ContextMenuEventArgs e, DependencyObject excluded)
    {
        if (e.OriginalSource is not DependencyObject origin) return;
        if (IsWithin(origin, excluded)) return;      // 标题栏 / 导航条：原有行为原样保留

        var source = NearestElement(origin);
        if (source is null) return;

        // 向上找最近一个自带菜单的元素：有就追加（幂等），没有就弹共享菜单
        var owner = NearestMenuOwner(source);
        if (owner?.ContextMenu is { } menu)
        {
            AppendIfMissing(menu, source);
            return;
        }

        // 文本框自己没有菜单 → 别接管（WPF 的剪切/复制/粘贴菜单不能被顶掉）
        if (IsTextInput(source)) return;

        OpenShared(origin, source, e);
    }

    /// <summary>追加「刷新」（幂等：菜单里已有同名项就只刷新它的可用性，不再加第二项）。</summary>
    private static void AppendIfMissing(ContextMenu menu, DependencyObject source)
    {
        foreach (var item in menu.Items)
        {
            if (item is MenuItem existing && IsRefreshLabel(existing.Header))
            {
                // 页面自带的刷新项（回收站页）：只同步可用性，不动它的命令绑定
                existing.IsEnabled = PageRefresh.CanInvokeFrom(source);
                return;
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem(source));
    }

    private static void OpenShared(DependencyObject origin, DependencyObject source, ContextMenuEventArgs e)
    {
        if (_sharedMenu is null)
        {
            _sharedMenu = new ContextMenu { Placement = PlacementMode.MousePoint };
            // 紧凑菜单样式（与各页菜单同一套）：样式来自 UIKit.xaml 的隐式兜底键
            if (Application.Current?.TryFindResource("LpMenuItem") is Style style)
                _sharedMenu.Resources.Add(typeof(MenuItem), style);
        }

        _sharedMenu.Items.Clear();
        _sharedMenu.Items.Add(CreateItem(source));
        _sharedMenu.PlacementTarget = origin as UIElement;
        _sharedMenu.Placement = PlacementMode.MousePoint;
        _sharedMenu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem CreateItem(DependencyObject source)
    {
        var item = new MenuItem { Header = Loc.T("menu.refresh") };
        item.IsEnabled = PageRefresh.CanInvokeFrom(source);
        item.Click += (_, _) => PageRefresh.TryInvokeFrom(source);
        return item;
    }

    private static bool IsRefreshLabel(object? header)
        => header is string text && string.Equals(text, Loc.T("menu.refresh"), StringComparison.Ordinal);

    private static DependencyObject? NearestElement(DependencyObject origin)
        => origin is FrameworkElement element ? element : Nearest<FrameworkElement>(origin);

    /// <summary>从命中元素往上找最近一个 <c>ContextMenu</c> 非空的元素（谁先有谁就是这张菜单的主人）。</summary>
    private static FrameworkElement? NearestMenuOwner(DependencyObject source)
    {
        for (var node = source; node is not null; node = PageRefresh.ParentOf(node))
        {
            if (node is FrameworkElement element && element.ContextMenu is not null) return element;
            if (node is Window) break;
        }
        return null;
    }

    private static bool IsTextInput(DependencyObject source)
    {
        for (var node = source; node is not null; node = PageRefresh.ParentOf(node))
        {
            if (node is System.Windows.Controls.Primitives.TextBoxBase) return true;
            if (node is Window) break;
        }
        return false;
    }

    private static bool IsWithin(DependencyObject source, DependencyObject ancestor)
    {
        for (var node = source; node is not null; node = PageRefresh.ParentOf(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    private static T? Nearest<T>(DependencyObject source) where T : DependencyObject
    {
        for (var node = source; node is not null; node = PageRefresh.ParentOf(node))
            if (node is T hit) return hit;
        return null;
    }
}
