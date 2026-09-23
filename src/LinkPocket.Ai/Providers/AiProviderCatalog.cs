using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 预设服务商目录（12 条，功能书 §4.2）。这些是**模板数据**：用户保存的服务商记录是它的覆盖层，
/// 删除用户记录即回到出厂模板；与预设不撞名的 Id 则是用户自建服务商（自定义）。
/// <para>显示名一律走**文案键**（<c>ai.provider.*</c>，中文只许出现在 StringTables.cs）；
/// <c>DisplayName</c> 只是 ASCII 回退名。</para>
/// </summary>
public static class AiProviderCatalog
{
    /// <summary>预设模板（顺序即设置页展出顺序）。</summary>
    public static IReadOnlyList<AiProviderTemplate> Templates { get; } =
    [
        new("openai", "ai.provider.openai", "OpenAI", AiProtocol.OpenAiChat, "https://api.openai.com/v1",
            IsLocal: false, "https://platform.openai.com/api-keys", "https://platform.openai.com/docs",
            ["gpt-4o", "gpt-4o-mini"]),
        new("anthropic", "ai.provider.anthropic", "Anthropic", AiProtocol.AnthropicMessages,
            "https://api.anthropic.com", IsLocal: false,
            "https://console.anthropic.com/settings/keys", "https://docs.anthropic.com", []),
        new("gemini", "ai.provider.gemini", "Google Gemini", AiProtocol.OpenAiChat,
            "https://generativelanguage.googleapis.com/v1beta/openai", IsLocal: false,
            "https://aistudio.google.com/apikey", "https://ai.google.dev/gemini-api/docs", []),
        new("deepseek", "ai.provider.deepseek", "DeepSeek", AiProtocol.OpenAiChat,
            "https://api.deepseek.com/v1", IsLocal: false,
            "https://platform.deepseek.com/api_keys", "https://api-docs.deepseek.com",
            ["deepseek-chat", "deepseek-reasoner"]),
        new("moonshot", "ai.provider.moonshot", "Moonshot Kimi", AiProtocol.OpenAiChat,
            "https://api.moonshot.cn/v1", IsLocal: false,
            "https://platform.moonshot.cn/console/api-keys", "https://platform.moonshot.cn/docs", []),
        new("bigmodel", "ai.provider.bigmodel", "Zhipu GLM", AiProtocol.OpenAiChat,
            "https://open.bigmodel.cn/api/paas/v4", IsLocal: false,
            "https://open.bigmodel.cn/usercenter/apikeys", "https://open.bigmodel.cn/dev/api", []),
        new("dashscope", "ai.provider.dashscope", "Alibaba Bailian (Qwen)", AiProtocol.OpenAiChat,
            "https://dashscope.aliyuncs.com/compatible-mode/v1", IsLocal: false,
            "https://bailian.console.aliyun.com/", "https://help.aliyun.com/zh/model-studio/",
            ["qwen-plus", "qwen-max"]),
        new("volcengine", "ai.provider.volcengine", "Volcano Ark (Doubao)", AiProtocol.OpenAiChat,
            "https://ark.cn-beijing.volces.com/api/v3", IsLocal: false,
            "https://console.volcengine.com/ark", "https://www.volcengine.com/docs/82379", []),
        new("siliconflow", "ai.provider.siliconflow", "SiliconFlow", AiProtocol.OpenAiChat,
            "https://api.siliconflow.cn/v1", IsLocal: false,
            "https://cloud.siliconflow.cn/account/ak", "https://docs.siliconflow.cn", []),
        new("openrouter", "ai.provider.openrouter", "OpenRouter", AiProtocol.OpenAiChat,
            "https://openrouter.ai/api/v1", IsLocal: false,
            "https://openrouter.ai/keys", "https://openrouter.ai/docs", []),
        new("xai", "ai.provider.xai", "xAI Grok", AiProtocol.OpenAiChat, "https://api.x.ai/v1",
            IsLocal: false, "https://console.x.ai/", "https://docs.x.ai", []),
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
