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

    /// <summary>新建一个自定义服务商（可建多个）：Id 由 AI 层生成、与预设不撞名，
    /// 初值 = 空名 + 空地址 + <paramref name="protocol"/>（接入格式由「添加服务商」的模板选择器给，
    /// 与预设不撞名；草稿宽松）；用户在表单里补名称 / 地址 / 密钥。</summary>
    Task<AiProviderInfo> CreateCustomProviderAsync(AiProtocol protocol, CancellationToken ct = default);

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

    /// <summary>
    /// 新建会话 = **建草稿**（模式取偏好缺省）：只造一个内存里的 <see cref="AiSessionPersistence.Deferred"/>
    /// 会话，**不落盘、不进左栏列表**，直到第一条消息发出（<see cref="SendAsync"/> 内部提升为正式）才真正落盘。
    /// 连点这个入口只会反复复用同一个草稿（界面单飞），不会产生一串空会话。
    /// </summary>
    Task<AiSessionSummary> CreateSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// 丢弃一个**尚未提升**的草稿（正式会话或已被提升的会话 = 幂等 no-op，绝不误删真会话）。
    /// 界面在「草稿无人使用」（换会话 / 关页 / 重新预热换代）时调用，保证空草稿不残留。
    /// </summary>
    Task DiscardDraftSessionAsync(string sessionId, CancellationToken ct = default);

    Task RenameSessionAsync(string sessionId, string title, CancellationToken ct = default);

    /// <summary>删除会话（连台账与大结果；界面走 ConfirmDialog）。</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>切换会话模式（只读 / 每次确认 / 自动应用）。</summary>
    Task SetModeAsync(string sessionId, AiMode mode, CancellationToken ct = default);

    // ── 提及（输入区 @）────────────────────────────────────────

    /// <summary>
    /// 提及候选检索（文件夹 + 链接，按已输入片段匹配；<paramref name="limit"/> 上限内按名称序返回）。
    /// 纯读（folders.find / links.query），失败的候选来源如实缺席（不猜、不编）。
    /// </summary>
    Task<IReadOnlyList<AiMentionCandidate>> SearchMentionsAsync(string query, int limit = 8,
        CancellationToken ct = default);

    // ── 技能库 ────────────────────────────────────────────────

    /// <summary>技能清单（<c>{程序根}/ai/skills.json</c>；损坏如实暴露 <c>LP.AI.015</c>）。</summary>
    Task<IReadOnlyList<AiSkill>> ListSkillsAsync(CancellationToken ct = default);

    /// <summary>保存技能（名称唯一；模板里 `{参数}` 占位上限 8 个；非空宏名经 <c>macro.get</c> 校验存在）。</summary>
    Task<AiSkill> SaveSkillAsync(AiSkillDraft draft, CancellationToken ct = default);

    /// <summary>删除技能。</summary>
    Task DeleteSkillAsync(string skillId, CancellationToken ct = default);

    /// <summary>
    /// 运行技能：渲染提示模板（填入参数；未填的占位保持字面）作为**用户消息**发起一个回合
    /// （走正常回合与审批链，绝不绕过）。回合在跑时拒绝（<c>LP.AI.011</c>）。
    /// </summary>
    Task RunSkillAsync(string sessionId, string skillId, IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken ct = default);

    /// <summary>宏名清单（技能编辑器的绑定下拉；数据源 = 引擎 <c>macro.list</c>）。</summary>
    Task<IReadOnlyList<string>> ListMacroNamesAsync(CancellationToken ct = default);

    // ── 用量 ──────────────────────────────────────────────────

    /// <summary>本会话用量读数（模型调用 / 工具调用 / token 合计；数据源 = 会话文件里的逐轮读数）。</summary>
    Task<AiSessionUsage> GetSessionUsageAsync(string sessionId, CancellationToken ct = default);

    /// <summary>近 N 天用量汇总（数据源 = <c>ai/usage.json</c> 的按天 × 模型聚合）。</summary>
    Task<AiUsageSummary> GetUsageSummaryAsync(int days = 7, CancellationToken ct = default);

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

    /// <summary>引擎审计页签的数据源（audit.query）：按回合关联取齐该回合/本会话的引擎调用史。</summary>
    Task<AiAuditPage> QueryEngineAuditAsync(AiAuditQuery query, CancellationToken ct = default);

    /// <summary>
    /// 撤销上一轮（最近回合）的 AI 变更：按归属键（工具调用 ID）在引擎撤销栈里逐条定点撤销（新者先撤），
    /// 走既有 <c>undo.undo</c>、不做第二条撤销路径；引擎未登记逆向的变更不进可撤销集合（如实跳过）。
    /// 回合在跑时拒绝（<c>LP.AI.011</c>）。
    /// </summary>
    Task<AiUndoResult> UndoLastTurnAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// **回溯**到某一轮之前（「回溯」按钮的落点）：一次调用做两件事，缺一件就不是回溯——
    /// ① 回退该轮做过的**全部数据操作**（走引擎既有 <c>undo.undo</c>，与「撤销上一轮」同一条核心路径）；
    /// ② 把会话**裁到该轮之前**——该轮及其之后的回合 / 消息 / 工具调用 / 变更 / 审批 / 模型历史一并移除，
    /// 于是"这一轮好像没发生过"。
    /// <para>为什么不裁剪就够、为什么必须一起做：只回退数据面的话，对话里那条提问与回答还在，
    /// 用户看到的是"说了半天、库回到原样、但记录还挂着"，完全不是回溯的语义。</para>
    /// <para>回合在跑时拒绝（<c>LP.AI.011</c>）；不可撤销的操作如实跳过并计数，不承诺"什么都退得回来"。</para>
    /// </summary>
    Task<AiRewindResult> RewindTurnAsync(string sessionId, string turnId, CancellationToken ct = default);

    /// <summary>
    /// 撤销**本会话**的 AI 变更：按批次（归属键 = 工具调用 ID，一次批/宏 = 一条记录）分组**逐批退**，
    /// 新者先撤；口径与 <see cref="UndoLastTurnAsync"/> 完全一致——走引擎既有 <c>undo.undo</c> 定点撤销，
    /// **绝不做第二条撤销路径**；不在撤销栈里的如实跳过并计数，绝不给会失败的撤销。
    /// 回合在跑时拒绝（<c>LP.AI.011</c>）。
    /// </summary>
    Task<AiUndoResult> UndoSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 本会话**仍在引擎撤销栈里持有归属记录**的可撤销批次数（0 = 没有可撤销批次）。
    /// 界面据此决定「撤销本会话 AI 变更」按钮给不给（**不给会失败的按钮**）；纯读，不改任何状态。
    /// </summary>
    Task<int> CountUndoableAsync(string sessionId, CancellationToken ct = default);

    // ── 通知（订阅一次，按 Kind 分派）──────────────────────────

    /// <summary>会话内容增量通知（消息 / 流式增量 / 工具调用 / 变更 / 审批 / 回合 / 会话摘要）。</summary>
    event Action<AiNotification>? Notified;

    /// <summary>写入冻结开 / 关（Shell 用它做全局投影：写命令置灰 + 状态带 + 危险键让位）。</summary>
    event Action? WriteFreezeChanged;
}
