using LinkPocket.Contracts;

namespace LinkPocket.App.Tests;

/// <summary>
/// <see cref="IAiAssistant"/> 测试桩：只记录调用、按预置数据应答（不碰文件、不碰网络）。
/// 断言口径 = 可观测结果（记录到的入参 / VM 投影状态），与 App.Tests 其余 VM 金标准一致。
/// </summary>
public sealed class StubAiAssistant : IAiAssistant
{
    // ── 预置数据 ─────────────────────────────────────────────
    public List<AiProviderInfo> Providers { get; } = [];
    public List<AiSessionSummary> Sessions { get; } = [];
    public AiPreferences Preferences { get; set; } = new();
    public AiSelectionResolution Selection { get; set; } = new(null, AiSelectionIssue.ProviderMissing, null, null);
    public AiUndoResult UndoResult { get; set; } = new(0, 0, 0, null);
    public int AuditPageCount { get; set; } = 1;

    // ── 调用记录 ─────────────────────────────────────────────
    public List<(string SessionId, string Text)> SendCalls { get; } = [];
    public List<AiTurnContext?> SendContexts { get; } = [];
    public List<(string SessionId, AiMode Mode)> SetModeCalls { get; } = [];
    public List<AiModelDraft> SaveModelCalls { get; } = [];
    /// <summary>DeleteProviderAsync 的记录（服务商删除权限用例看它有没有被调到）。</summary>
    public List<string> DeleteProviderCalls { get; } = [];
    /// <summary>SaveProviderAsync 的记录（「拉取模型前先落地址草稿」用例看它有没有被调到）。</summary>
    public List<AiProviderDraft> SaveProviderCalls { get; } = [];
    /// <summary>RefreshModelsAsync（拉取模型）的记录；空地址用例断言它**没有**被调到。</summary>
    public List<string> RefreshModelsCalls { get; } = [];
    /// <summary>TestConnectivityAsync 的记录（同口径）。</summary>
    public List<string> TestCalls { get; } = [];
    public List<AiPreferences> SavePreferencesCalls { get; } = [];
    public List<AiAuditQuery> AuditQueries { get; } = [];
    public List<(string SessionId, string ApprovalId, AiApprovalDecision Decision, string? Reason)> ApprovalCalls { get; } = [];
    public List<string> UndoSessionCalls { get; } = [];
    public List<string> CountUndoableCalls { get; } = [];
    /// <summary>CountUndoableAsync 的预置读数（= 面板上「撤销本会话」按钮的给不给）。</summary>
    public int UndoableBatches { get; set; }
    public int UndoCalls { get; private set; }

    public void RaiseNotify(AiNotification notification) => Notified?.Invoke(notification);
    public void RaiseFreeze() => WriteFreezeChanged?.Invoke();

    // ── 配置面 ───────────────────────────────────────────────

    public Task<IReadOnlyList<AiProviderInfo>> ListProvidersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AiProviderInfo>>(Providers);

    public Task<AiProviderInfo> CreateCustomProviderAsync(AiProtocol protocol, CancellationToken ct = default)
        => throw new NotSupportedException("测试桩不落配置");

    public Task<AiProviderInfo> SaveProviderAsync(AiProviderDraft draft, CancellationToken ct = default)
    {
        SaveProviderCalls.Add(draft);
        var index = Providers.FindIndex(p => p.Id == draft.Id);
        var info = index < 0 ? null : Providers[index];
        if (info is not null)
            Providers[index] = info with
            {
                DisplayName = draft.DisplayName,
                BaseUrl = draft.BaseUrl,
                Protocol = draft.Protocol,
            };
        return Task.FromResult(Providers.Single(p => p.Id == draft.Id));
    }

    public Task DeleteProviderAsync(string providerId, CancellationToken ct = default)
    {
        DeleteProviderCalls.Add(providerId);
        return Task.CompletedTask;
    }

    public Task SetApiKeyAsync(string providerId, string apiKey, CancellationToken ct = default) => Task.CompletedTask;

