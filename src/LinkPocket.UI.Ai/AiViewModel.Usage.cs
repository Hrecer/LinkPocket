using LinkPocket.Contracts;
using LinkPocket.I18n;

namespace LinkPocket.UI.Ai;

/// <summary>
/// AI 页 VM 的用量面与运行反馈（口径见功能书 §5.6 / §7.6）：
/// 用量行（本会话轮数 / 工具调用 / token 合计）、批进度读数（轮询而来的 <see cref="AiBatchProgress"/>）、
/// 限流等待的瞬时状态行（如实读数优先，读不到就回落到按键取词）。
/// </summary>
public sealed partial class AiViewModel
{
    private AiSessionUsage? _usage;
    private AiBatchProgress? _progress;
    private LocValue? _statusOverride;

    /// <summary>本会话用量行（审计面板底部状态条；读数未取到 = 空）。</summary>
    public LocValue UsageValue => _usage is null
        ? LocValue.Empty
        : Loc.K("ai.usage.line", _usage.Turns, _usage.ToolCalls, _usage.InputTokens, _usage.OutputTokens);

    /// <summary>输入区用量环：有没有读数（新会话 / 还没发过消息 = 没有 → 空环，不编造）。</summary>
    public bool HasContextUsage => _usage is { ContextTokens: > 0, ContextWindowTokens: > 0 };

    /// <summary>上下文占用百分比（0–100；无读数 = 0）。</summary>
    public double ContextUsagePercent => HasContextUsage
        ? Math.Clamp(_usage!.ContextTokens!.Value * 100.0 / _usage.ContextWindowTokens, 0, 100)
        : 0;

    /// <summary>用量环的悬浮提示：估算占用 / 窗口（本地估算口径与压缩阈值同源）。</summary>
    public LocValue UsageTipValue => HasContextUsage
        ? Loc.K("ai.usage.context", _usage!.ContextTokens!.Value, _usage.ContextWindowTokens)
        : LocValue.Of("ai.usage.context.none");

    /// <summary>悬浮面板的标题行读数：「已用 / 窗口 (百分比)」；无读数 = 空。</summary>
    public LocValue ContextSummaryValue => HasContextUsage
        ? Loc.K("ai.usage.panel.summary",
            FormatTokens(_usage!.ContextTokens!.Value),
            FormatTokens(_usage.ContextWindowTokens),
            (ContextUsagePercent / 100).ToString("P1", CurrentCulture))
        : LocValue.Empty;

    /// <summary>悬浮面板的分项行（来源名 + 占比；按 token 降序，空表 = 面板只显示摘要）。</summary>
    public IReadOnlyList<AiContextSourceRow> ContextSourceRows => _contextRows;

    /// <summary>悬浮面板有没有分项（没有 = 只显示标题与摘要，不摆空列表）。</summary>
    public bool HasContextSources => _contextRows.Count > 0;

    private IReadOnlyList<AiContextSourceRow> _contextRows = [];
    private bool _contextPanelOpen;

    /// <summary>用量环的容量面板是否展开（悬浮 / 聚焦时开；离开且未聚焦时合）。</summary>
    public bool IsContextPanelOpen
    {
        get => _contextPanelOpen;
        set
        {
            if (_contextPanelOpen == value) return;
            _contextPanelOpen = value;
            Raise(nameof(IsContextPanelOpen));
        }
    }

    /// <summary>把引擎给出的分项折算成界面行（占比 = 该项 / 总量；总量 0 = 空表）。</summary>
    private void RebuildContextRows()
    {
        var breakdown = _usage?.Breakdown;
        if (breakdown is not { Count: > 0 })
        {
            _contextRows = [];
            return;
        }

        var total = breakdown.Sum(i => i.Tokens);
        if (total <= 0)
        {
            _contextRows = [];
            return;
        }

        _contextRows = breakdown
            .Select((item, index) => new AiContextSourceRow(
                Loc.K($"ai.usage.source.{SourceKey(item.Source)}"),
                item.Tokens,
                item.Tokens * 100.0 / total,
                index))
            .ToList();
    }

    /// <summary>分项枚举 → 文案键尾段（键形固定，英文侧逐条对应）。</summary>
    private static string SourceKey(AiContextSource source) => source switch
    {
        AiContextSource.SystemPrompt => "systemPrompt",
        AiContextSource.PageContext => "pageContext",
        AiContextSource.Mentions => "mentions",
        AiContextSource.Skills => "skills",
        AiContextSource.ToolSchemas => "toolSchemas",
        _ => "messages",
    };

    /// <summary>token 数按千位分隔（与摘要行同口径；不缩写，避免 1.2k 这种二次换算）。</summary>
    private static string FormatTokens(long value) => value.ToString("N0", CurrentCulture);

    private static System.Globalization.CultureInfo CurrentCulture
        => System.Globalization.CultureInfo.CurrentCulture;

    /// <summary>状态行文案：瞬时读数（限流等待 / 回溯回执等）优先，否则按键取词。</summary>
    public LocValue StatusValue => _statusOverride ?? LocValue.Of(StatusKey);

    /// <summary>状态行是否显示：回合进行中，或有一条**限时反馈**（回溯回执等）正在展示。</summary>
    public bool IsProgressVisible => IsTurnRunning || _statusOverride is not null;

