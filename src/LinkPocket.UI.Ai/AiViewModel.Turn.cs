using System.Globalization;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.ViewModels;

namespace LinkPocket.UI.Ai;

/// <summary>AI 页 VM 的回合面：发送 / 停止 / 回应审批。</summary>
public sealed partial class AiViewModel
{
    private string _approvalReason = "";

    /// <summary>审批理由输入（原样回灌模型；可空）。</summary>
    public string ApprovalReason
    {
        get => _approvalReason;
        set => Set(ref _approvalReason, value, nameof(ApprovalReason));
    }

    /// <summary>能否发送（未配置服务商 / 正在跑回合 / 输入为空 → 否；斜杠命令不走模型、未配置也可发）。</summary>
    public bool CanSend
        => !IsTurnRunning && ComposerText.Trim().Length > 0
           && (IsConfigured || ComposerText.Trim().StartsWith('/'));

    /// <summary>发送当前输入（回合上下文：当前语言 + 当前页 + @提及；以"/"开头 = 本地斜杠命令，不发给模型）。</summary>
    public async Task SendAsync()
    {
        if (!CanSend || _activeSessionId is not { } sessionId) return;
        var text = ComposerText.Trim();
        var mentions = text.StartsWith('/') ? null : MentionsForSend(text);
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

    /// <summary>停止当前回合（立即释放写入冻结；已提交的变更不回退）。</summary>
    public async Task StopAsync()
    {
        if (_activeSessionId is not { } sessionId) return;
        try
        {
            await _assistant.CancelTurnAsync(sessionId).ConfigureAwait(true);
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>回应一次审批（允许一次 / 本会话总是允许 / 拒绝 / 拒绝并停止）。</summary>
    public async Task RespondAsync(AiApprovalRow row, AiApprovalDecision decision)
    {
        if (_activeSessionId is not { } sessionId) return;
        try
        {
            await _assistant.RespondToApprovalAsync(sessionId, row.ApprovalId, decision, ApprovalReason)
                .ConfigureAwait(true);
            ApprovalReason = "";
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

    private static AiTurnContext BuildContext(IReadOnlyList<AiMentionRef>? mentions = null)
        => new(NavId: "ai", LanguageCode: CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            Mentions: mentions is { Count: > 0 } ? mentions : null);
}