    public Task RemoveApiKeyAsync(string providerId, CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveModelAsync(AiModelDraft draft, CancellationToken ct = default)
    {
        SaveModelCalls.Add(draft);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AiModelInfo>> RefreshModelsAsync(string providerId, CancellationToken ct = default)
    {
        RefreshModelsCalls.Add(providerId);
        return Task.FromResult(Providers.Single(p => p.Id == providerId).Models);
    }

    public Task<AiConnectivityResult> TestConnectivityAsync(string providerId, CancellationToken ct = default)
    {
        TestCalls.Add(providerId);
        return Task.FromResult(new AiConnectivityResult(AiProviderStatus.Verified, null, 1, 0));
    }

    public Task<AiPreferences> GetPreferencesAsync(CancellationToken ct = default)
        => Task.FromResult(Preferences);

    public Task SavePreferencesAsync(AiPreferences preferences, CancellationToken ct = default)
    {
        Preferences = preferences;
        SavePreferencesCalls.Add(preferences);
        return Task.CompletedTask;
    }

    // ── 选择与运行状态 ────────────────────────────────────────

    public AiSelectionResolution ResolveSelection() => Selection;

    public bool IsTurnRunning => false;
    public bool IsWriteFrozen => false;
    public string? WriteFreezeSessionId => null;

    // ── 会话 ─────────────────────────────────────────────────

    public Task<IReadOnlyList<AiSessionSummary>> ListSessionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AiSessionSummary>>(Sessions);

    public Task<AiSessionDetail> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (Details.TryGetValue(sessionId, out var detail)) return Task.FromResult(detail);
        var summary = Sessions.Single(s => s.SessionId == sessionId);
        return Task.FromResult(new AiSessionDetail(summary, [], [], [], [], []));
    }

    /// <summary>会话详情预置（未预置 = 空详情）。</summary>
    public Dictionary<string, AiSessionDetail> Details { get; } = [];

    /// <summary>建草稿调用计数（断言"连点复用同一个草稿"用）。</summary>
    public int CreateSessionCalls { get; private set; }

    /// <summary>被丢弃的草稿 id（断言"未用草稿有回收"用）。</summary>
    public List<string> DiscardedDrafts { get; } = [];

    /// <summary>建会话 = 建**草稿**（与真实引擎同语义）：不进 <see cref="Sessions"/>（列表只有正式会话）。</summary>
    public Task<AiSessionSummary> CreateSessionAsync(CancellationToken ct = default)
    {
        CreateSessionCalls++;
        var summary = new AiSessionSummary($"s-draft{CreateSessionCalls}", "", AiMode.ConfirmEach, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, null, AiSessionPersistence.Deferred);
        return Task.FromResult(summary);
    }

    public Task DiscardDraftSessionAsync(string sessionId, CancellationToken ct = default)
    {
        DiscardedDrafts.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task RenameSessionAsync(string sessionId, string title, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetModeAsync(string sessionId, AiMode mode, CancellationToken ct = default)
    {
        SetModeCalls.Add((sessionId, mode));
        return Task.CompletedTask;
    }

    // ── 回合 / 审批 / 导出 / 审计 / 撤销 ─────────────────────────

    public Task SendAsync(string sessionId, string text, AiTurnContext? context = null, CancellationToken ct = default)
    {
        SendCalls.Add((sessionId, text));
        SendContexts.Add(context);
        return Task.CompletedTask;
    }

    public Task CancelTurnAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RespondToApprovalAsync(string sessionId, string approvalId, AiApprovalDecision decision,
        string? reason = null, CancellationToken ct = default)
    {
        ApprovalCalls.Add((sessionId, approvalId, decision, reason));
        return Task.CompletedTask;
    }

    public Task ExportAsync(string sessionId, AiExportFormat format, string outputPath, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<AiAuditPage> QueryEngineAuditAsync(AiAuditQuery query, CancellationToken ct = default)
    {
        AuditQueries.Add(query);
        // 桩的分页形状：满页 2 行、末页 1 行；总行数按页数推出（VM 分页断言只看页码与按钮可用性）
        var count = query.Page < AuditPageCount ? 2 : 1;
        var items = Enumerable.Range(0, count)
            .Select(i => new AiEngineCallRow(DateTimeOffset.UtcNow.AddMinutes(-i), "folders.create", "agent:s-1",
                "ai:t-1", true, null, 3, false, false, null, null, null))
            .ToArray();
        var total = 2 * (AuditPageCount - 1) + 1;
        return Task.FromResult(new AiAuditPage(items, total, query.Page, AuditPageCount));
    }

    public Task<AiUndoResult> UndoLastTurnAsync(string sessionId, CancellationToken ct = default)
    {
        UndoCalls++;
        return Task.FromResult(UndoResult);
    }

    public Task<AiUndoResult> UndoSessionAsync(string sessionId, CancellationToken ct = default)
    {
        UndoSessionCalls.Add(sessionId);
        return Task.FromResult(UndoResult);
    }

    public Task<int> CountUndoableAsync(string sessionId, CancellationToken ct = default)
    {
        CountUndoableCalls.Add(sessionId);
        return Task.FromResult(UndoableBatches);
    }

    // ── 提及 / 技能 / 用量（P4）─────────────────────────────────

    /// <summary>提及候选预置（SearchMentionsAsync 按名称/ID 子串过滤后返回）。</summary>
    public List<AiMentionCandidate> MentionCandidates { get; } = [];
    public List<string> MentionQueries { get; } = [];

    /// <summary>技能库预置（ListSkillsAsync 的读数；Save/Delete 会就地更新它）。</summary>
    public List<AiSkill> Skills { get; } = [];
    public List<AiSkillDraft> SaveSkillCalls { get; } = [];
    public List<string> DeleteSkillCalls { get; } = [];
    public List<(string SessionId, string SkillId, IReadOnlyDictionary<string, string>? Parameters)> RunSkillCalls { get; } = [];
    public List<string> MacroNames { get; } = [];
    public List<(string SessionId, int Days)> UsageSummaryCalls { get; } = [];
    public AiSessionUsage SessionUsage { get; set; } = new(0, 0, 0, 0);
    public AiUsageSummary UsageSummary { get; set; } = new(7, [], 0, 0, 0);

    public Task<IReadOnlyList<AiMentionCandidate>> SearchMentionsAsync(string query, int limit = 8,
        CancellationToken ct = default)
    {
        MentionQueries.Add(query);
        var items = MentionCandidates
            .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || c.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToArray();
        return Task.FromResult<IReadOnlyList<AiMentionCandidate>>(items);
    }

    public Task<IReadOnlyList<AiSkill>> ListSkillsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AiSkill>>(Skills.ToArray());

    public Task<AiSkill> SaveSkillAsync(AiSkillDraft draft, CancellationToken ct = default)
    {
        SaveSkillCalls.Add(draft);
        var saved = new AiSkill(draft.SkillId ?? $"k-{Skills.Count + 1}", draft.Name, draft.Description,
            draft.PromptTemplate, draft.MacroName, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            LinkPocket.Ai.AiSkillStore.ExtractParameters(draft.PromptTemplate));
        var index = Skills.FindIndex(s => s.SkillId == saved.SkillId);
        if (index >= 0) Skills[index] = saved;
        else Skills.Add(saved);
        return Task.FromResult(saved);
    }

    public Task DeleteSkillAsync(string skillId, CancellationToken ct = default)
    {
        DeleteSkillCalls.Add(skillId);
        Skills.RemoveAll(s => s.SkillId == skillId);
        return Task.CompletedTask;
    }

    public Task RunSkillAsync(string sessionId, string skillId,
        IReadOnlyDictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        RunSkillCalls.Add((sessionId, skillId, parameters));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListMacroNamesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(MacroNames.ToArray());

    public Task<AiSessionUsage> GetSessionUsageAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(SessionUsage);

    public Task<AiUsageSummary> GetUsageSummaryAsync(int days = 7, CancellationToken ct = default)
    {
        UsageSummaryCalls.Add((string.Empty, days));
        return Task.FromResult(UsageSummary);
    }

    // ── 通知 ─────────────────────────────────────────────────

    public event Action<AiNotification>? Notified;
    public event Action? WriteFreezeChanged;
}
