using LinkPocket.Contracts;
using LinkPocket.I18n;

namespace LinkPocket.Views;

/// <summary>
/// AI 侧状态 / 错误码 → 文案键的**唯一映射**（UI.Ai 与 UI.Settings 共用，避免各写一份）。
/// 界面只按码取词，**从不显示英文技术文案**（HumanSummary / Message 一律不上屏）。
/// </summary>
public static class AiKeyMap
{
    /// <summary>服务商状态徽标 / 连通性结果 → 键。
    /// ⚠️ 与「回合状态」分开一套键（`ai.status.*` 那组是回合/页面的状态行文案）——曾共用
    /// `ai.status.failed`，于是设置页的服务商徽标画的是"上一回合失败了"。</summary>
    public static string Status(AiProviderStatus status) => status switch
    {
        AiProviderStatus.Verified => "ai.status.provider.verified",
        AiProviderStatus.Failed => "ai.status.provider.failed",
        AiProviderStatus.Configured => "ai.status.provider.configured",
        _ => "ai.status.provider.notConfigured",
    };

    /// <summary>LP.AI.* 错误码 → 键（未收录即通用文案）。</summary>
    public static string Error(string? code) => code switch
    {
        AiErrors.ProviderNotConfigured => "ai.err.providerNotConfigured",
        AiErrors.ProviderUnreachable => "ai.err.providerUnreachable",
        AiErrors.AuthFailed => "ai.err.authFailed",
        AiErrors.UpstreamRateLimited => "ai.err.upstreamRateLimited",
        AiErrors.ModelNotFound => "ai.err.modelNotFound",
        AiErrors.ModelListUnsupported => "ai.err.modelListUnsupported",
        AiErrors.BadResponse => "ai.err.badResponse",
        AiErrors.ContextOverflow => "ai.err.contextOverflow",
        AiErrors.ToolCallInvalid => "ai.err.toolCallInvalid",
        AiErrors.TurnCancelled => "ai.err.turnCancelled",
        AiErrors.QuotaExhausted => "ai.err.quotaExhausted",
        AiErrors.CredentialStoreFailed => "ai.err.credentialStoreFailed",
        AiErrors.SessionStoreFailed => "ai.err.sessionStoreFailed",
        AiErrors.UnsupportedCapability => "ai.err.unsupportedCapability",
        AiErrors.AiDataStoreFailed => "ai.err.dataStoreFailed",
        AiErrors.InvalidInput => "ai.err.invalidInput",
        _ => "ai.err.generic",
    };

    /// <summary>工具调用状态 → 键。</summary>
    public static string ToolState(AiToolCallState state) => state switch
    {
        AiToolCallState.Pending => "ai.tool.state.pending",
        AiToolCallState.AwaitingApproval => "ai.tool.state.awaiting",
        AiToolCallState.Running => "ai.tool.state.running",
        AiToolCallState.Completed => "ai.tool.state.completed",
        AiToolCallState.Rejected => "ai.tool.state.rejected",
        AiToolCallState.Skipped => "ai.tool.state.skipped",
        _ => "ai.tool.state.failed",
    };

    /// <summary>
    /// 命令名（机器面，含下划线）→ 审批卡的**本地化动作短语键**（功能书 §8.2「做什么」）。
    /// 键规范的每段只认 <c>[a-z][a-zA-Z0-9]*</c>（无下划线），所以
    /// <c>folders.move_batch</c> 落成 <c>ai.action.folders.moveBatch</c>；改映射必须同时改这张键表。
    /// </summary>
    public static string Action(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "ai.action.unknown";
        var segments = command.Split('.');
        for (var i = 0; i < segments.Length; i++) segments[i] = UnderscoreToCamel(segments[i]);
        return "ai.action." + string.Join(".", segments);
    }

    private static string UnderscoreToCamel(string segment)
    {
        if (!segment.Contains('_', StringComparison.Ordinal)) return segment;
        var parts = segment.Split('_');
        var result = parts[0];
        for (var i = 1; i < parts.Length; i++)
            if (parts[i].Length > 0)
                result += char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        return result;
    }

    /// <summary>
    /// 命令名（机器面，含下划线）→ 工具行的**域图标**字形键（只认 <c>LpIcons</c> 已注册的字形；
    /// 未收录的域回落通用工具图标——不猜、不新增字形）。
    /// </summary>
    public static string Icon(string? command) => command?.Split('.')[0] switch
    {
        "folders" => "folder-outline",
        "links" => "link-variant",
        "trash" => "delete-outline",
        "search" => "magnify",
        "bookmarks" => "bookmark-outline",
        "backup" => "backup-restore",
        "dedup" => "content-duplicate",
        "favicon" => "star",
        "locate" => "map-marker",
        "maintenance" => "cog-outline",
        "macro" or "skill" => "auto-fix",
        "undo" or "session" => "history",
        "staging" => "import",
        "batch" => "sort",
        _ => "wrench-outline",
    };

    /// <summary>
    /// 引擎随 <c>LP.SEC.003</c> 下发的影响面摘要（机器面英文）→ 键；未收录的形状 = 带参数的整句
    /// （引擎给的事实原样进参数，不改写、不翻译掉）。
    /// </summary>
    public static LocValue Impact(string? impact) => impact switch
    {
        null => LocValue.Empty,
        "link" => LocValue.Of("ai.approve.impact.link"),
        "folder (with subtree)" => LocValue.Of("ai.approve.impact.folder"),
        "entire database" => LocValue.Of("ai.approve.impact.database"),
        _ => Loc.K("ai.approve.impact.other", impact),
    };

    /// <summary>批脚本步骤的错误策略 → 键；null = 脚本没写该策略。
    /// 未知策略到不了这里：引擎解析脚本时就按 JSON 枚举转换器拒绝了。</summary>
    public static string? OnError(string? policy) => policy switch
    {
        null => null,
        "abort" => "ai.onError.abort",
        "continue" => "ai.onError.continue",
        "skip_and_log" => "ai.onError.skipAndLog",
        _ => null,
    };
}
