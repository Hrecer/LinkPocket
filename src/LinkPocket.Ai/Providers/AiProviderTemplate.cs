using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 服务商模板（**预设数据**）：加一家服务商 = 加一条数据，不改代码。
/// <para><c>BaseUrl</c> 约定 = 服务根，由协议适配器拼固定路径：
/// Chat Completions 族 <c>{BaseUrl}/chat/completions</c>、Responses 族 <c>{BaseUrl}/responses</c>
/// （两族 BaseUrl 通常以 <c>/v1</c> 结尾）、Anthropic 族 <c>{BaseUrl}/v1/messages</c>。
/// 模板值是**可编辑缺省**，用户可在设置页改。</para>
/// <para><c>PresetModelIds</c> = 该服务商出厂内置的模型清单（随目录一起更新）；
/// 服务商模型表会腐化，用户可用「拉取模型列表」或手填覆盖它（功能书 §4.5）。</para>
/// </summary>
public sealed record AiProviderTemplate(
    string Id,
    /// <summary>显示名的**文案键**（界面取词；中文只许出现在 <c>I18n/Strings/*.json</c>，此处一律 ASCII）。</summary>
    string DisplayNameKey,
    /// <summary>显示名回退（ASCII 英文名）：仅在界面拿不到 Key 取词结果时使用，不作为界面文案主路径。</summary>
    string DisplayName,
    AiProtocol Protocol,
    string BaseUrl,
    bool IsLocal,
    string? ApiKeyManagementUrl,
    string? DocsUrl,
    IReadOnlyList<string> PresetModelIds);
