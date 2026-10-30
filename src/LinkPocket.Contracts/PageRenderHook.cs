namespace LinkPocket.Contracts;

/// <summary>浏览器内核渲染后的页面元数据（只取标题与描述——图标另走 favicon 链路）。</summary>
/// <remarks>
/// 与 <c>LinkPocket.Kernel.PageMetadata</c> 分开：契约层不认识内核类型（内核反向依赖契约），
/// 而渲染兜底只需要这两个字段，不需要 favicon / 拦截标记。
/// </remarks>
/// <param name="Title">渲染后 <c>document.title</c>；空串按"没取到"处理。</param>
/// <param name="Description">渲染后 <c>meta[name=description]</c>（缺失时取 <c>og:description</c>）。</param>
public sealed record RenderedPageMetadata(string? Title, string? Description);

/// <summary>
/// 浏览器渲染兜底钩子（**由组合根装配**，与 <c>FaviconCache.ImpersonatedDownloader</c> 同一套做法）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：有一类站点把"证明自己是浏览器"做成了<b>必须执行 JS</b> 的挑战
/// （火山引擎 WAF 的 <c>__waf_verification_challenge</c> 即此例）：页面用 <b>200</b> 返回一张
/// "正在进行安全检测"的空壳，靠页内脚本算出 proof、写入放行 cookie 后自行重载。
/// 这种墙对请求头、TLS/HTTP2 指纹、甚至 headless 浏览器（暴露 <c>navigator.webdriver</c>）一律不放行，
/// 只有<b>真正的浏览器内核</b>能过——所以兜底必须是宿主自己的浏览器实例，而不是又一个 HTTP 客户端。
/// </para>
/// <para>
/// <b>为什么是钩子而不是普通依赖</b>：只有 WPF 宿主有浏览器内核（以及 UI 线程）。引擎另有两个无界面
/// 宿主（CLI / MCP），它们拿不到渲染能力，此时钩子为 <c>null</c>，抓取退回"仅 HTTP"的既有行为。
/// </para>
/// <para>
/// <b>纪律</b>：实现方必须自带硬超时并<b>串行</b>（浏览器实例不可并发），失败一律返回 <c>null</c>
/// —— 兜底缺位不得让抓取整体失败（与指纹兜底同口径）。
/// </para>
/// </remarks>
public static class PageRenderHook
{
    /// <summary>
    /// 渲染器：入参 = 已通过 http/https 与环回校验的绝对地址；返回 <c>null</c> = 渲染不可用或失败。
    /// <c>null</c>（默认）= 未装配，只有 HTTP 路径。
    /// </summary>
    public static Func<string, CancellationToken, Task<RenderedPageMetadata?>>? Renderer { get; set; }
}