    private System.Threading.CancellationTokenSource? _statusFlashCts;

    /// <summary>
    /// 状态行限时反馈：显示 <paramref name="seconds"/> 秒后自动恢复——
    /// 回溯这类"一次性回执"不再往对话流里塞一条**永久占一整行**的记录（实测：回溯完成后
    /// 那条记录一直钉在顶端，不知道什么时候才消失）。被下一次状态变化覆盖即提前结束。
    /// </summary>
    private void FlashStatus(LocValue value, int seconds = 8)
    {
        _statusFlashCts?.Cancel();
        _statusFlashCts?.Dispose();
        var cts = new System.Threading.CancellationTokenSource();
        _statusFlashCts = cts;
        _statusOverride = value;
        Raise(nameof(StatusValue));
        Raise(nameof(IsProgressVisible));
        _ = Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (!ReferenceEquals(_statusFlashCts, cts)) return;   // 已被新的反馈接管
            _statusOverride = null;
            Raise(nameof(StatusValue));
            Raise(nameof(IsProgressVisible));
        }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>清空本会话用量读数（换到草稿 / 会话不可用时调用；环随之退场，不编造数字）。</summary>
    private void ClearUsage()
    {
        if (_usage is null && _contextRows.Count == 0) return;
        _usage = null;
        _contextRows = [];
        Raise(nameof(UsageValue));
        Raise(nameof(HasContextUsage));
        Raise(nameof(ContextUsagePercent));
        Raise(nameof(UsageTipValue));
        Raise(nameof(ContextSummaryValue));
        Raise(nameof(ContextSourceRows));
        Raise(nameof(HasContextSources));
    }

    /// <summary>进度文字（知道总步数才有；"正在执行 12/40 步"）。</summary>
    public LocValue ProgressTextValue => _progress is { Total: > 0 } batch
        ? Loc.K("ai.progress.steps", batch.Completed, batch.Total)
        : LocValue.Empty;

    /// <summary>刷新本会话用量读数（回合收尾 / 换会话 / 进页）。</summary>
    public async Task RefreshUsageAsync()
    {
        // **草稿态必须清掉读数**，两个口子都堵上：
        // ① 没有活动会话；② 活动的是**内存草稿**（新建对话、正在打字）——草稿也是一条真实会话，
        //    它的用量读数是"系统提示 + 工具定义"的估算（实测 55.6%，大头是工具定义 72%），
        //    用户一条消息都还没发，环亮着就是在编造"已经用掉这么多"（参照实现：草稿态根本不渲染）。
        // ⚠️ 判据必须带 State：草稿**提升为正式会话后 `_draft` 仍被保留**（Promoted 态，防误回收），
        //    只看 SessionId 会把真实会话一直误判成草稿 ⇒ 每次刷新都 ClearUsage（实测症状：
        //    发完消息回合收尾刷新了读数，面板仍是"还没有上下文读数"）。
        var isDraftSession = _activeSessionId is { } id && _draft is { } d && d.SessionId == id
            && d.State == DraftState.Draft;
        if (_activeSessionId is not { } sessionId || isDraftSession)
        {
            ClearUsage();
            return;
        }
        try
        {
            var usage = await _assistant.GetSessionUsageAsync(sessionId).ConfigureAwait(true);
            if (_activeSessionId != sessionId) return;   // 期间换了会话：这次读数作废
            // ⚠️ 期间**变成草稿**同样作废：草稿也是一条真实会话，引擎会返回"系统提示 + 工具定义"的**估算**
            //（实测 53.0%、工具定义占 75.9%）——它看着像"已经用掉这么多"，而用户一条消息都还没发。
            // 时序窗口：新建会话时 `_activeSessionId` 先指向新草稿、`_draft` 还是旧的 ⇒ 上面的草稿判据漏判。
            // 同样必须带 State：已提升（Promoted）的会话不是草稿，别把刚取回的真实读数又清掉。
            if (_activeSessionId is { } current && _draft is { } draftNow && draftNow.SessionId == current
                && draftNow.State == DraftState.Draft)
            {
                ClearUsage();
                return;
            }
            _usage = usage;
            RebuildContextRows();
            Raise(nameof(UsageValue));
            Raise(nameof(HasContextUsage));
            Raise(nameof(ContextUsagePercent));
            Raise(nameof(UsageTipValue));
            Raise(nameof(ContextSummaryValue));
            Raise(nameof(ContextSourceRows));
            Raise(nameof(HasContextSources));
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>批进度读数（通知驱动；回合终态清空）。</summary>
    private void ApplyProgress(AiBatchProgress progress)
    {
        _progress = progress;
        Raise(nameof(ProgressTextValue));
    }

    private void ClearProgress()
    {
        if (_progress is null) return;
        _progress = null;
        Raise(nameof(ProgressTextValue));
    }

    /// <summary>限流等待的瞬时状态（秒数按毫秒上线；回合收尾或下一次状态变化即清）。</summary>
    private void ApplyRateLimit(AiRateLimitNotice notice)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(notice.RetryAfterMs / 1000.0));
        _statusOverride = notice.Retried ? Loc.K("ai.progress.limited", seconds) : null;
        Raise(nameof(StatusValue));
    }

    private void ClearStatusOverride()
    {
        if (_statusOverride is null) return;
        _statusOverride = null;
        Raise(nameof(StatusValue));
    }
}
