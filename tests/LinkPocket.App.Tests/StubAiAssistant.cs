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
    public List<(string SessionId, AiMode Mode)> SetModeCalls { get; } = [];
    public List<AiModelDraft> SaveModelCalls { get; } = [];
    public List<AiPreferences> SavePreferencesCalls { get; } = [];
    public List<AiAuditQuery> AuditQueries { get; } = [];
    public List<(string SessionId, string ApprovalId, AiApprovalDecision Decision, string? Reason)> ApprovalCalls { get; } = [];
    public int UndoCalls { get; private set; }

    public void RaiseNotify(AiNotification notification) => Notified?.Invoke(notification);
    public void RaiseFreeze() => WriteFreezeChanged?.Invoke();

    // ── 配置面 ───────────────────────────────────────────────

    public Task<IReadOnlyList<AiProviderInfo>> ListProvidersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AiProviderInfo>>(Providers);

    public Task<AiProviderInfo> SaveProviderAsync(AiProviderDraft draft, CancellationToken ct = default)
        => throw new NotSupportedException("测试桩不落配置");

    public Task DeleteProviderAsync(string providerId, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetApiKeyAsync(string providerId, string apiKey, CancellationToken ct = default) => Task.CompletedTask;

    public Task RemoveApiKeyAsync(string providerId, CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveModelAsync(AiModelDraft draft, CancellationToken ct = default)
    {
        SaveModelCalls.Add(draft);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AiModelInfo>> RefreshModelsAsync(string providerId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AiModelInfo>>([]);

    public Task<AiConnectivityResult> TestConnectivityAsync(string providerId, CancellationToken ct = default)
        => Task.FromResult(new AiConnectivityResult(AiProviderStatus.Verified, null, 1, 0));

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
        var summary = Sessions.Single(s => s.SessionId == sessionId);
        return Task.FromResult(new AiSessionDetail(summary, [], [], [], [], []));
    }

    public Task<AiSessionSummary> CreateSessionAsync(CancellationToken ct = default)
    {
        var summary = new AiSessionSummary($"s-new{Sessions.Count}", "", AiMode.ConfirmEach, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, null);
        Sessions.Insert(0, summary);
        return Task.FromResult(summary);
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

    // ── 通知 ─────────────────────────────────────────────────

    public event Action<AiNotification>? Notified;
    public event Action? WriteFreezeChanged;
}
