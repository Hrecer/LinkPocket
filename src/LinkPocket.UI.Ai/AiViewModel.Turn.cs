using System.Globalization;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.ViewModels;

namespace LinkPocket.UI.Ai;

/// <summary>AI 页 VM 的回合面：发送 / 停止 / 回应审批。</summary>
public sealed partial class AiViewModel
{
    /// <summary>能否发送（未配置服务商 / **有回合在跑** / 输入为空 → 否；斜杠命令不走模型、未配置也可发）。
    /// "有回合在跑"= **不允许并发**：按钮置灰，只能先在跑着的那个会话里手动停止。</summary>
    public bool CanSend
        => !IsTurnRunning
           && ComposerText.Trim().Length > 0
           && (IsConfigured || ComposerText.Trim().StartsWith('/'));

    /// <summary>发送当前输入（回合上下文：当前语言 + 当前页 + @提及；以"/"开头 = 本地斜杠命令，不发给模型）。
    /// 没有当前会话时**懒建一个**（进页不再自动建会话）。</summary>
    public async Task SendAsync()
    {
        // 回合在跑 = 不允许并发：**先提示**（按钮此时已置灰；这里兜住回车/斜杠等旁路）——
        // 必须放在 CanSend 之前：否则会被"输入为空 / 未配置"的静默 return 挡掉，用户看不到原因。
        // **绝不自动停别人的回合**——现场：自动停会打断正在输出内容的会话，其输出还会串到新面板。
        if (IsTurnRunning)
        {
            Notice(LocValue.Of("ai.send.turnBusy"));
            return;
        }
        if (!CanSend) return;
        var text = ComposerText.Trim();
        var mentions = text.StartsWith('/') ? null : MentionsForSend(text);
        var sessionId = _activeSessionId ?? await EnsureDraftSessionAsync().ConfigureAwait(true);
        if (sessionId is null) return;
        // 首发即提升：草稿从此成为正式会话（引擎侧在 SendAsync 里落盘），界面标记为不再回收。
        // 斜杠命令也走同一条提升路径——它同样是"这个会话被真正用起来了"。
        PromoteDraft(sessionId);
        ComposerText = "";
        CancelMentions();
        LastErrorKey = null;
        try
        {
            if (text.StartsWith('/'))
                await TrySlashAsync(sessionId, text).ConfigureAwait(true);
            else
                await _assistant.SendAsync(sessionId, text, BuildContext(mentions)).ConfigureAwait(true);
            ClearMentions();
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
        finally
        {
            Raise(nameof(CanSend));
            CommandRefresh.Request();
        }
    }

    /// <summary>
    /// 懒建 = 取（或建）一个**草稿**：第一条消息 / 斜杠命令要用会话时才准备载体。
    /// 草稿本就存在（用户先点了「新建会话」）则直接复用同一个 id——这正是"连点不炸"的来源；
    /// 真落盘发生在引擎的 <c>SendAsync</c>（提升草稿为正式会话）里。
    /// </summary>
    private async Task<string?> EnsureDraftSessionAsync()
    {
        try
        {
            var draft = await EnsureDraftAsync().ConfigureAwait(true);
            if (draft is null) return null;
            _activeSessionId = draft.SessionId;
            ClearUsage();   // 进草稿即清用量读数（环与上下文面板一起退场；读数由 RefreshUsageAsync 的草稿闸门守着）
            Raise(nameof(ActiveSessionId));
            return draft.SessionId;
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
            return null;
        }
    }

    /// <summary>停止当前回合（立即释放写入冻结；已提交的变更不回退）。</summary>
    public async Task StopAsync()
    {
        if (_activeSessionId is null) return;
        try
        {
            // 停止按钮的语义 = "别跑了"：**取消正在跑的那个回合**——它可能不属于当前会话
            //（现场：AI 在会话 A 里跑，用户在新建的草稿界面按停止；用当前会话 id 匹配永远失败、
            // 只回一句"没有在跑的回合"，任务其实还在跑）。
            var cancelled = await _assistant.CancelTurnAsync(null).ConfigureAwait(true);
            // 取消是**协作式**的：模型思考/生成会立刻停，引擎步骤要等当前这一步做完
            //（绝不打断半截写操作）。返回 false = 这个会话当前没有在跑的回合 ——
            // 必须如实告知，否则用户对着"点了没反应"的按钮反复点（本仓禁止静默失败）。
            if (!cancelled) FlashStatus(Loc.K("ai.turn.stopIgnored"));
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>
    /// 停止当前回合并**等到它真正结束**（上限 30 秒）：取消是协作式的——
    /// 模型思考/生成文本时立即停；引擎步骤执行中会等那一步完成（绝不打断半截写操作，
    /// 否则引擎写面还占用着，紧接着的回溯/新回合照样失败）。
    /// 返回时若 <see cref="IsTurnRunning"/> 仍为 true = 超时没停下来，调用方如实告知。
    /// </summary>
    private async Task StopTurnAndWaitAsync()
    {
        // ⚠️ 这里要停的是"**正在跑的那个回合**"，而它很可能**不属于当前会话**（现场：会话 A 在跑时
        // 新建草稿并发消息——用当前会话 id 去取消永远不匹配 ⇒ 发送被挡死、草稿永远不提升）。
        // 传 null = 取消任意在跑的回合；真正的"停止按钮"仍走 StopAsync（只停当前会话，语义不变）。
        try
        {
            await _assistant.CancelTurnAsync(null).ConfigureAwait(true);
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
        for (var i = 0; i < 300 && IsTurnRunning; i++)
            await Task.Delay(100).ConfigureAwait(true);
    }

    /// <summary>回应一次审批（允许一次 / 本会话总是允许 / 拒绝 / 拒绝并停止）。</summary>
    public async Task RespondAsync(AiApprovalRow row, AiApprovalDecision decision, string? reason = null)
    {
        if (_activeSessionId is not { } sessionId) return;
        try
        {
            await _assistant.RespondToApprovalAsync(sessionId, row.ApprovalId, decision, reason)
                .ConfigureAwait(true);
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    // ── 撤销本会话 AI 变更（功能书 §2 / §7.4：按批次分组逐批退，走 undo.undo 定点，绝不第二条撤销路径）──

    private int _undoableBatches;

    /// <summary>
    /// 「撤销本会话 AI 变更」给不给（按钮与 Ctrl+Shift+Z 共用同一条 CanExecute）：
    /// **有可撤销批次**（台账 ∩ 引擎撤销栈）**且没有回合在跑**——不给会失败的按钮。
    /// </summary>
    public bool CanUndoSession => _undoableBatches > 0 && !IsTurnRunning;

    /// <summary>刷新可撤销批次数（纯读 <c>undo.list</c>；换会话 / 回合收尾 / 撤销之后各刷一次）。</summary>
    public async Task RefreshUndoableAsync()
    {
        if (_activeSessionId is not { } sessionId) return;
        try
        {
            var count = await _assistant.CountUndoableAsync(sessionId).ConfigureAwait(true);
            if (_activeSessionId != sessionId) return;   // 期间换了会话：这次读数作废，不写投影
            if (_undoableBatches == count) return;
            _undoableBatches = count;
            Raise(nameof(CanUndoSession));
            CommandRefresh.Request();
        }
        catch (Exception ex) when (ex is AiException or EngineException)
        {
            // 读数失败**不静默**：按钮保持原样 + 如实报错；下一次刷新入口（换会话 / 回合收尾 / 撤完）会再试
            LpLog.Warn("undoable batch count failed", ex, category: "ai.ledger");
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error((ex as AiException)?.Error.Code);
        }
    }

    /// <summary>撤销本会话的 AI 变更（确认弹窗在视图侧——弹窗是视图的事，VM 只发命令 + 按结果如实提示；
    /// 提示条那一步顺手重算可撤销批次，按钮跟着收起）。</summary>
    public async Task UndoSessionAsync()
    {
        if (!CanUndoSession || _activeSessionId is not { } sessionId) return;
        try
        {
            var result = await _assistant.UndoSessionAsync(sessionId).ConfigureAwait(true);
            NoticeForUndo(result);
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>
    /// 「回溯」一轮：① 回退这一轮做过的**全部操作**；② 把这一轮从会话里**去掉**（该轮及其之后的
    /// 回合、消息、工具调用、变更、审批、模型历史一并移除）；③ 把这一轮我们说过的话填回输入框等你发送。
    /// <para>三件事绑在一起才是回溯：只回退数据 → 记录还挂着；只删记录 → 库里的改动还留着；
    /// 不填回原话 → 人还得自己翻上去抄一遍。</para>
    /// </summary>
    public async Task RewindTurnAsync(AiFeedItem userItem)
    {
        if (_activeSessionId is not { } sessionId || userItem.TurnId is not { } turnId) return;
        if (IsTurnRunning)
        {
            // **回溯不再被"回合还在运行"挡住**：自动停止当前回合后继续回溯。
            // 取消是协作式的——模型在思考/生成文本时立即停；引擎步骤执行中会等那一步跑完再停
            //（不会打断半截写操作）。等不到（超时）才如实说"停不下来"。
            Notice(LocValue.Of("ai.rewind.stopping"));
            await StopTurnAndWaitAsync().ConfigureAwait(true);
            if (IsTurnRunning)
            {
                Notice(LocValue.Of("ai.rewind.busy"));
                return;
            }
        }
        try
        {
            var result = await _assistant.RewindTurnAsync(sessionId, turnId).ConfigureAwait(true);
            ComposerText = userItem.Text;   // 这一轮的原话（用户可改后再发）
            await OpenSessionAsync(sessionId).ConfigureAwait(true);   // 会话已裁过，重新投影
            NoticeForRewind(result);
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>回溯回执：数据面（撤了几项 / 有几项退不回来 / 为何退不回来）与会话面（裁了几轮）分开说。</summary>
    private void NoticeForRewind(AiRewindResult result)
    {
        _ = RefreshUndoableAsync();
        if (result.ErrorCode is { } code) LastErrorKey = LinkPocket.Views.AiKeyMap.Error(code);
        // 两种回执：全退成（done）/ 有没退成的（partial，读数里如实带 X/M）。
        // 走**状态行限时反馈**（8 秒自动恢复），不再往对话流里塞一条永久占整行的记录——
        // 实测那条记录回溯后一直钉在顶端，用户不知道它什么时候才消失。
        FlashStatus(result switch
        {
            { MissingCalls: 0, ErrorCode: null } => Loc.K("ai.rewind.done", result.RemovedTurns, result.UndoneCalls),
            _ => Loc.K("ai.rewind.partial", result.RemovedTurns, result.UndoneCalls, result.TotalCalls),
        });
    }

    private static AiTurnContext BuildContext(IReadOnlyList<AiMentionRef>? mentions = null)
        => new(NavId: "ai", LanguageCode: CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            Mentions: mentions is { Count: > 0 } ? mentions : null);
}
