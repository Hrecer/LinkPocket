using System.Collections.Concurrent;
using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 撤销协调器（IUndoCoordinator）：纯状态机（撤销栈 + 重做栈，上限 100 条）。
/// 登记面：引擎在顶层可撤销命令（Reversible + UndoInverse）成功后调 <see cref="Record"/>。
/// 消费面：undo.undo / undo.redo 命令处理器调 TakeUndoAsync/TakeRedoAsync 拿到条目后，
/// 在自身管道内嵌套派发逆向命令/原命令（与撤销命令同事务；引擎会为逆向执行再次登记新条目——
/// 对称对由此天然形成 撤销↔重放 的往复能力）。
/// </summary>
public sealed class UndoCoordinator : IUndoCoordinator
{
    private const int Capacity = 100;

    private readonly ConcurrentStack<UndoEntry> _undo = new();
    private readonly ConcurrentStack<UndoEntry> _redo = new();
    /// <summary>
    /// 栈操作互斥：TakeUndoAsync(id) 的快照→清空→重放之间若并发放置会吞掉新条目；
    /// 公开调用面虽经引擎写闸串行化，但协调器本身应自洽——不经管道直调的并发也绝不丢条目。
    /// </summary>
    private readonly object _lock = new();

    public Task<IReadOnlyList<UndoEntry>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<UndoEntry> list = _undo.ToArray();   // 栈序 = 最近在前
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<UndoEntry>> ListRedoAsync(CancellationToken ct)
    {
        IReadOnlyList<UndoEntry> list = _redo.ToArray();   // 栈序 = 最近在前
        return Task.FromResult(list);
    }

    public Task<int> ClearAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            var count = _undo.Count + _redo.Count;
            _undo.Clear();
            _redo.Clear();
            return Task.FromResult(count);
        }
    }

    public Task<UndoEntry?> TakeUndoAsync(string? id, CancellationToken ct)
    {
        lock (_lock)
        {
            if (id is null)
            {
                return Task.FromResult(_undo.TryPop(out var entry) ? entry : null);
            }

            // 按 id 取指定条目：重排栈（栈内其余条目保持原序）
            var items = _undo.ToArray();
            var taken = items.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
            if (taken is null) return Task.FromResult<UndoEntry?>(null);
            var rest = items.Where(e => !string.Equals(e.Id, id, StringComparison.Ordinal));
            _undo.Clear();
            foreach (var e in rest) _undo.Push(e);
            return Task.FromResult<UndoEntry?>(taken);
        }
    }

    public Task<UndoEntry?> TakeRedoAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult(_redo.TryPop(out var entry) ? entry : null);
        }
    }

/// <summary>
    /// 登记一条（或合并进一条）可撤销记录。
    /// 逆向步骤来源：① 处理器回填的 <paramref name="inverse"/>（能带旧值，重命名/移动靠它）；
    /// ② 退回描述符声明的 UndoInverse + 原参数（对称对 links.trash↔trash.restore 走这条）。
    /// 两者都没有 → 不入栈（例如 folders.update 改名：明确不可撤销）。
    /// <paramref name="groupId"/> 与栈顶同组 → **追加合并**（一次粘贴多选 = 一个用户动作 = 一条记录）。
    /// </summary>
    public void Record(CommandDescriptor descriptor, JsonElement args, CallerRef caller,
        IReadOnlyList<UndoInverseStep>? inverse = null, string? groupId = null)
    {
        if (!descriptor.IsMutation) return;

        var steps = BuildSteps(descriptor, args, inverse);
        if (steps.Count == 0) return;

        lock (_lock)
        {
            _redo.Clear();   // 新撤销使重做链失效（标准 redo 语义）

            // 分组：与栈顶同组 → 合并进同一条（不进新条，容量不变）
            if (groupId is { Length: > 0 } && _undo.TryPeek(out var top)
                && string.Equals(top.GroupId, groupId, StringComparison.Ordinal))
            {
                _undo.TryPop(out _);
                _undo.Push(top with { Steps = [.. top.Steps, .. steps] });
                return;
            }

            _undo.Push(new UndoEntry(
                Id: Guid.NewGuid().ToString("N"),
                At: DateTimeOffset.Now,
                Steps: steps,
                Caller: caller,
                GroupId: groupId));

            while (_undo.Count > Capacity) _undo.TryPop(out _);
        }

        // 里程碑（Debug）：撤销栈变化——「本次用户动作留下了什么可回退的东西」
        if (LpLog.IsEnabled(LogLevel.Debug))
            LpLog.Write(LogLevel.Debug, "engine.undo", $"撤销登记：{descriptor.Name}", props: new Dictionary<string, object?>
            {
                // cmd / caller / corr 由**调用上下文**落到记录首类字段（不在 props 里再抄一份）
                ["steps"] = steps.Count,
                ["group"] = groupId ?? string.Empty,
            });
    }

    /// <summary>构造本次调用的可撤销步骤（处理器回填优先；否则退回描述符 + 原参数）。</summary>
    private static IReadOnlyList<UndoStep> BuildSteps(
        CommandDescriptor descriptor, JsonElement args, IReadOnlyList<UndoInverseStep>? inverse)
    {
        if (inverse is { Count: > 0 })
            return inverse
                .Select(s => new UndoStep(descriptor.Name, args.Clone(), s.Command, s.Args.Clone(), s.Redo))
                .ToArray();

        if (descriptor.UndoInverse is not { Length: > 0 } fallbackInverse)
            return [];   // 无回填也无声明 → 不可撤销

        return [new UndoStep(descriptor.Name, args.Clone(), fallbackInverse, args.Clone())];
    }

    /// <summary>undo.undo 处理器在逆向命令派发成功后调（把弹出条目转入重做栈）。</summary>
    public void MarkUndone(UndoEntry entry)
    {
        lock (_lock)
        {
            _redo.Push(entry);
        }
    }
}
