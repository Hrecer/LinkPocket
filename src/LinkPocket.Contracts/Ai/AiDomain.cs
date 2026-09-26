namespace LinkPocket.Contracts;

/// <summary>服务商接入协议（= 设置页「格式」下拉的三档）：
/// openai_chat = Chat Completions 兼容族（含国内网关与本地推理）；
/// anthropic_messages = Messages API 族；
/// openai_responses = Responses API 族。</summary>
public enum AiProtocol
{
    OpenAiChat = 0,
    AnthropicMessages = 1,
    OpenAiResponses = 2,
}

/// <summary>服务商来源：preset = 内置模板；custom = 用户自建。</summary>
public enum AiProviderSource
{
    Preset = 0,
    Custom = 1,
}

/// <summary>模型来源：preset = 模板内置；fetched = 从服务商拉取；manual = 手填。</summary>
public enum AiModelSource
{
    Preset = 0,
    Fetched = 1,
    Manual = 2,
}

/// <summary>服务商可用状态（设置页徽标）：未配置 / 已配置未验证 / 已验证 / 验证失败。</summary>
public enum AiProviderStatus
{
    /// <summary>配置不完整（缺地址、缺密钥或无启用模型）。</summary>
    NotConfigured = 0,
    /// <summary>配置完整且最近一次连通性测试成功。</summary>
    Verified = 1,
    /// <summary>配置完整但最近一次连通性测试失败（ErrorCode 说明原因）。</summary>
    Failed = 2,
    /// <summary>配置完整、尚未做过连通性测试。</summary>
    Configured = 3,
}

/// <summary>AI 工作模式：readonly = 纯问答（只读会话）；confirm_each = 每次写都审批（**缺省**）；auto_apply = 写自动执行（破坏性仍审批）。</summary>
public enum AiMode
{
    ReadOnly = 0,
    ConfirmEach = 1,
    AutoApply = 2,
}

/// <summary>
/// 会话是否已持久化（**「会话已落进会话库」这一事实的协议侧镜像**）。
/// <para><see cref="Immediate"/> = 正式会话：已在 <c>sessions/</c> 落盘、进左栏列表。</para>
/// <para><see cref="Deferred"/> = **草稿**：只在内存里存在（不落盘、不进列表），
/// 由第一条消息（<see cref="IAiAssistant.SendAsync"/>）**提升**为 <see cref="Immediate"/>——
/// 提升即落盘。未被提升的草稿可以在切换会话 / 关闭页面时被静默丢弃，绝不留下空会话。</para>
/// <para>这是「连点新建产生一堆空会话」的结构性解法：新建只造草稿，草稿不落盘就不进列表，
/// 反复点也只是复用同一个草稿（单飞），而不是真的建出一串会话。</para>
/// </summary>
public enum AiSessionPersistence
{
    /// <summary>已持久化：正式会话（落盘 + 进列表）。</summary>
    Immediate = 0,

    /// <summary>草稿：仅内存，未落盘、不进列表；首发时提升为 <see cref="Immediate"/>。</summary>
    Deferred = 1,
}

/// <summary>模型输入模态声明（模型编辑弹窗「输入模态」那一组；<c>Text</c> 恒为真且不可关，
/// 其余按模型实际能力勾选）。</summary>
public sealed record AiModelInputFormat(
    bool SupportsText = true,
    bool SupportsImage = false,
    bool SupportsVideo = false,
    bool SupportsPdf = false);

/// <summary>模型推理等级声明：「有序等级名」+「等级 → 供应商参数的 JSON 映射」。
/// <paramref name="MapJson"/> 是**机器面**原文（界面上屏前逐字给，不做插值）。</summary>
public sealed record AiModelReasoning(
    IReadOnlyList<string> Levels,
    string? MapJson = null);

/// <summary>模型能力声明（用于上下文预算与工具/流式开关；未声明按保守缺省）。</summary>
public sealed record AiModelInfo(
    string Id,
    string DisplayName,
    AiModelSource Source,
    bool Enabled,
    int? ContextWindow,
    int? MaxOutputTokens,
    bool SupportsTools,
    bool SupportsStreaming,
    /// <summary>输入模态声明（null = 未声明 = 仅文本）。</summary>
    AiModelInputFormat? InputFormat = null,
    /// <summary>是否支持 JSON Schema 结构化输出。</summary>
    bool SupportsJsonSchemaOutput = false,
    /// <summary>是否支持供应商原生联网搜索。</summary>
    bool SupportsNativeWebSearch = false,
    /// <summary>是否支持会话中插入系统提示。</summary>
    bool SupportsMidConversationSystem = false,
    /// <summary>推理等级声明（null = 不支持推理等级）。</summary>
    AiModelReasoning? Reasoning = null);

