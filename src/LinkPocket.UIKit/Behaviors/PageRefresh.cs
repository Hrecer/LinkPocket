using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LinkPocket.ViewModels;

/// <summary>
/// 「刷新」的全局登记处（右键菜单用）：每个页面把自己的刷新命令登记进来。
///
/// <para><b>口径</b>：登记的必须是**该页 F5 用的同一个命令对象**（`RefreshCommand`），不是第二条刷新路径——
/// 否则右键刷新与 F5 迟早会漂移成两种语义（一处带选中保持、一处不带）。</para>
/// <para>右键菜单按"被点元素**所属的页面**"取它调用，所以同一套右键逻辑在任意页面都指向本页的刷新；
/// 没有登记的页面（设置页这种没有单一刷新语义的）右键项仍在、但如实置灰，不假装能刷。</para>
/// </summary>
public static class PageRefresh
{
    private sealed record Entry(WeakReference<FrameworkElement> Page, ICommand Command, Func<bool>? CanExecute);

    private static readonly List<Entry> Entries = new();

    /// <summary>页面装载时登记本页的刷新命令（重复登记同一个页面只保留最后一条）。</summary>
    public static void Register(FrameworkElement page, ICommand command, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(command);
        lock (Entries)
        {
            Entries.RemoveAll(entry => !entry.Page.TryGetTarget(out var target) || ReferenceEquals(target, page));
            Entries.Add(new Entry(new WeakReference<FrameworkElement>(page), command, canExecute));
        }
    }

    /// <summary>从被点元素向上找**最近的已登记页面**并执行它的刷新；返回是否真的执行了。</summary>
    public static bool TryInvokeFrom(DependencyObject? source)
    {
        if (Find(source) is not { } entry) return false;
        if (!CanExecute(entry)) return false;
        entry.Command.Execute(null);
        return true;
    }

    /// <summary>同上，只问"现在能不能刷"（菜单项置灰依据）。</summary>
    public static bool CanInvokeFrom(DependencyObject? source)
        => Find(source) is { } entry && CanExecute(entry);

    private static bool CanExecute(Entry entry)
        => entry.CanExecute?.Invoke() ?? entry.Command.CanExecute(null);

    private static Entry? Find(DependencyObject? source)
    {
        if (source is null) return null;
        Entry[] snapshot;
        lock (Entries)
        {
            Entries.RemoveAll(entry => !entry.Page.TryGetTarget(out _));
            snapshot = Entries.ToArray();
        }

        foreach (var entry in snapshot)
        {
            if (!entry.Page.TryGetTarget(out var page)) continue;
            if (IsWithin(source, page)) return entry;
        }
        return null;
    }

    /// <summary>元素是否在某个祖先子树内（视觉树优先，跨到逻辑树兜底）。</summary>
    private static bool IsWithin(DependencyObject source, DependencyObject ancestor)
    {
        for (var node = source; node is not null; node = ParentOf(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    internal static DependencyObject? ParentOf(DependencyObject node)
    {
        var visual = VisualTreeHelper.GetParent(node);
        return visual ?? LogicalTreeHelper.GetParent(node);
    }
}
