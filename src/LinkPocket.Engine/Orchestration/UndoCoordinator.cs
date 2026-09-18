using System.Collections.Concurrent;
using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 撤销协调器（方案 4.3 IUndoCoordinator）：纯状态机（撤销栈 + 重做栈，上限 100 条）。
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
        var count = _undo.Count + _redo.Count;
        _undo.Clear();
        _redo.Clear();
        return Task.FromResult(count);
    }

    public Task<UndoEntry?> TakeUndoAsync(string? id, CancellationToken ct)
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

    public Task<UndoEntry?> TakeRedoAsync(CancellationToken ct)
        => Task.FromResult(_redo.TryPop(out var entry) ? entry : null);

    public void Record(CommandDescriptor descriptor, JsonElement args, CallerRef caller)
    {
        if (!descriptor.IsMutation || descriptor.UndoInverse is not { Length: > 0 } inverse)
            return;

        _redo.Clear();   // 新撤销使重做链失效（标准 redo 语义）
        _undo.Push(new UndoEntry(
            Id: Guid.NewGuid().ToString("N"),
            At: DateTimeOffset.Now,
            Command: descriptor.Name,
            Args: args.Clone(),
            InverseCommand: inverse,
            InverseArgs: args.Clone(),
            Caller: caller));

        while (_undo.Count > Capacity) _undo.TryPop(out _);
    }

    /// <summary>undo.undo 处理器在逆向命令派发成功后调（把弹出条目转入重做栈）。</summary>
    public void MarkUndone(UndoEntry entry) => _redo.Push(entry);
}
