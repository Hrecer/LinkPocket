namespace LinkPocket.Contracts;

/// <summary>服务商接入协议：openai_chat = /chat/completions 兼容族（含国内网关与本地推理）；anthropic_messages = Messages API 族。</summary>
public enum AiProtocol
{
    OpenAiChat = 0,
    AnthropicMessages = 1,
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

/// <summary>模型能力声明（用于上下文预算与工具/流式开关；未声明按保守缺省）。</summary>
public sealed record AiModelInfo(
    string Id,
    string DisplayName,
    AiModelSource Source,
    bool Enabled,
    int? ContextWindow,
    int? MaxOutputTokens,
    bool SupportsTools,
    bool SupportsStreaming);

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
    bool SupportsStreaming);

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
/// （本地估算口径，与压缩阈值同源；还没发过请求 = null / 0——界面显示空环，不编造）。</summary>
public sealed record AiSessionUsage(
    int Turns,
    int ToolCalls,
    long InputTokens,
    long OutputTokens,
    int? ContextTokens = null,
    int ContextWindowTokens = 0);

/// <summary>按天用量（设置页「AI 服务」只读行；<paramref name="Day"/> = 本地日）。</summary>
public sealed record AiUsageDay(DateTimeOffset Day, int Calls, long InputTokens, long OutputTokens);

/// <summary>近 N 天用量汇总（数据源 = <c>ai/usage.json</c>；天数与合计一起给出，不做事后裁剪）。</summary>
public sealed record AiUsageSummary(
    int Days,
    IReadOnlyList<AiUsageDay> Items,
    long Calls,
    long InputTokens,
    long OutputTokens);
