using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 原生联网搜索的**方言探测**（只做"要不要在请求里声明内置搜索工具"的判定）。
/// </summary>
/// <remarks>
/// <para><b>2026-09-26 产品决定：本地搜索兜底已移除，联网只走模型侧能力</b>——本地实现（Bing 结果页解析 +
/// 网页抓取）在 MiMo 上根本走不通（实测：模型六连调 <c>web_search</c> 但 <c>arguments</c> 恒为空——它把调用
/// 写在正文 XML 里，结构化参数没给），留着只会让用户看到"调了但没用"的假功能。</para>
/// <para><b>为什么只开 Anthropic</b>：Anthropic 的 server tool 由服务端执行、无需任何额外开关，恒可用；
/// 其余方言（MiMo/GLM/MiniMax 的 <c>{"type":"web_search"}</c>）需要厂商控制台先开插件——**未开时那条声明
/// 会被当"没有参数 schema 的普通函数"透传**，模型反复试探被拒后放弃联网（实测现场）。厂商开关就绪并验证
/// 后，在 <see cref="Supports"/> 里逐个加回白名单即可。</para>
/// </remarks>
public static class WebSearchDialect
{
    /// <summary>该服务商是否声明"服务端执行的原生联网搜索"（客户端只声明工具，绝不自行执行）。</summary>
    public static bool Supports(AiProviderInfo provider)
        => provider.Protocol == AiProtocol.AnthropicMessages;
}
