using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 预设服务商目录（6 条）。这些是**模板数据**：用户保存的服务商记录是它的覆盖层，
/// 删除用户记录即回到出厂模板；与预设不撞名的 Id 则是用户自建服务商（自定义）。
/// <para>显示名一律走**文案键**（<c>ai.provider.*</c>，中文只许出现在 <c>I18n/Strings/*.json</c>）；
/// <c>DisplayName</c> 只是 ASCII 回退名。</para>
/// <para><c>PresetModelIds</c> = 该服务商出厂内置的模型清单。同一家若有多种接入格式，
/// 各档是**独立条目**（各有自己的地址与协议）；其余接入用「其他（自定义）」自建。</para>
/// </summary>
public static class AiProviderCatalog
{
    /// <summary>预设模板（顺序即设置页展出顺序）。</summary>
    public static IReadOnlyList<AiProviderTemplate> Templates { get; } =
    [
        new("zai-standard-api", "ai.provider.zaiStandardApi", "Z.ai API", AiProtocol.OpenAiChat,
            "https://api.z.ai/api/paas/v4", IsLocal: false, "https://z.ai/manage-apikey/apikey-list", null,
            ["GLM-5.3", "GLM-5.3-Flash", "GLM-5V-Turbo", "GLM-5.1", "GLM-5.1-Highspeed", "GLM-5", "GLM-5-Turbo", "GLM-4.7", "GLM-4.7-FlashX", "GLM-4.7-Flash", "GLM-4.6", "GLM-4.5-Air", "GLM-4.5", "GLM-4.6V", "GLM-4.6V-Flash", "GLM-4.6V-FlashX", "GLM-4.1V-Thinking-FlashX", "GLM-4.1V-Thinking-Flash", "GLM-4-FlashX-250414", "GLM-4-Flash-250414", "GLM-4V-Flash", "codegeex-4", "charglm-4", "emohaa"]),
        new("bigmodel-standard-api", "ai.provider.bigmodelStandardApi", "BigModel API", AiProtocol.OpenAiChat,
            "https://open.bigmodel.cn/api/paas/v4", IsLocal: false, "https://bigmodel.cn/usercenter/proj-mgmt/apikeys", null,
            ["GLM-5.3", "GLM-5.3-Flash", "GLM-5V-Turbo", "GLM-5.1", "GLM-5.1-Highspeed", "GLM-5", "GLM-5-Turbo", "GLM-4.7", "GLM-4.7-FlashX", "GLM-4.7-Flash", "GLM-4.6", "GLM-4.5-Air", "GLM-4.5", "GLM-4.6V", "GLM-4.6V-Flash", "GLM-4.6V-FlashX", "GLM-4.1V-Thinking-FlashX", "GLM-4.1V-Thinking-Flash", "GLM-4-FlashX-250414", "GLM-4-Flash-250414", "GLM-4V-Flash", "codegeex-4", "charglm-4", "emohaa"]),
        new("moonshot-kimi", "ai.provider.moonshotKimi", "Kimi", AiProtocol.AnthropicMessages,
            "https://api.moonshot.cn/anthropic", IsLocal: false, "https://platform.kimi.com/console/api-keys", null,
            ["kimi-k3", "kimi-k2.7-code", "kimi-k2.6", "kimi-k2.7-code-highspeed", "k3", "k3-256k"]),
        new("deepseek", "ai.provider.deepseek", "DeepSeek", AiProtocol.AnthropicMessages,
            "https://api.deepseek.com/anthropic", IsLocal: false, "https://platform.deepseek.com/api_keys", null,
            ["deepseek-flash", "deepseek-v4-pro"]),
        new("xiaomi-mimo", "ai.provider.xiaomiMimo", "Xiaomi MiMo", AiProtocol.AnthropicMessages,
            "https://api.xiaomimimo.com/anthropic", IsLocal: false, "https://platform.xiaomimimo.com/", null,
            ["mimo-v2.5-pro", "mimo-v2.5"]),
        // 本机推理预设（不属于上面那批云端接入变体）：地址留本机 OpenAI 兼容端点，
        // IsLocal: true ⇒ 免密钥（准入校验不要求 API Key）。
        new("local", "ai.provider.local", "Local models (Ollama / LM Studio)", AiProtocol.OpenAiChat,
            "http://127.0.0.1:11434/v1", IsLocal: true, null, null, []),
    ];

    /// <summary>是否预设 Id（不在模板表里 = 用户自建）。</summary>
    public static bool IsPreset(string id)
        => Templates.Any(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>按 Id 取模板（不存在 → null）。</summary>
    public static AiProviderTemplate? Find(string id)
        => Templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
}
