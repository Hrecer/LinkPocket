using LinkPocket.Contracts;
using LinkPocket.I18n;

namespace LinkPocket.UI.Ai;

/// <summary>
/// AI 页 VM 的右栏面板面：四页签（本轮 / 本会话 / 审批 / 引擎审计）+ 过滤 + 汇总状态条 + 导出，
/// 以及左栏的会话重命名会话态。视图只做输入采集与模板，投影全在这里。
/// </summary>
public sealed partial class AiViewModel
{
    private List<AiChange> _allChanges = [];
    private List<AiToolCall> _allToolCalls = [];
    private List<AiApproval> _allApprovals = [];
    private string? _lastTurnId;
    private string? _renamingSessionId;

    private int _tabIndex;
    private string _searchText = "";
    private int _changeFilterIndex;
    private int _auditResultIndex;
    private bool _isRenamingSession;
    private string _renameText = "";
    private LocValue _summaryValue = LocValue.Empty;
    private string _auditHintKey = "ai.audit.engine.scope";
    private int _auditPage = 1;
    private int _auditPageCount = 1;
    private int _auditTotal;

    /// <summary>引擎审计行（数据源 = audit.query；载荷随行返回、默认收起）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<AiEngineRow> EngineAudit { get; } = [];

    /// <summary>页签索引：0 = 本轮变更 / 1 = 本会话变更 / 2 = 审批记录 / 3 = 引擎审计。</summary>
    public int TabIndex
    {
        get => _tabIndex;
        set
        {
            if (!Set(ref _tabIndex, value, nameof(TabIndex))) return;
            Raise(nameof(IsChangesTab));
            Raise(nameof(IsApprovalTab));
            Raise(nameof(IsEngineTab));
            if (IsEngineTab) _ = ReloadEngineAuditAsync();
        }
    }

    public bool IsChangesTab => _tabIndex is 0 or 1;
    public bool IsApprovalTab => _tabIndex == 2;
    public bool IsEngineTab => _tabIndex == 3;

