using LinkPocket.Contracts;
using LinkPocket.I18n;

namespace LinkPocket.UI.Ai;

/// <summary>
/// AI 页 VM 的台账面：把会话详情里的**变更 / 工具调用 / 审批**收进底账，并投影出对话流要用的东西
/// （审批行、内联变更卡、回合分隔行的"变更 M 项"），以及左栏的会话重命名。
///
/// <para><b>2026-09-25 简化</b>：原先这里还有一整面"右栏变更与审计"（四页签 + 过滤 + 汇总条 + 导出）。
/// 右栏已按产品决定删除（功能与对话流重复、占宽），所以过滤/分页/汇总/审计行这些**只为那一栏存在**的
/// 投影一并退场。保留下来的是**对话流与审批卡真正依赖的部分**——注意
/// <see cref="Approvals"/> 不是"右栏的列表"：对话里审批卡上的「允许/拒绝」按钮是靠它按审批 ID 回查行的
///（<c>AiView.OnApproveOnce</c> 一路过来就查这里），删了它审批卡会变成点不动的死卡。</para>
/// </summary>
public sealed partial class AiViewModel
{
    private List<AiChange> _allChanges = [];
    private List<AiToolCall> _allToolCalls = [];
    private List<AiApproval> _allApprovals = [];
    private string? _renamingSessionId;
    private bool _isRenamingSession;
    private string _renameText = "";

    // ── 会话重命名（左栏）─────────────────────────────────────

    public bool IsRenamingSession
    {
        get => _isRenamingSession;
        private set => Set(ref _isRenamingSession, value, nameof(IsRenamingSession));
    }

    public string RenameText
    {
        get => _renameText;
        set => Set(ref _renameText, value, nameof(RenameText));
    }

    /// <summary>
    /// 进入就地重命名。**先清掉上一行的编辑态**——早先只把 <c>_renamingSessionId</c> 换成新行、
    /// 不碰旧行的标志，于是"改 A 改到一半又去改 B"会把 A 永久留在编辑态：<see cref="CancelRename"/>
    /// 只会收掉当前那一个 id，A 的 <c>IsRenaming</c> 再也没人碰 → 界面上一行空编辑框卡死（用户实测）。
    /// </summary>
    public void BeginRename(AiSessionRow session)
    {
        foreach (var row in Sessions)
            if (!ReferenceEquals(row, session)) row.IsRenaming = false;
        _renamingSessionId = session.SessionId;
        RenameText = session.Title;
        session.IsRenaming = true;
        IsRenamingSession = true;
    }

    /// <summary>退出重命名（**清全部行**而不只是当前 id：任何残留的编辑态都是卡死的空编辑框）。</summary>
    public void CancelRename()
    {
        foreach (var row in Sessions) row.IsRenaming = false;
        _renamingSessionId = null;
        IsRenamingSession = false;
    }

    /// <summary>提交重命名（空标题 = 回到"首条消息派生"的缺省；不做二次确认——可再改）。</summary>
    public async Task CommitRenameAsync()
    {
        if (_renamingSessionId is not { } sessionId) return;
        var title = RenameText.Trim();
        try
        {
            await _assistant.RenameSessionAsync(sessionId, title).ConfigureAwait(true);
            var index = Sessions.ToList().FindIndex(s => s.SessionId == sessionId);
            if (index >= 0) Sessions[index].Apply(Sessions[index].Summary with { Title = title });
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
        finally
        {
            CancelRename();
        }
    }

    // ── 无会话空态 ────────────────────────────────────────────

    /// <summary>
    /// 回到「没有会话」（进页时一条会话都没有 / 把最后一条删了）：清空对话流与台账。
    /// **不建新会话**——空就是空；用户真要说话时由 <c>SendAsync</c> 懒建（见 AiViewModel.Turn.cs）。
    /// </summary>
    private void ClearConversation()
    {
        _activeSessionId = null;
        Raise(nameof(ActiveSessionId));
        ClearConversationContent();
    }

    /// <summary>只清对话流 / 台账，**不动 <c>_activeSessionId</c>**
    /// （草稿态停在空白草稿上时用它：会话 id 要保留，内容本就是空的）。</summary>
    private void ClearConversationContent()
    {
        Feed.Clear();
        Turns.Clear();
        _allChanges.Clear();
        _allToolCalls.Clear();
        _allApprovals.Clear();
        _undoableBatches = 0;
        ProjectApprovals();
        Raise(nameof(CanUndoSession));
        Raise(nameof(ShowRail));
        IsTurnRunning = false;
        StatusKey = "ai.status.idle";
        ClearMentions();
    }

    // ── 投影（唯一入口）──────────────────────────────────────

    /// <summary>换会话 / 进页：整份重投影（底账、审批行）。</summary>
    private void ApplySession(AiSessionDetail detail)
    {
        _allChanges = [.. detail.Changes];
        _allToolCalls = [.. detail.ToolCalls];
        _allApprovals = [.. detail.Approvals];
        ProjectApprovals();
        _ = RefreshUndoableAsync();   // 换会话：撤销按钮给不给按新会话的可撤销批次算
    }

    /// <summary>台账增量（回合进行中）：先入底账，再把变更内联挂回它所属的那张调用行。</summary>
    private void OnChangeRecorded(AiChange change)
    {
        _allChanges.Add(change);
        // 工具行内联：一条变更挂回它所属的那张调用行（功能书 §7.5）
        var card = Feed.FirstOrDefault(i => i.Kind == AiFeedItem.ItemKind.ToolCall && i.ItemId == change.CallId);
        card?.AttachChange(new AiChangeRow { Change = change });
        // 回合分隔行的"变更 M 项"当场跟上（回合状态通知不一定紧跟着来）
        HeaderOf(change.TurnId)?.SetChangeCount(
            _allChanges.Count(c => string.Equals(c.TurnId, change.TurnId, StringComparison.Ordinal)));
    }

    /// <summary>换会话：把每个工具调用的变更内联挂回它的卡片（对话是本地状态，随详情一次投影）。</summary>
    private void AttachInlineChanges()
    {
        foreach (var item in Feed)
        {
            if (item.Kind != AiFeedItem.ItemKind.ToolCall || item.ToolCall is not { } call) continue;
            foreach (var change in _allChanges.Where(c => c.CallId == call.CallId).OrderBy(c => c.Seq))
                item.AttachChange(new AiChangeRow { Change = change });
        }
    }

    private void OnApprovalRecorded(AiApproval approval)
    {
        var index = _allApprovals.FindIndex(a => a.ApprovalId == approval.ApprovalId);
        if (index >= 0) _allApprovals[index] = approval;
        else _allApprovals.Add(approval);
        ProjectApprovals();
    }

    /// <summary>回合终态：刷新可撤销读数与用量（台账本身已随每次通知增量入账）。</summary>
    private void OnTurnSettled()
    {
        ProjectApprovals();
        _ = RefreshUndoableAsync();   // 回合里新产生的可撤销批次要让撤销入口出现
        _ = RefreshUsageAsync();
    }

    /// <summary>
    /// 审批行的唯一投影：按时间倒序重建成 <see cref="Approvals"/>（含目标摘要，读引擎给的事实，不看模型怎么说）。
    /// **必须保留**——对话流里的审批卡按钮靠它按审批 ID 回查（<c>PrepareRespond</c> 路径）。
    /// </summary>
    private void ProjectApprovals()
    {
        Approvals.Clear();
        foreach (var approval in _allApprovals.OrderByDescending(a => a.Seq))
            Approvals.Add(new AiApprovalRow(approval, DescribeTarget(approval)));
    }
}
