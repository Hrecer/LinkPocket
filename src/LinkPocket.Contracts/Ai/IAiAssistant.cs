namespace LinkPocket.Contracts;

/// <summary>
/// AI 助手契约面（UI 与 AI 运行时之间的**唯一交界面**）。
/// 定位：AI 是**引擎的消费者**——一切读写都经 <see cref="EngineClient"/> / <see cref="IEngine"/> 管道，
/// 本接口只表达「配置 / 会话 / 回合 / 审批 / 通知」。
/// 实现 = <c>LinkPocket.Ai</c>（只被组合根 new）；UI 只依赖本接口与 <see cref="AiProviderInfo"/> 一族 DTO。
/// </summary>
public interface IAiAssistant
{
    // ── 配置面（设置页「AI 服务」）─────────────────────────────

    /// <summary>服务商清单（含模型与状态；不含明文密钥）。</summary>
    Task<IReadOnlyList<AiProviderInfo>> ListProvidersAsync(CancellationToken ct = default);

    /// <summary>保存服务商（草稿可半填；只有完整者进运行期 registry）。</summary>
    Task<AiProviderInfo> SaveProviderAsync(AiProviderDraft draft, CancellationToken ct = default);

    /// <summary>删除服务商（连同其模型与凭据；界面走 ConfirmDialog）。</summary>
    Task DeleteProviderAsync(string providerId, CancellationToken ct = default);

    /// <summary>写入 / 替换 API Key（DPAPI 加密落盘；明文不落任何文件、不进日志）。</summary>
    Task SetApiKeyAsync(string providerId, string apiKey, CancellationToken ct = default);

    /// <summary>删除 API Key。</summary>
    Task RemoveApiKeyAsync(string providerId, CancellationToken ct = default);

    /// <summary>新增 / 更新一个模型（启用、能力声明、手填）。</summary>
    Task SaveModelAsync(AiModelDraft draft, CancellationToken ct = default);

    /// <summary>拉取模型列表（不支持列表的服务商 → LP.AI.006，界面引导手填）。</summary>
    Task<IReadOnlyList<AiModelInfo>> RefreshModelsAsync(string providerId, CancellationToken ct = default);

    /// <summary>连通性测试（一次最小请求；结果三态，附耗时与模型数）。</summary>
    Task<AiConnectivityResult> TestConnectivityAsync(string providerId, CancellationToken ct = default);

    /// <summary>读助手偏好（独立文件；损坏如实暴露）。</summary>
    Task<AiPreferences> GetPreferencesAsync(CancellationToken ct = default);

    /// <summary>写助手偏好（原子替换）。</summary>
    Task SavePreferencesAsync(AiPreferences preferences, CancellationToken ct = default);

    // ── 选择与运行状态 ────────────────────────────────────────

    /// <summary>生效解析（未配置 / 模型不存在 / 无密钥 / 能力缺失 → 返回结构化 issue，**不抛**）。</summary>
    AiSelectionResolution ResolveSelection();

    /// <summary>是否有回合在跑（进程内同一时刻最多一个；UI 据此禁用其它会话的发送入口）。</summary>
    bool IsTurnRunning { get; }

    /// <summary>写入冻结是否生效（AI 改数据期间用户不能改；Shell 只做投影，权威在引擎）。</summary>
    bool IsWriteFrozen { get; }

    /// <summary>持锁会话（无冻结时为 null）。</summary>
    string? WriteFreezeSessionId { get; }

    // ── 会话 ──────────────────────────────────────────────────

    Task<IReadOnlyList<AiSessionSummary>> ListSessionsAsync(CancellationToken ct = default);

    /// <summary>会话详情（对话 + 工具调用 + 台账 + 审批 + 回合）。</summary>
    Task<AiSessionDetail> GetSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>新建会话（模式取偏好缺省）。</summary>
    Task<AiSessionSummary> CreateSessionAsync(CancellationToken ct = default);

    Task RenameSessionAsync(string sessionId, string title, CancellationToken ct = default);

    /// <summary>删除会话（连台账与大结果；界面走 ConfirmDialog）。</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>切换会话模式（只读 / 每次确认 / 自动应用）。</summary>
    Task SetModeAsync(string sessionId, AiMode mode, CancellationToken ct = default);

    // ── 回合 ──────────────────────────────────────────────────

    /// <summary>发一条用户消息并跑一个回合；已有回合在跑 → LP.AI.011（details.reason = turn_in_progress）。</summary>
    Task SendAsync(string sessionId, string text, AiTurnContext? context = null, CancellationToken ct = default);

    /// <summary>停止当前回合（立即释放写入冻结；**已提交的变更不回退**）。</summary>
    Task CancelTurnAsync(string sessionId, CancellationToken ct = default);

    // ── 审批 ──────────────────────────────────────────────────

    /// <summary>回应一次审批（允许一次 / 本会话总是允许 / 拒绝[+理由] / 拒绝并停止回合）。</summary>
    Task RespondToApprovalAsync(string sessionId, string approvalId, AiApprovalDecision decision,
        string? reason = null, CancellationToken ct = default);

    // ── 导出 ──────────────────────────────────────────────────

    /// <summary>导出会话或台账报告（同目录临时文件 + 原子替换；导出物**绝不含密钥**）。</summary>
    Task ExportAsync(string sessionId, AiExportFormat format, string outputPath, CancellationToken ct = default);

    // ── 通知（订阅一次，按 Kind 分派）──────────────────────────

    /// <summary>会话内容增量通知（消息 / 流式增量 / 工具调用 / 变更 / 审批 / 回合 / 会话摘要）。</summary>
    event Action<AiNotification>? Notified;

    /// <summary>写入冻结开 / 关（Shell 用它做全局投影：写命令置灰 + 状态带 + 危险键让位）。</summary>
    event Action? WriteFreezeChanged;
}
