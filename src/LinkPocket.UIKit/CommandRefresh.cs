using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace LinkPocket.ViewModels;

/// <summary>
/// 命令可用性的**立即**重估（全站唯一入口）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：WPF 的 <see cref="CommandManager.InvalidateRequerySuggested"/> 把重查排在
/// <c>DispatcherPriority.Background</c>（优先级低于输入 / 渲染 / 动画），界面稍忙就会被饿住；
/// 而"清空选中"发生的那一次鼠标抬起事件里，WPF 自己排的重查往往**早于**清选中落地 ——
/// 两个因素叠加的表现就是用户报障的那条：「取消选中以后，明显过了一会，浏览页的『重命名』和『删除』才变灰」。
/// 判据必须落在"状态真的变了"这一刻，不能靠调度器有空再来。
/// </para>
/// <para>
/// <b>做法</b>：每个 <see cref="RelayCommand"/> 构造时把自己登记进来（**弱引用** —— 测试宿主里
/// 成百上千个 VM 不该被静态表吊住），<see cref="Request"/> 同步逐个触发它们的
/// <c>CanExecuteChanged</c>；末尾仍调一次 <c>InvalidateRequerySuggested</c>，兜住那些
/// 不经过 RelayCommand 的隐式重查场景（剪贴板内容变化、控件焦点变化等）。
/// </para>
/// <para>
/// <b>用法</b>：任何"会改变某个命令 CanExecute 依据"的状态写入点（选中集合、上下文门、编辑态、
/// 异步完成…）在状态写完之后调一次 <see cref="Request"/> —— 与"唯一事实来源 + 一次投影"同一口径。
/// </para>
/// </remarks>
public static class CommandRefresh
{
    private static readonly List<WeakReference<ICommandRefreshable>> Registry = new();

    /// <summary>命令自登记（构造期调用；弱引用，不阻止回收）。</summary>
    internal static void Register(ICommandRefreshable command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (Registry)
        {
            Prune();
            Registry.Add(new WeakReference<ICommandRefreshable>(command));
        }
    }

    /// <summary>**同步**重估全部命令的可用性（立即生效，不等 Background 级调度）。</summary>
    public static void Request()
    {
        List<ICommandRefreshable> alive;
        lock (Registry)
        {
            Prune();
            alive = new List<ICommandRefreshable>(Registry.Count);
            foreach (var weak in Registry)
                if (weak.TryGetTarget(out var command)) alive.Add(command);
        }

        foreach (var command in alive)
            command.RaiseCanExecuteChanged();

        // 兜底：不经 RelayCommand 的绑定 / WPF 自身的隐式重查（焦点、剪贴板等）仍走原路
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>清掉已被回收的登记项（每次访问都做，表大小 = 存活命令数）。</summary>
    private static void Prune()
    {
        for (var i = Registry.Count - 1; i >= 0; i--)
            if (!Registry[i].TryGetTarget(out _))
                Registry.RemoveAt(i);
    }
}

/// <summary>可被 <see cref="CommandRefresh"/> 同步重估的命令（本程序集内的两个 RelayCommand 实现它）。</summary>
internal interface ICommandRefreshable
{
    /// <summary>立即触发 <c>CanExecuteChanged</c>。</summary>
    void RaiseCanExecuteChanged();
}
