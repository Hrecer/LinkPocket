using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LinkPocket.ViewModels;

/// <summary>
/// 支持**批量替换**的 <see cref="ObservableCollection{T}"/>：整体换一批数据只发**一次** Reset 通知。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要</b>：列表刷新时逐条 <c>Clear()</c> + <c>Add()</c> 会让集合发出 N+1 次变更通知，
/// 而每个订阅者（`ItemsControl` / `TreeView`）都要各自处理一次（重排、容器管理、布局失效）。
/// 实测：10 001 行的目录页刷新时，通知处理本身就是"UI 布局"那 1.2 秒的主要来源；
/// 面向十万级数据，集合通知次数必须与"数据总量"脱钩。
/// </para>
/// <para>
/// <b>语义</b>：<see cref="ReplaceAll"/> 等价于"清空 + 逐条加入"的**结果**，只是通知合并为一次
/// <see cref="NotifyCollectionChangedAction.Reset"/>。视图侧因此会整体重建容器（虚拟化下只重建可视区），
/// 与原先 <c>Clear()</c> 已经发出的 Reset 行为一致——不引入新的视图语义。
/// </para>
/// </remarks>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>整体替换内容（一次 Reset 通知）。</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>追加一批（一次 Reset 通知）。</summary>
    public void AddRange(IEnumerable<T> items)
    {
        var added = false;
        foreach (var item in items)
        {
            Items.Add(item);
            added = true;
        }
        if (!added) return;

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
