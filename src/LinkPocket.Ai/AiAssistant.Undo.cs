using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 撤销面（<see cref="IAiAssistant.UndoLastTurnAsync"/> / <see cref="IAiAssistant.UndoSessionAsync"/> /
/// <see cref="IAiAssistant.CountUndoableAsync"/>）：撤销上一轮 / 本会话的 AI 变更。
///
/// <para><b>口径（功能书 §7.4）</b>：走引擎既有 <c>undo.undo</c>（一条记录 = 一次用户动作），**不做第二条撤销路径**；
/// 定位靠归属键（<c>CallOptions.UndoGroupId</c> = 工具调用 ID，与台账可撤销性核对同一把钥匙）——
/// 一次批/宏 = 一个归属键 = 撤销栈里的一条记录（N 个逆向步），所以"按批次分组逐批退"就是逐归属键退。
/// 引擎未登记逆向的变更（改名/改属性、查重、永久删除…）本来就不在撤销栈里——如实跳过、如实计数，
/// **绝不给会失败的撤销**；按钮给不给由 <see cref="CountUndoableAsync"/>（同一把钥匙的纯读）决定。</para>
/// </summary>
public sealed partial class AiAssistant
{
    public Task<AiUndoResult> UndoLastTurnAsync(string sessionId, CancellationToken ct = default)
    {
        var file = LoadForUndo(sessionId);
        var lastTurn = file.Turns.LastOrDefault();
        return lastTurn is null
            ? UndoCallIdsAsync(sessionId, [], ct)
            : UndoTurnCoreAsync(sessionId, file, lastTurn.TurnId, ct);
    }

    /// <summary>
    /// **回溯**：先撤销该轮的数据操作，再把会话裁到该轮之前。两件事必须都做，缺一件就不是回溯
    /// （只撤数据 → 记录还挂着；只裁记录 → 库里的改动还留着）。
    /// </summary>
    public async Task<AiRewindResult> RewindTurnAsync(string sessionId, string turnId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        var file = LoadForUndo(sessionId);   // 回合在跑 → Busy；会话不存在 → NotFound

        // ① 数据面：先撤（此时回合记录还在，CallIdsNewestFirst 要靠它取归属键）
        var undo = await UndoTurnCoreAsync(sessionId, file, turnId, ct).ConfigureAwait(false);

        // ② 会话面：裁掉该轮及其之后（重新 Load 一次，撤销过程中文件已被引擎写过）
        file = _sessionStore.Load(sessionId) ?? throw NotFound(sessionId);
        var turns = file.Turns.OrderBy(t => t.Index).ToList();
        var cut = turns.FindIndex(t => string.Equals(t.TurnId, turnId, StringComparison.Ordinal));
        if (cut < 0)
            return new AiRewindResult(undo.TotalCalls, undo.UndoneCalls, undo.MissingCalls, 0, 0, undo.ErrorCode);

        var dropped = turns.Skip(cut).ToList();
        var droppedIds = dropped.Select(t => t.TurnId).ToHashSet(StringComparer.Ordinal);
        file.Turns.RemoveAll(t => droppedIds.Contains(t.TurnId));
        var removedMessages = file.Messages.RemoveAll(m => m.TurnId is { } id && droppedIds.Contains(id));
        file.ToolCalls.RemoveAll(c => droppedIds.Contains(c.TurnId));
        file.Changes.RemoveAll(c => droppedIds.Contains(c.TurnId));
        file.Approvals.RemoveAll(a => droppedIds.Contains(a.TurnId));

        TrimChatForDroppedTurns(file, dropped.Count);
        _sessionStore.Save(file);

        Notified?.Invoke(new AiNotification(AiNotificationKind.SessionChanged, sessionId,
            Session: file.Summary));
        return new AiRewindResult(undo.TotalCalls, undo.UndoneCalls, undo.MissingCalls,
            dropped.Count, removedMessages, undo.ErrorCode);
    }

