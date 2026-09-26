using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Ai.Tests;

/// <summary>
/// 原生联网搜索的方言探测回归（2026-09-26 产品决定：**本地搜索兜底已移除，只走模型侧能力**）：
/// 只有 Anthropic 的 server tool 无条件可用；MiMo/GLM 等需厂商控制台开插件，未验证前一律不开
///（实测未开插件时那条声明会被当"没有参数 schema 的普通函数"，模型试探被拒后放弃联网）。
/// </summary>
public class WebSearchDialectTests
{
    [Fact]
    public void 方言探测_只开Anthropic()
    {
        Assert.True(WebSearchDialect.Supports(Provider(AiProtocol.AnthropicMessages, "https://api.anthropic.com")));
        Assert.False(WebSearchDialect.Supports(Provider(AiProtocol.OpenAiChat, "https://api.xiaomimimo.com/v1")));
        Assert.False(WebSearchDialect.Supports(Provider(AiProtocol.OpenAiChat, "https://open.bigmodel.cn/api/paas/v4")));
        Assert.False(WebSearchDialect.Supports(Provider(AiProtocol.OpenAiResponses, "https://api.openai.com/v1")));
    }

    private static AiProviderInfo Provider(AiProtocol protocol, string baseUrl)
        => new("p", "p", protocol, baseUrl, AiProviderSource.Custom, false, true, true, null,
            AiProviderStatus.Verified, null, null, null, []);
}