/// <summary>服务商对外投影（**不含明文密钥**；ApiKeyMasked 只露前 4 后 4）。</summary>
public sealed record AiProviderInfo(
    string Id,
    /// <summary>显示名：预设/覆盖层未改名时 = 模板英文名；用户改过名时 = 用户输入（用户数据）。</summary>
    string DisplayName,
    AiProtocol Protocol,
    string BaseUrl,
    AiProviderSource Source,
    bool IsLocal,
    bool Enabled,
    bool HasApiKey,
    string? ApiKeyMasked,
    AiProviderStatus Status,
    string? StatusErrorCode,
    string? ApiKeyManagementUrl,
    string? DocsUrl,
    IReadOnlyList<AiModelInfo> Models,
    /// <summary>准入校验问题清单（空 = 配置完整可运行期使用）；界面按 FieldPath 高亮、按 Code 取词。</summary>
    IReadOnlyList<AiConfigIssue>? Issues = null,
    /// <summary>预设显示名的文案键（界面优先用它取词）；用户改过名或自建服务商 → null（直接显示 DisplayName）。</summary>
    string? DisplayNameKey = null);

/// <summary>配置校验问题（字段路径 + 稳定码；界面负责取词）。</summary>
public sealed record AiConfigIssue(string FieldPath, string Code);

/// <summary>配置校验问题码（稳定；界面按码取词）。</summary>
public static class AiConfigIssueCodes
{
    /// <summary>必填项为空（display_name 等）。</summary>
    public const string Required = "required";
    /// <summary>接入地址不是合法的 http/https 绝对地址。</summary>
    public const string InvalidUrl = "invalid_url";
    /// <summary>缺 API Key（本地服务商不需要）。</summary>
    public const string ApiKeyMissing = "api_key_missing";
    /// <summary>没有任何已启用模型。</summary>
    public const string NoEnabledModel = "no_enabled_model";
}

/// <summary>服务商草稿（设置页保存）：宽松阶段允许半填（可存），只有完整者进运行期 registry。</summary>
public sealed record AiProviderDraft(
    string Id,
    string DisplayName,
    AiProtocol Protocol,
    string BaseUrl,
    bool Enabled,
    bool IsLocal);

/// <summary>模型草稿（新增 / 启停 / 能力声明 / 手填）。</summary>
public sealed record AiModelDraft(
    string ProviderId,
    string Id,
    string DisplayName,
    bool Enabled,
    int? ContextWindow,
    int? MaxOutputTokens,
    bool SupportsTools,
    bool SupportsStreaming,
    AiModelInputFormat? InputFormat = null,
    bool SupportsJsonSchemaOutput = false,
    bool SupportsNativeWebSearch = false,
    bool SupportsMidConversationSystem = false,
    AiModelReasoning? Reasoning = null);

/// <summary>模型选择（会话级持久化）。</summary>
public sealed record AiModelSelection(string ProviderId, string ModelId);

/// <summary>选择解析失败的结构化原因（界面按码取词，**不抛异常**）。</summary>
public enum AiSelectionIssue
{
    ProviderMissing = 0,
    ModelMissing = 1,
    ApiKeyMissing = 2,
    ModelDisabled = 3,
    CapabilityMissing = 4,
}

/// <summary>生效解析结果：Selection 非空 = 可用；否则 Issue 说明缺什么（ProviderName/ModelName 供界面显示）。</summary>
public sealed record AiSelectionResolution(
    AiModelSelection? Selection,
    AiSelectionIssue? Issue,
    string? ProviderName,
    string? ModelName);

/// <summary>连通性测试结果（三态 + 耗时 + 拉到的模型数；ErrorCode 走 LP.AI.*，界面按键取词）。</summary>
public sealed record AiConnectivityResult(
    AiProviderStatus Status,
    string? ErrorCode,
    long ElapsedMs,
    int? ModelCount);

