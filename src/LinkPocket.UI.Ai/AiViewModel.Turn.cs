using System.Globalization;
using LinkPocket.Contracts;

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

    /// <summary>能否发送（未配置服务商 / 正在跑回合 / 输入为空 → 否）。</summary>
    public bool CanSend => IsConfigured && !IsTurnRunning && ComposerText.Trim().Length > 0;

    /// <summary>发送当前输入（回合上下文：当前语言 + 当前页；更完整的位置/选中上下文见 P2）。</summary>
    public async Task SendAsync()
    {
        if (!CanSend || _activeSessionId is not { } sessionId) return;
        var text = ComposerText.Trim();
        ComposerText = "";
        LastErrorKey = null;
        try
        {
            await _assistant.SendAsync(sessionId, text, BuildContext()).ConfigureAwait(true);
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
        finally
        {
            Raise(nameof(CanSend));
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

    private static AiTurnContext BuildContext()
        => new(NavId: "ai", LanguageCode: CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
}
