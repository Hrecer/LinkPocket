using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 服务商模板（**预设数据**）：加一家服务商 = 加一条数据，不改代码。
/// <para><c>BaseUrl</c> 约定 = 服务根，由协议适配器拼固定路径：
/// OpenAI 族 <c>{BaseUrl}/chat/completions</c>（故 BaseUrl 通常以 <c>/v1</c> 结尾）、
/// Anthropic 族 <c>{BaseUrl}/v1/messages</c>。模板值是**可编辑缺省**，用户可在设置页改。</para>
/// <para>内置模型 ID 只放"长期稳定"的少数几个，**不维护长名单**（服务商模型表必然腐化）——
/// 其余模型靠「拉取模型列表」或手填（功能书 §4.5）。</para>
/// </summary>
public sealed record AiProviderTemplate(
    string Id,
    /// <summary>显示名的**文案键**（界面取词；中文只许出现在 StringTables.cs，此处一律 ASCII）。</summary>
    string DisplayNameKey,
    /// <summary>显示名回退（ASCII 英文名）：仅在界面拿不到 Key 取词结果时使用，不作为界面文案主路径。</summary>
    string DisplayName,
    AiProtocol Protocol,
    string BaseUrl,
    bool IsLocal,
    string? ApiKeyManagementUrl,
    string? DocsUrl,
    IReadOnlyList<string> PresetModelIds);