/// <summary>助手偏好（设置页「AI 服务」；与外观偏好同族：独立文件、加字段不升版本、损坏如实暴露）。</summary>
public sealed record AiPreferences(
    string? ProviderId = null,
    string? ModelId = null,
    AiMode Mode = AiMode.ConfirmEach,
    int MaxToolCallsPerTurn = 40,
    int MaxChangesPerTurn = 200,
    int MaxBatchSteps = 500,
    int CallsPerMinute = 120,
    int ContextBudgetTokens = 32000,
    bool AdvancedToolsEnabled = false);

/// <summary>技能（提示词模板 + 可选绑定的宏；存 <c>{程序根}/ai/skills.json</c>）。
/// <paramref name="Parameters"/> = 模板里解析出的 `{参数}` 占位名（只读投影，按出现顺序去重）。</summary>
public sealed record AiSkill(
    string SkillId,
    string Name,
    string Description,
    string PromptTemplate,
    string? MacroName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? Parameters = null);

/// <summary>技能草稿（保存入参；<paramref name="SkillId"/> 为空 = 新建）。</summary>
/// <summary>宏清单行（macro.list 的投影：名称 + 更新时间，不带脚本体——脚本按需 macro.get）。</summary>
public sealed record AiMacroInfo(string Name, DateTimeOffset UpdatedAt)
{
    /// <summary>清单行的本地化时间文本（列表直接绑定；MinValue = 引擎未给时间，显示占位）。</summary>
    public string UpdatedAtText => UpdatedAt == DateTimeOffset.MinValue
        ? "-"
        : UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
}

public sealed record AiSkillDraft(
    string? SkillId,
    string Name,
    string Description,
    string PromptTemplate,
    string? MacroName);

/// <summary>用量用途（按用途分账：对话请求 / 上下文摘要请求）。</summary>
public enum AiUsagePurpose
{
    Turn = 0,
    Summary = 1,
}

/// <summary>本会话用量读数（审计面板底部状态条：轮数 / 工具调用次数 / token 合计；逐轮读数之和，单一来源 = 会话文件）。</summary>
/// <summary>本会话用量读数（数据源 = 会话文件里的逐轮记录）。
/// <paramref name="ContextTokens"/>/<paramref name="ContextWindowTokens"/> = **最近一次模型请求**的上下文占用读数
/// （本地估算口径，与压缩阈值同源；还没发过请求 = null / 0——界面显示空环，不编造）。
/// <paramref name="Breakdown"/> = 同一次请求的**分项构成**（各段字符数折算的估算 token），
/// 供悬浮面板画出"这一段排了多少"；没有读数 = 空表（不编造分项）。</summary>
public sealed record AiSessionUsage(
    int Turns,
    int ToolCalls,
    long InputTokens,
    long OutputTokens,
    int? ContextTokens = null,
    int ContextWindowTokens = 0,
    IReadOnlyList<AiContextSourceItem>? Breakdown = null);

/// <summary>上下文构成的来源（与系统提示词的分段一一对应，顺序即展示顺序）。</summary>
public enum AiContextSource
{
    /// <summary>固定系统提示词（角色、工具规则、安全约束、当前模式）。</summary>
    SystemPrompt = 0,

    /// <summary>当前页面上下文（所在页 / 当前目录 / 选中项）。</summary>
    PageContext = 1,

    /// <summary>用户点名引用的对象（提及 + 会话引用）。</summary>
    Mentions = 2,

    /// <summary>已加载技能（提示词模板）。</summary>
    Skills = 3,

    /// <summary>工具模式的 schema（发给模型的工具清单）。</summary>
    ToolSchemas = 4,

    /// <summary>对话消息（历史 + 本轮）。</summary>
    Messages = 5,
}

/// <summary>上下文分项（来源 + 估算 token）；<paramref name="Tokens"/> = 0 的项不进列表。</summary>
public sealed record AiContextSourceItem(AiContextSource Source, int Tokens);

/// <summary>按天用量（设置页「AI 服务」只读行；<paramref name="Day"/> = 本地日）。</summary>
public sealed record AiUsageDay(DateTimeOffset Day, int Calls, long InputTokens, long OutputTokens);

/// <summary>近 N 天用量汇总（数据源 = <c>ai/usage.json</c>；天数与合计一起给出，不做事后裁剪）。</summary>
public sealed record AiUsageSummary(
    int Days,
    IReadOnlyList<AiUsageDay> Items,
    long Calls,
    long InputTokens,
    long OutputTokens);
