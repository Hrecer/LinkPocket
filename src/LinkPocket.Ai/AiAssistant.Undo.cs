using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 撤销面（<see cref="IAiAssistant.UndoLastTurnAsync"/>）：撤销**上一轮（最近回合）**的 AI 变更。
///
/// <para><b>口径（功能书 §7.4）</b>：走引擎既有 <c>undo.undo</c>（一条记录 = 一次用户动作），不做第二条撤销路径；
/// 定位靠归属键（<c>CallOptions.UndoGroupId</c> = 工具调用 ID，与台账可撤销性核对同一把钥匙）。
/// 引擎未登记逆向的变更（改名/改属性、事务批、宏、查重、永久删除）本来就不在撤销栈里——
/// 如实跳过、如实计数，**绝不给会失败的撤销**。</para>
/// </summary>
public sealed partial class AiAssistant
{
    public async Task<AiUndoResult> UndoLastTurnAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (IsTurnRunning) throw Busy(sessionId);
        var file = _sessionStore.Load(sessionId) ?? throw NotFound(sessionId);
        var lastTurn = file.Turns.LastOrDefault();
        if (lastTurn is null) return new AiUndoResult(0, 0, 0, null);

        // 本轮标记为可撤销的工具调用（台账已在执行时与撤销栈核对过），新者先撤
        var callIds = file.Changes
            .Where(change => string.Equals(change.TurnId, lastTurn.TurnId, StringComparison.Ordinal) && change.Undoable)
            .Select(change => change.CallId)
            .Distinct(StringComparer.Ordinal)
            .Reverse()
            .ToList();
        if (callIds.Count == 0) return new AiUndoResult(0, 0, 0, null);

        // 撤销栈现状（进程内、重启清空）：只有仍持有归属记录的调用才算"可撤销集合"
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
                entryByGroup[group] = id;   // 栈序最新在前；同组只可能出现一条（Record 合并）
            }
        }

        var candidates = callIds.Where(entryByGroup.ContainsKey).ToList();
        if (candidates.Count == 0) return new AiUndoResult(0, 0, 0, null);

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
