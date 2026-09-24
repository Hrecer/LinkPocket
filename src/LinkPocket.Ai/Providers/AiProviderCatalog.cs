using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 预设服务商目录（21 条）。这些是**模板数据**：用户保存的服务商记录是它的覆盖层，
/// 删除用户记录即回到出厂模板；与预设不撞名的 Id 则是用户自建服务商（自定义）。
/// <para>显示名一律走**文案键**（<c>ai.provider.*</c>，中文只许出现在 <c>I18n/Strings/*.json</c>）；
/// <c>DisplayName</c> 只是 ASCII 回退名。</para>
/// <para><c>PresetModelIds</c> = 该服务商出厂内置的模型清单（含各家「Coding Plan / 标准 API」两个接入变体，
/// 变体是独立条目：同一家的两种接入格式各有自己的地址与协议）。</para>
/// </summary>
public static class AiProviderCatalog
{
    /// <summary>预设模板（顺序即设置页展出顺序）。</summary>
    public static IReadOnlyList<AiProviderTemplate> Templates { get; } =
    [
        new("zai-api", "ai.provider.zai-api", "Z.ai Coding Plan", AiProtocol.AnthropicMessages,
            "https://api.z.ai/api/anthropic", IsLocal: false, "https://z.ai/manage-apikey/apikey-list", null,
            ["GLM-5.3", "GLM-5.3-Flash"]),
        new("zai-standard-api", "ai.provider.zai-standard-api", "Z.ai API", AiProtocol.OpenAiChat,
            "https://api.z.ai/api/paas/v4", IsLocal: false, "https://z.ai/manage-apikey/apikey-list", null,
            ["GLM-5.3", "GLM-5.3-Flash", "GLM-5V-Turbo", "GLM-5.1", "GLM-5.1-Highspeed", "GLM-5", "GLM-5-Turbo", "GLM-4.7", "GLM-4.7-FlashX", "GLM-4.7-Flash", "GLM-4.6", "GLM-4.5-Air", "GLM-4.5", "GLM-4.6V", "GLM-4.6V-Flash", "GLM-4.6V-FlashX", "GLM-4.1V-Thinking-FlashX", "GLM-4.1V-Thinking-Flash", "GLM-4-FlashX-250414", "GLM-4-Flash-250414", "GLM-4V-Flash", "codegeex-4", "charglm-4", "emohaa"]),
        new("bigmodel-api", "ai.provider.bigmodel-api", "BigModel Coding Plan", AiProtocol.AnthropicMessages,
            "https://open.bigmodel.cn/api/anthropic", IsLocal: false, "https://bigmodel.cn/coding-plan/personal/overview", null,
            ["GLM-5.3", "GLM-5.3-Flash"]),
        new("bigmodel-standard-api", "ai.provider.bigmodel-standard-api", "BigModel API", AiProtocol.OpenAiChat,
            "https://open.bigmodel.cn/api/paas/v4", IsLocal: false, "https://bigmodel.cn/usercenter/proj-mgmt/apikeys", null,
            ["GLM-5.3", "GLM-5.3-Flash", "GLM-5V-Turbo", "GLM-5.1", "GLM-5.1-Highspeed", "GLM-5", "GLM-5-Turbo", "GLM-4.7", "GLM-4.7-FlashX", "GLM-4.7-Flash", "GLM-4.6", "GLM-4.5-Air", "GLM-4.5", "GLM-4.6V", "GLM-4.6V-Flash", "GLM-4.6V-FlashX", "GLM-4.1V-Thinking-FlashX", "GLM-4.1V-Thinking-Flash", "GLM-4-FlashX-250414", "GLM-4-Flash-250414", "GLM-4V-Flash", "codegeex-4", "charglm-4", "emohaa"]),
        new("moonshot-kimi", "ai.provider.moonshot-kimi", "Kimi", AiProtocol.AnthropicMessages,
            "https://api.moonshot.cn/anthropic", IsLocal: false, "https://platform.kimi.com/console/api-keys", null,
            ["kimi-k3", "kimi-k2.7-code", "kimi-k2.6", "kimi-k2.7-code-highspeed", "k3", "k3-256k"]),
        new("minimax", "ai.provider.minimax", "MiniMax", AiProtocol.AnthropicMessages,
            "https://api.minimaxi.com/anthropic", IsLocal: false, "https://platform.minimaxi.com/console/access?tab=api-keys", null,
            ["MiniMax-M3", "MiniMax-M2.7", "MiniMax-M2.7-highspeed", "MiniMax-M2.5", "MiniMax-M2.5-highspeed", "MiniMax-M2.1", "MiniMax-M2.1-highspeed", "MiniMax-M2"]),
        new("deepseek", "ai.provider.deepseek", "DeepSeek", AiProtocol.AnthropicMessages,
            "https://api.deepseek.com/anthropic", IsLocal: false, "https://platform.deepseek.com/api_keys", null,
            ["deepseek-flash", "deepseek-v4-pro"]),
        new("qwen-alibaba-model-studio-cn", "ai.provider.qwen-alibaba-model-studio-cn", "Alibaba Cloud (China)", AiProtocol.AnthropicMessages,
            "https://dashscope.aliyuncs.com/apps/anthropic", IsLocal: false, "https://bailian.console.aliyun.com/cn-beijing?tab=model", null,
            ["qwen3.8-max", "qwen3.8-flash", "qwen3.7-max", "qwen3.7-plus", "qwen3.7-flash", "qwen3.6-plus", "qwen3.6-flash", "qwen3.5-plus", "qwen3.5-flash", "qwen3-max", "qwen-plus", "qwen-flash", "qwen3-vl-plus"]),
        new("qwen-alibaba-model-studio-intl", "ai.provider.qwen-alibaba-model-studio-intl", "Alibaba Cloud (Global)", AiProtocol.OpenAiChat,
            "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", IsLocal: false, "https://modelstudio.console.aliyun.com/ap-southeast-1?tab=dashboard", null,
            ["qwen3.8-max", "qwen3.8-flash", "qwen3.8-omni-flash", "qwen3.7-max", "qwen3.7-plus", "qwen3.7-flash", "qwen3.6-plus", "qwen3.6-flash", "qwen3.5-plus", "qwen3.5-flash", "qwen3-max", "qwen-plus", "qwen-flash", "qwen3-vl-plus"]),
        new("xiaomi-mimo", "ai.provider.xiaomi-mimo", "Xiaomi MiMo", AiProtocol.AnthropicMessages,
            "https://api.xiaomimimo.com/anthropic", IsLocal: false, "https://platform.xiaomimimo.com/", null,
            ["mimo-v2.5-pro", "mimo-v2.5"]),
        new("openai", "ai.provider.openai", "OpenAI", AiProtocol.OpenAiResponses,
            "https://api.openai.com/v1", IsLocal: false, "https://platform.openai.com/api-keys", null,
            ["gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.6", "gpt-5.4", "gpt-5.4-pro", "gpt-5.4-mini", "gpt-5.4-nano", "gpt-5.3-codex"]),
        new("anthropic", "ai.provider.anthropic", "Anthropic", AiProtocol.AnthropicMessages,
            "https://api.anthropic.com", IsLocal: false, "https://console.anthropic.com/settings/keys", null,
            ["claude-fable-5-1", "claude-fable-5", "claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5-20251001"]),
        new("xai", "ai.provider.xai", "xAI", AiProtocol.OpenAiResponses,
            "https://api.x.ai/v1", IsLocal: false, "https://console.x.ai", null,
            ["grok-4.6", "grok-build-0.1", "grok-4.3"]),
        new("openrouter", "ai.provider.openrouter", "OpenRouter", AiProtocol.AnthropicMessages,
            "https://openrouter.ai/api", IsLocal: false, "https://openrouter.ai/keys", null,
            ["anthropic/claude-fable-5.1", "openai/gpt-6-astra", "openai/gpt-5.6-sol", "anthropic/claude-opus-5", "deepseek/deepseek-v4-pro", "moonshotai/kimi-k3", "z-ai/glm-5.3", "qwen/qwen3.8-max", "minimax/minimax-m3", "xiaomi/mimo-v2.5-pro", "x-ai/grok-4.6", "deepseek/deepseek-v4.1-flash", "qwen/qwen3.8-max-0902", "openai/gpt-5.6-terra", "openai/gpt-5.6-luna", "openai/gpt-5.6", "openai/gpt-5.4", "openai/gpt-5.4-pro", "openai/gpt-5.4-mini", "openai/gpt-5.4-nano", "openai/gpt-5.3-codex", "anthropic/claude-sonnet-5", "anthropic/claude-haiku-4.5", "anthropic/claude-opus-4.8", "anthropic/claude-opus-4.7", "anthropic/claude-opus-4.6", "anthropic/claude-opus-4.5", "anthropic/claude-sonnet-4.6", "anthropic/claude-sonnet-4.5", "deepseek/deepseek-v4-flash", "moonshotai/kimi-k2.7-code", "moonshotai/kimi-k2.6", "moonshotai/kimi-k2.5", "z-ai/glm-5.3-flash", "z-ai/glm-5.2", "z-ai/glm-5.1", "z-ai/glm-5v-turbo", "z-ai/glm-5", "z-ai/glm-5-turbo", "z-ai/glm-4.7", "z-ai/glm-4.7-flash", "z-ai/glm-4.6", "z-ai/glm-4.6v", "z-ai/glm-4.5-air", "z-ai/glm-4.5", "qwen/qwen3.8-flash", "qwen/qwen3.7-max", "qwen/qwen3.7-plus", "qwen/qwen3.7-flash", "qwen/qwen3.6-plus", "qwen/qwen3.6-flash", "qwen/qwen3.5-plus-20260420", "qwen/qwen3-vl-plus", "qwen/qwen3-vl-flash", "minimax/minimax-m2.7", "minimax/minimax-m2.5", "xiaomi/mimo-v2.5", "x-ai/grok-build-0.1", "x-ai/grok-4.3"]),
        new("opencode-go-chat", "ai.provider.opencode-go-chat", "OpenCode Go (Chat)", AiProtocol.OpenAiChat,
            "https://opencode.ai/zen/go/v1", IsLocal: false, "https://opencode.ai/auth", null,
            ["glm-5.3-flash", "glm-5.3", "kimi-k3", "kimi-k2.7-code", "deepseek-v4.1-flash", "deepseek-v4-pro", "mimo-v2.5", "mimo-v2.5-pro", "glm-5.2", "glm-5.1", "kimi-k2.6", "deepseek-v4-flash", "deepseek-v4-flash-vision-exp", "hy4-preview", "hy3"]),
        new("opencode-go-messages", "ai.provider.opencode-go-messages", "OpenCode Go (Anthropic)", AiProtocol.AnthropicMessages,
            "https://opencode.ai/zen/go", IsLocal: false, "https://opencode.ai/auth", null,
            ["minimax-m3", "qwen3.8-max", "qwen3.8-flash", "minimax-m2.7", "minimax-m2.5", "qwen3.7-max", "qwen3.7-plus", "qwen3.6-plus"]),
        new("opencode-go-responses", "ai.provider.opencode-go-responses", "OpenCode Go (Responses)", AiProtocol.OpenAiResponses,
            "https://opencode.ai/zen/go/v1", IsLocal: false, "https://opencode.ai/auth", null,
            ["gpt-5.6-luna", "grok-4.6"]),
        new("opencode-zen-responses", "ai.provider.opencode-zen-responses", "OpenCode Zen (Responses)", AiProtocol.OpenAiResponses,
            "https://opencode.ai/zen/v1", IsLocal: false, "https://opencode.ai/auth", null,
            ["gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5", "gpt-5.5-pro", "gpt-5.4", "gpt-5.4-pro", "gpt-5.4-mini", "gpt-5.4-nano", "gpt-5.3-codex", "gpt-5.3-codex-spark", "gpt-5.2", "gpt-5.1"]),
        new("opencode-zen-messages", "ai.provider.opencode-zen-messages", "OpenCode Zen (Anthropic)", AiProtocol.AnthropicMessages,
            "https://opencode.ai/zen", IsLocal: false, "https://opencode.ai/auth", null,
            ["claude-fable-5-1", "claude-fable-5", "qwen3.7-max", "qwen3.6-plus", "qwen3.5-plus", "claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5", "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-opus-4-5", "claude-sonnet-4-6", "claude-sonnet-4-5", "qwen3.7-plus"]),
        new("opencode-zen-chat", "ai.provider.opencode-zen-chat", "OpenCode Zen (Chat)", AiProtocol.OpenAiChat,
            "https://opencode.ai/zen/v1", IsLocal: false, "https://opencode.ai/auth", null,
            ["kimi-k3", "minimax-m3", "deepseek-v4-pro", "glm-5.2", "big-pickle", "mimo-v2.5-free", "hy3-free", "ling-3.0-flash-fin-free", "nemotron-3-ultra-free", "muse-spark-1.2-contributor-free", "minimax-m2.7", "deepseek-v4-flash", "glm-5.1", "nemotron-3.5-lightning-free"]),
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
