using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// AI 助手（<see cref="IAiAssistant"/> 的唯一实现）：配置面 + 会话面 + 回合循环 + 审批 + 写入冻结 + 通知。
/// 定位：**引擎的消费者**——一切读写经 <see cref="EngineClient"/>；本类只做"驱动 + 审批 + 留痕"。
/// 并发：进程内同一时刻最多**一个进行中回合**（<see cref="IsTurnRunning"/>）；HTTP 层并发闸 = 1。
/// </summary>
public sealed partial class AiAssistant : IAiAssistant
{
    private readonly EngineClient _client;
    private readonly ISessionManager _engineSessions;
    private readonly AiProviderStore _providers;
    private readonly AiCredentialStore _credentials;
    private readonly AiPreferenceStore _preferences;
    private readonly AiSessionStore _sessionStore;
    private readonly AiSkillStore _skillStore;
    private readonly AiUsageStore _usage;
    private readonly AiLocalTools _localTools;
    private readonly IAiHttpTransport _http;
    private readonly AiToolCatalog _tools;

    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<AiApprovalResponse>> _pendingApprovals =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _sessionAllowances = new(StringComparer.Ordinal);
    private TurnRun? _running;
    private IDisposable? _writeHold;

    internal AiAssistant(
        EngineClient client,
        ISessionManager engineSessions,
        AiToolCatalog tools,
        AiProviderStore providers,
        AiCredentialStore credentials,
        AiPreferenceStore preferences,
        AiSessionStore sessionStore,
        AiSkillStore skillStore,
        AiUsageStore usage,
        IAiHttpTransport http)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _engineSessions = engineSessions ?? throw new ArgumentNullException(nameof(engineSessions));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _skillStore = skillStore ?? throw new ArgumentNullException(nameof(skillStore));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _localTools = new AiLocalTools(sessionStore, skillStore);
        _http = http ?? throw new ArgumentNullException(nameof(http));
        SweepOrphanedTurns();   // 启动清扫：上次进程的僵尸运行态回合（见方法注释——不清会连锁卡死回溯）
    }

    /// <summary>
    /// 清扫孤儿运行态回合（启动时一次）。进程内回合不可能跨进程存活——会话文件里遗留的
    /// Pending / Streaming / ToolRunning / AwaitingApproval 都是上次进程被杀（崩溃 / 关窗 / 停电）留下的僵尸。
    /// 不清的后果是**连锁**的：① 回合头"本轮进行中 · 已用 N 秒"永久存在且每秒跳秒；
    /// ② 摘要 ActiveTurnState 恒为运行态 → 左栏行转圈不停、进会话即 IsTurnRunning；
    /// ③ 界面的回溯 / 撤销读同一个投影（<see cref="AiViewModel"/> 的 IsTurnRunning）→ 静默拒绝、点了没反应。
    /// 落定为 Interrupted（"本轮已停止"），EndedAt 取**清扫前**该文件的最后写入时刻（僵尸死前最后一次持久化）。
    /// 审批也一并落定：待批表在内存，重启后那张卡永远等不到决定。
    /// </summary>
    private void SweepOrphanedTurns()
    {
        try
        {
            foreach (var summary in _sessionStore.List())
            {
                var file = _sessionStore.Load(summary.SessionId);
                if (file is null) continue;
                var endedAt = file.Summary.UpdatedAt;   // 先取：Save 会刷新 UpdatedAt，那不是"死"的时刻
                var orphans = file.Turns.Count(t => t.State is AiTurnState.Pending or AiTurnState.Streaming
                    or AiTurnState.ToolRunning or AiTurnState.AwaitingApproval);
                if (orphans == 0) continue;
                for (var i = 0; i < file.Turns.Count; i++)
                {
                    var turn = file.Turns[i];
                    if (turn.State is not (AiTurnState.Pending or AiTurnState.Streaming
                        or AiTurnState.ToolRunning or AiTurnState.AwaitingApproval)) continue;
                    file.Turns[i] = turn with { State = AiTurnState.Interrupted, EndedAt = endedAt };
                }
                file.Summary = file.Summary with { ActiveTurnState = file.Turns.LastOrDefault()?.State };
                _sessionStore.Save(file);
                LpLog.Info($"swept {orphans} orphaned running turn(s)", category: "ai.turn");
            }
        }
        catch (Exception ex)
        {
            // 清扫失败不拦启动：症状退回"僵尸回合头 / 回溯被拒"，下次启动再试（如实留痕）
            LpLog.Warn("orphaned turn sweep failed (startup continues)", ex, category: "ai.turn");
        }
    }

    /// <summary>进行中的回合（进程内唯一）。</summary>
    private sealed class TurnRun(string sessionId, string turnId, CancellationTokenSource cts)
    {
        public string SessionId { get; } = sessionId;
        public string TurnId { get; } = turnId;
        public CancellationTokenSource Cts { get; } = cts;
        public bool WriteHoldTaken { get; set; }

        /// <summary>
        /// 本回合是否执行过**用户已批准的批 / 宏**（走到引擎执行 = 审批已经放行）。
        /// </summary>
        /// <remarks>
        /// 批脚本动辄一次产生几百上千条变更记录（实测：一次移动根目录全部书签 = 1430 条），
        /// 这是用户**看见步数并批准**的整体操作，不该再被"单回合变更数上限"枪毙——
        /// 实测它把一次成功的批量移动标成了"本轮失败"（LP.AI.011），而数据其实全都移完了。
        /// 单回合变更上限仍然约束**未走批的散写**（防模型失控逐条狂改）。
        /// </remarks>
        public bool HasApprovedBatch { get; set; }

        /// <summary>本回合模型用量累计（含摘要请求；服务商未声明用量 = 0 → 落盘为 null）。</summary>
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }

        /// <summary>本回合**最近一次**请求的上下文占用读数（本地估算 tokens 与窗口；0 = 还没读过）。</summary>
        public int ContextTokens { get; set; }
        public int ContextWindowTokens { get; set; }

        /// <summary>同一次请求的**分项构成**（估算 token；空 = 还没读过）。</summary>
        public IReadOnlyList<AiContextSourceItem> Breakdown { get; set; } = [];
    }

    internal sealed record AiApprovalResponse(AiApprovalDecision Decision, string? Reason);

    public event Action<AiNotification>? Notified;
    public event Action? WriteFreezeChanged;

    public bool IsTurnRunning
    {
        get
        {
            lock (_gate) return _running is not null;
        }
    }

    /// <summary>写入冻结的**权威读数**取自引擎写锁（本层只是投影）。</summary>
    public bool IsWriteFrozen => _engineSessions.CurrentWriteHold is not null;

    public string? WriteFreezeSessionId => _engineSessions.CurrentWriteHold?.SessionId;

    // ── 配置面（设置页「AI 服务」）─────────────────────────────

    public Task<IReadOnlyList<AiProviderInfo>> ListProvidersAsync(CancellationToken ct = default)
        => Task.FromResult(_providers.List(_credentials));

    /// <summary>自定义服务商 Id = <c>custom-</c> + 8 位十六进制：与预设的人工命名 Id 天然不撞，
    /// 也不与静态模板目录耦合（模板只承载预设条目）。</summary>
    public Task<AiProviderInfo> CreateCustomProviderAsync(AiProtocol protocol, CancellationToken ct = default)
        => Task.FromResult(_providers.SaveProvider(
            new AiProviderDraft("custom-" + Guid.NewGuid().ToString("N")[..8], "", protocol,
                "", Enabled: true, IsLocal: false), _credentials));

    public Task<AiProviderInfo> SaveProviderAsync(AiProviderDraft draft, CancellationToken ct = default)
        => Task.FromResult(_providers.SaveProvider(draft, _credentials));

    public Task DeleteProviderAsync(string providerId, CancellationToken ct = default)
    {
        _providers.DeleteProvider(providerId);
        _credentials.Remove(providerId);
        return Task.CompletedTask;
    }

    public Task SetApiKeyAsync(string providerId, string apiKey, CancellationToken ct = default)
    {
        _credentials.Set(providerId, apiKey);
        _providers.ClearLastTestResult(providerId, _credentials);   // 密钥变了：徽标回到"已配置未验证"
        return Task.CompletedTask;
    }

    public Task RemoveApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        _credentials.Remove(providerId);
        _providers.ClearLastTestResult(providerId, _credentials);
        return Task.CompletedTask;
    }

    public Task SaveModelAsync(AiModelDraft draft, CancellationToken ct = default)
    {
        _providers.SaveModel(draft, _credentials);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AiModelInfo>> RefreshModelsAsync(string providerId, CancellationToken ct = default)
    {
        var provider = InfoOrThrow(providerId);
        var ids = await ListModelIdsAsync(provider, ct).ConfigureAwait(false);
        return _providers.MergeFetchedModels(providerId, ids, _credentials).Models;
    }

    public async Task<AiConnectivityResult> TestConnectivityAsync(string providerId, CancellationToken ct = default)
    {
        var provider = InfoOrThrow(providerId);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var ids = await ListModelIdsAsync(provider, ct).ConfigureAwait(false);
            started.Stop();
            var info = _providers.SetLastTestResult(providerId, null, _credentials);
            return new AiConnectivityResult(info.Status, null, started.ElapsedMilliseconds, ids.Count);
        }
        catch (AiException ex)
        {
            started.Stop();
            var info = _providers.SetLastTestResult(providerId, ex.Error.Code, _credentials);
            return new AiConnectivityResult(info.Status, ex.Error.Code, started.ElapsedMilliseconds, null);
        }
    }

    public Task<AiPreferences> GetPreferencesAsync(CancellationToken ct = default)
        => Task.FromResult(_preferences.Load());

    public Task SavePreferencesAsync(AiPreferences preferences, CancellationToken ct = default)
    {
        _preferences.Save(preferences);
        return Task.CompletedTask;
    }

    public AiSelectionResolution ResolveSelection()
    {
        var preferences = _preferences.Load();
        if (string.IsNullOrWhiteSpace(preferences.ProviderId) || string.IsNullOrWhiteSpace(preferences.ModelId))
            return new AiSelectionResolution(null, AiSelectionIssue.ProviderMissing, null, null);

        var provider = _providers.List(_credentials).FirstOrDefault(p => p.Id == preferences.ProviderId);
        if (provider is null)
            return new AiSelectionResolution(null, AiSelectionIssue.ProviderMissing, null, null);
        if (provider.Issues is { Count: > 0 })
        {
            var issue = provider.Issues.Any(i => i.Code == AiConfigIssueCodes.ApiKeyMissing)
                ? AiSelectionIssue.ApiKeyMissing
                : AiSelectionIssue.ProviderMissing;
            return new AiSelectionResolution(null, issue, provider.DisplayName, null);
        }

        var model = provider.Models.FirstOrDefault(m => string.Equals(m.Id, preferences.ModelId, StringComparison.Ordinal));
        if (model is null)
            return new AiSelectionResolution(null, AiSelectionIssue.ModelMissing, provider.DisplayName, null);
        if (!model.Enabled)
            return new AiSelectionResolution(null, AiSelectionIssue.ModelDisabled, provider.DisplayName, model.DisplayName);
        if (!model.SupportsTools)
            return new AiSelectionResolution(null, AiSelectionIssue.CapabilityMissing, provider.DisplayName, model.DisplayName);
        return new AiSelectionResolution(new AiModelSelection(provider.Id, model.Id), null,
            provider.DisplayName, model.DisplayName);
    }

    // ── 会话 ──────────────────────────────────────────────────

    /// <summary>会话清单。**只含正式会话**：草稿只活在内存里，<c>_sessionStore.List()</c> 扫的是 sessions 目录，
    /// 草稿不在目录中，天然不会被列出来（这里再显式滤一次，防未来实现漂移）。</summary>
    public Task<IReadOnlyList<AiSessionSummary>> ListSessionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AiSessionSummary>>(
            _sessionStore.List().Where(s => s.Persistence != AiSessionPersistence.Deferred).ToArray());

    public Task<AiSessionDetail> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var file = _sessionStore.Load(sessionId) ?? throw NotFound(sessionId);
        return Task.FromResult(ToDetail(file));
    }

    public Task<AiSessionSummary> CreateSessionAsync(CancellationToken ct = default)
    {
        var mode = _preferences.Load().Mode;
        var now = DateTimeOffset.UtcNow;
        var sessionId = $"s-{Guid.NewGuid():N}";
        // 新建 = 草稿（deferred）：**只进内存、不落盘、不进列表**。连点这个入口只会反复复用同一个草稿
        // （界面单飞），不再产生一串空会话。首个回合开始时由 SendAsync 提升为 immediate（那时才落盘）。
        var summary = new AiSessionSummary(sessionId, "", mode, null, null, now, now, 0, 0, null,
            AiSessionPersistence.Deferred);
        _sessionStore.CreateDraft(AiSessionFile.Create(summary));
        Notified?.Invoke(new AiNotification(AiNotificationKind.SessionChanged, sessionId, Session: summary));
        return Task.FromResult(summary);
    }

    /// <summary>丢弃未提升的草稿（幂等）；正式会话 / 已被提升 → no-op（绝不误删真会话）。</summary>
    public Task DiscardDraftSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_sessionStore.IsDraft(sessionId)) return Task.CompletedTask;
        _sessionStore.DiscardDraft(sessionId);
        return Task.CompletedTask;
    }

    public Task RenameSessionAsync(string sessionId, string title, CancellationToken ct = default)
    {
        var file = _sessionStore.Load(sessionId) ?? throw NotFound(sessionId);
        file.Summary = file.Summary with { Title = title ?? "", UpdatedAt = DateTimeOffset.UtcNow };
        _sessionStore.Save(file);
        Notified?.Invoke(new AiNotification(AiNotificationKind.SessionChanged, sessionId, Session: file.Summary));
        return Task.CompletedTask;
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (IsTurnRunning) throw Busy(sessionId);
        _sessionStore.Delete(sessionId);
        return Task.CompletedTask;
    }

    public Task SetModeAsync(string sessionId, AiMode mode, CancellationToken ct = default)
    {
        var file = _sessionStore.Load(sessionId) ?? throw NotFound(sessionId);
        file.Summary = file.Summary with { Mode = mode, UpdatedAt = DateTimeOffset.UtcNow };
        _sessionStore.Save(file);
        Notified?.Invoke(new AiNotification(AiNotificationKind.SessionChanged, sessionId, Session: file.Summary));
        return Task.CompletedTask;
    }

    // ── 审批 ──────────────────────────────────────────────────

    public Task RespondToApprovalAsync(string sessionId, string approvalId, AiApprovalDecision decision,
        string? reason = null, CancellationToken ct = default)
    {
        TaskCompletionSource<AiApprovalResponse>? pending;
        lock (_gate) _pendingApprovals.TryGetValue(approvalId, out pending);
        if (pending is null)
            throw new AiException(AiErrors.Of(AiErrors.ToolCallInvalid,
                "the approval request no longer exists (the turn may have ended)",
                details: JsonSerializer.SerializeToElement(new { approval_id = approvalId })));

        pending.TrySetResult(new AiApprovalResponse(decision, reason));
        return Task.CompletedTask;
    }

    /// <summary>登记"本次会话总是允许该工具"（回合层调用；重启即失效，无持久规则）。</summary>
    internal void GrantSessionAllowance(string command)
    {
        lock (_gate) _sessionAllowances.Add(command);
    }

    internal bool HasSessionAllowance(string command)
    {
        lock (_gate) return _sessionAllowances.Contains(command);
    }

    // ── 导出 ──────────────────────────────────────────────────

    public Task ExportAsync(string sessionId, AiExportFormat format, string outputPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var file = _sessionStore.Load(sessionId) ?? throw NotFound(sessionId);
        var text = format switch
        {
            AiExportFormat.Csv => ToCsv(file),
            AiExportFormat.Json => JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }),
            _ => ToMarkdown(file),
        };
        AtomicFile.WriteAllText(outputPath, text);
        return Task.CompletedTask;
    }

    private static string ToCsv(AiSessionFile file)
    {
        var builder = new StringBuilder("at,turn_id,command,kind,entity_type,entity_id,entity_name,outcome,error_code,fields\n");
        foreach (var change in file.Changes)
        {
            var fields = change.Fields is null ? "" : string.Join("; ", change.Fields.Select(f => $"{f.Field}={f.Before}->{f.After}"));
            builder.Append(change.At.ToString("O")).Append(',')
                .Append(change.TurnId).Append(',')
                .Append(change.Command).Append(',')
                .Append(change.Kind).Append(',')
                .Append(change.EntityType).Append(',')
                .Append(change.EntityId).Append(',')
                .Append(Escape(change.EntityName)).Append(',')
                .Append(change.Outcome).Append(',')
                .Append(change.ErrorCode).Append(',')
                .Append(Escape(fields)).Append('\n');
        }
        return builder.ToString();

        static string Escape(string? value)
            => value is null ? "" : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string ToMarkdown(AiSessionFile file)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(file.Summary.Title).Append('\n');
        builder.Append("model: ").Append(file.Summary.ProviderId).Append(" / ").Append(file.Summary.ModelId)
            .Append(" · mode: ").Append(file.Summary.Mode).Append('\n');
        builder.Append("> This export contains no credentials.\n\n## Conversation\n");
        foreach (var message in file.Messages)
            builder.Append("**").Append(message.Role).Append("**: ").Append(message.Text).Append("\n\n");
        builder.Append("## Changes (").Append(file.Changes.Count).Append(")\n");
        foreach (var change in file.Changes)
        {
            builder.Append("- `").Append(change.Command).Append("` ").Append(change.Kind).Append(' ')
                .Append(change.EntityType).Append('/').Append(change.EntityName ?? change.EntityId)
                .Append(" → ").Append(change.Outcome);
            if (change.Fields is { Count: > 0 })
                builder.Append(" · ").Append(string.Join(", ", change.Fields.Select(f => $"{f.Field}: {f.Before} → {f.After}")));
            builder.Append('\n');
        }
        return builder.ToString();
    }

    // ── 共用小工具 ────────────────────────────────────────────

    private AiProviderInfo InfoOrThrow(string providerId)
        => _providers.List(_credentials).FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal))
           ?? throw new AiException(AiErrors.Of(AiErrors.ProviderNotConfigured, "unknown provider",
               details: JsonSerializer.SerializeToElement(new { provider_id = providerId })));

    private async Task<IReadOnlyList<string>> ListModelIdsAsync(AiProviderInfo provider, CancellationToken ct)
    {
        var adapter = AiProtocols.For(provider.Protocol);
        var key = _credentials.TryGetPlaintext(provider.Id);
        try
        {
            var response = await _http.SendAsync(adapter.BuildModelsRequest(provider, key), ct).ConfigureAwait(false);
            return adapter.ParseModels(response.Body);
        }
        catch (AiException ex) when (ex.Error.Code == AiErrors.ModelNotFound)
        {
            // 模型端点不存在 = 该服务商不提供列表（与"某个模型不存在"是两回事）
            throw new AiException(AiErrors.Of(AiErrors.ModelListUnsupported,
                "the provider does not expose a model list endpoint; add models manually"));
        }
    }

    private static AiSessionDetail ToDetail(AiSessionFile file)
        => new(file.Summary, file.Messages, file.ToolCalls, file.Changes, file.Approvals, file.Turns);

    private static AiException NotFound(string sessionId)
        => new(AiErrors.Of(AiErrors.SessionStoreFailed, "session not found",
            details: JsonSerializer.SerializeToElement(new { session_id = sessionId })));

    private static AiException Busy(string sessionId)
        => new(AiErrors.Of(AiErrors.QuotaExhausted, "a turn is already running (one turn at a time)",
            details: JsonSerializer.SerializeToElement(new { reason = "turn_in_progress", session_id = sessionId })));
}