    /// <summary>
    /// 裁掉模型历史末尾的 N 段。模型历史是**一维**的（没有回合 id），但每轮都以一条 <c>user</c> 开头，
    /// 所以从尾部往回数 N 个段起点就是切割点。**从尾往头数**（不是从头往尾数）：压缩会往头部插一条
    /// 合成的 <c>user</c> 摘要，从头数会被它错开一位。
    /// </summary>
    private static void TrimChatForDroppedTurns(AiSessionFile file, int droppedTurns)
    {
        if (droppedTurns <= 0) return;
        var cut = 0;
        var seen = 0;
        for (var i = file.Chat.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(file.Chat[i].Role, "user", StringComparison.Ordinal)) continue;
            seen++;
            if (seen < droppedTurns) continue;
            cut = i;
            break;
        }
        if (seen < droppedTurns) { file.Chat.Clear(); return; }   // 历史比回合短：整段清掉（如实，不猜边界）
        if (cut < file.Chat.Count) file.Chat.RemoveRange(cut, file.Chat.Count - cut);
    }

    private Task<AiUndoResult> UndoTurnCoreAsync(string sessionId, AiSessionFile file, string turnId,
        CancellationToken ct)
        => UndoCallIdsAsync(sessionId,
            CallIdsNewestFirst(file, change => string.Equals(change.TurnId, turnId, StringComparison.Ordinal)), ct);

    public Task<AiUndoResult> UndoSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var file = LoadForUndo(sessionId);
        return UndoCallIdsAsync(sessionId, CallIdsNewestFirst(file, static _ => true), ct);
    }

    public async Task<int> CountUndoableAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var file = _sessionStore.Load(sessionId) ?? throw NotFound(sessionId);
        var callIds = CallIdsNewestFirst(file, static _ => true);
        if (callIds.Count == 0) return 0;
        var entryByGroup = await ReadUndoStackAsync(sessionId, ct).ConfigureAwait(false);
        return callIds.Count(entryByGroup.ContainsKey);
    }

    /// <summary>撤销前的入口校验（回合在跑 = 引擎写面正被占用，拒绝而不是排队）。</summary>
    private AiSessionFile LoadForUndo(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (IsTurnRunning) throw Busy(sessionId);
        return _sessionStore.Load(sessionId) ?? throw NotFound(sessionId);
    }

    /// <summary>本会话台账里"引擎登记过逆向"的工具调用（= 归属键集合），**新者先撤**。</summary>
    private static List<string> CallIdsNewestFirst(AiSessionFile file, Func<AiChange, bool> inScope)
        => file.Changes
            .Where(change => inScope(change) && change.Undoable)
            .Select(change => change.CallId)
            .Distinct(StringComparer.Ordinal)
            .Reverse()
            .ToList();

    /// <summary>撤销栈现状（进程内、重启清空）：归属键 → 记录 ID（栈序最新在前；同组只可能有一条）。</summary>
    private async Task<Dictionary<string, string>> ReadUndoStackAsync(string sessionId, CancellationToken ct)
    {
        var options = new CallOptions(Caller: new CallerRef(CallerKind.Agent, sessionId));
        var list = await _client.QueryAsync<JsonElement>("undo.list", null, options, ct).ConfigureAwait(false);
        var entryByGroup = new Dictionary<string, string>(StringComparer.Ordinal);
        if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("entries", out var entries)
            && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var group = Str(entry, "group_id");
                var id = Str(entry, "id");
                if (group is null || id is null) continue;
                entryByGroup[group] = id;
            }
        }
        return entryByGroup;
    }

    /// <summary>逐批定点撤销（新者先撤）；只撤"仍在栈里"的那部分，其余如实计数为不可撤销。</summary>
    private async Task<AiUndoResult> UndoCallIdsAsync(string sessionId, IReadOnlyList<string> callIds,
        CancellationToken ct)
    {
        if (callIds.Count == 0) return new AiUndoResult(0, 0, 0, null);

        var entryByGroup = await ReadUndoStackAsync(sessionId, ct).ConfigureAwait(false);
        var candidates = callIds.Where(entryByGroup.ContainsKey).ToList();
        if (candidates.Count == 0) return new AiUndoResult(0, 0, 0, null);

        var options = new CallOptions(Caller: new CallerRef(CallerKind.Agent, sessionId));
        var undone = 0;
        string? firstError = null;
        foreach (var callId in candidates)
        {
            try
            {
                await _client.ExecuteAsync<object>("undo.undo", new { id = entryByGroup[callId] }, options, ct)
                    .ConfigureAwait(false);
                undone++;
            }
            catch (EngineException ex)
            {
                // 部分失败立即停止并如实上报（后续条目保持原状，用户可再触发）
                firstError = ex.Error.Code;
                break;
            }
        }

        var result = new AiUndoResult(candidates.Count, undone, candidates.Count - undone, firstError);
        Notified?.Invoke(new AiNotification(AiNotificationKind.SessionChanged, sessionId,
            Session: _sessionStore.Load(sessionId)?.Summary));
        return result;
    }
}