    /// <summary>搜索（对象名 / 命令名；当前页签生效）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value, nameof(SearchText))) return;
            if (IsEngineTab)
            {
                _auditPage = 1;   // 过滤变化 = 回第一页（过滤后页数会变，停在旧页会翻空）
                _ = ReloadEngineAuditAsync();
            }
            else ApplyPanelFilter();
        }
    }

    /// <summary>变更分类过滤（0 = 全部；其余 = AiChangeKind 的取值 + 1）。</summary>
    public int ChangeFilterIndex
    {
        get => _changeFilterIndex;
        set
        {
            if (Set(ref _changeFilterIndex, value, nameof(ChangeFilterIndex))) ApplyPanelFilter();
        }
    }

    /// <summary>引擎审计的结果过滤（0 = 全部 / 1 = 成功 / 2 = 失败）。</summary>
    public int AuditResultIndex
    {
        get => _auditResultIndex;
        set
        {
            if (!Set(ref _auditResultIndex, value, nameof(AuditResultIndex))) return;
            if (!IsEngineTab) return;
            _auditPage = 1;
            _ = ReloadEngineAuditAsync();
        }
    }

    /// <summary>汇总状态条（含变量整句 = LocValue，渲染边界取词）。</summary>
    public LocValue SummaryValue
    {
        get => _summaryValue;
        private set => Set(ref _summaryValue, value, nameof(SummaryValue));
    }

    /// <summary>引擎审计的边界提示键（本会话范围 = 最近若干回合合并；分页归分页、边界照实说）。</summary>
    public string AuditHintKey
    {
        get => _auditHintKey;
        private set => Set(ref _auditHintKey, value, nameof(AuditHintKey));
    }

    /// <summary>引擎审计分页读数（含变量的整句 = LocValue，渲染边界取词）。</summary>
    public LocValue AuditPageValue
        => Loc.K("ai.audit.page", _auditPage, _auditPageCount, _auditTotal);

    public bool CanPrevAuditPage => _auditPage > 1;
    public bool CanNextAuditPage => _auditPage < _auditPageCount;

    /// <summary>上一页（到头不动）。</summary>
    public async Task AuditPrevAsync()
    {
        if (!CanPrevAuditPage) return;
        _auditPage--;
        await ReloadEngineAuditAsync().ConfigureAwait(true);
    }

    /// <summary>下一页（到尾不动）。</summary>
    public async Task AuditNextAsync()
    {
        if (!CanNextAuditPage) return;
        _auditPage++;
        await ReloadEngineAuditAsync().ConfigureAwait(true);
    }

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

    public void BeginRename(AiSessionSummary session)
    {
        _renamingSessionId = session.SessionId;
        RenameText = session.Title;
        IsRenamingSession = true;
    }

    public void CancelRename()
    {
        _renamingSessionId = null;
        IsRenamingSession = false;
    }

    /// <summary>提交重命名（空标题 = 回到"首条消息派生"的缺省；不做二次确认——可再改）。</summary>
    public async Task CommitRenameAsync()
    {
        if (_renamingSessionId is not { } sessionId) return;
        try
        {
            await _assistant.RenameSessionAsync(sessionId, RenameText.Trim()).ConfigureAwait(true);
            var index = Sessions.ToList().FindIndex(s => s.SessionId == sessionId);
            if (index >= 0) Sessions[index] = Sessions[index] with { Title = RenameText.Trim() };
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

    /// <summary>导出当前会话（格式由界面选择；导出物绝不含密钥）。</summary>
    public async Task<bool> ExportAuditAsync(AiExportFormat format, string outputPath)
        => await ExportSessionAsync(outputPath, format).ConfigureAwait(true);

    // ── 投影（唯一入口）──────────────────────────────────────

    /// <summary>换会话 / 进页：整份重投影（台账、审批、汇总、审计页签）。</summary>
    private void ApplySession(AiSessionDetail detail)
    {
        _allChanges = [.. detail.Changes];
        _allToolCalls = [.. detail.ToolCalls];
        _allApprovals = [.. detail.Approvals];
        _lastTurnId = detail.Changes.Count > 0 ? detail.Changes[^1].TurnId : detail.Turns.LastOrDefault()?.TurnId;
        _auditPage = 1;
        ApplyPanelFilter();
        if (IsEngineTab) _ = ReloadEngineAuditAsync();
    }

    /// <summary>台账 / 审批增量（回合进行中）：先入底账，再整份重投影。</summary>
    private void OnChangeRecorded(AiChange change)
    {
        _allChanges.Add(change);
        _lastTurnId = change.TurnId;
        ApplyPanelFilter();
        // 工具卡内联：一条变更挂回它所属的那张调用卡（功能书 §7.5）
        var card = Feed.FirstOrDefault(i => i.Kind == AiFeedItem.ItemKind.ToolCall && i.ItemId == change.CallId);
        card?.AttachChange(new AiChangeRow { Change = change });
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
        ApplyPanelFilter();
    }

    /// <summary>回合终态：刷新汇总（调用 / 失败 / 被拒 / 干跑）与引擎审计页签。</summary>
    private void OnTurnSettled()
    {
        ApplyPanelFilter();
        if (IsEngineTab) _ = ReloadEngineAuditAsync();
    }

    private void ApplyPanelFilter()
    {
        var kind = _changeFilterIndex > 0 ? (AiChangeKind?)(_changeFilterIndex - 1) : null;
        var search = _searchText.Trim();

        TurnChanges.Clear();
        SessionChanges.Clear();
        foreach (var change in _allChanges.OrderByDescending(c => c.Seq))
        {
            if (!Matches(change, kind, search)) continue;
            SessionChanges.Add(new AiChangeRow { Change = change });
            if (change.TurnId == _lastTurnId) TurnChanges.Add(new AiChangeRow { Change = change });
        }

        // 审批记录页签同样在这里重投影（回合进行中新增的审批也必须当场出现，不等换会话）
        Approvals.Clear();
        foreach (var approval in _allApprovals.OrderByDescending(a => a.Seq))
        {
            var target = DescribeTarget(approval);
            if (search.Length > 0
                && !approval.Command.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !target.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            Approvals.Add(new AiApprovalRow(approval, target));
        }

        var failed = _allToolCalls.Count(c => c.State == AiToolCallState.Failed);
        var rejected = _allApprovals.Count(a => a.Decision is AiApprovalDecision.Reject or AiApprovalDecision.RejectAndStop);
        var dryRun = _allToolCalls.Count(c => c.IsDryRun);
        SummaryValue = Loc.K("ai.audit.summary", _allChanges.Count, failed, rejected, dryRun);
    }

    private static bool Matches(AiChange change, AiChangeKind? kind, string search)
    {
        if (kind is { } expected && change.Kind != expected) return false;
        if (search.Length == 0) return true;
        return change.Command.Contains(search, StringComparison.OrdinalIgnoreCase)
               || (change.EntityName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
               || change.EntityId.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ReloadEngineAuditAsync()
    {
        if (_activeSessionId is not { } sessionId) return;
        try
        {
            var success = _auditResultIndex switch { 1 => true, 2 => false, _ => (bool?)null };
            var page = await _assistant.QueryEngineAuditAsync(new AiAuditQuery(
                sessionId,
                TurnId: null,
                Search: _searchText.Trim().Length > 0 ? _searchText.Trim() : null,
                Success: success,
                IncludePayloads: true,
                Page: _auditPage,
                PerPage: AuditPageSize)).ConfigureAwait(true);

            EngineAudit.Clear();
            foreach (var row in page.Items) EngineAudit.Add(new AiEngineRow { Call = row });
            _auditPage = Math.Max(1, page.Page);
            _auditPageCount = Math.Max(1, page.PageCount);
            _auditTotal = page.Total;
            Raise(nameof(AuditPageValue));
            Raise(nameof(CanPrevAuditPage));
            Raise(nameof(CanNextAuditPage));
            AuditHintKey = "ai.audit.engine.scope";
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>引擎审计单页行数（功能书 §7.6：单页 50 条 + 分页）。</summary>
    private const int AuditPageSize = 50;
}
