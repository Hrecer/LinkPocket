namespace LinkPocket.Contracts;

/// <summary>浏览器内核渲染后的页面元数据（只取标题与描述——图标另走 favicon 链路）。</summary>
/// <remarks>
/// 与 <c>LinkPocket.Kernel.PageMetadata</c> 分开：契约层不认识内核类型（内核反向依赖契约），
/// 而渲染兜底只需要这两个字段，不需要 favicon / 拦截标记。
/// </remarks>
/// <param name="Title">渲染后 <c>document.title</c>；空串按"没取到"处理。</param>
/// <param name="Description">渲染后 <c>meta[name=description]</c>（缺失时取 <c>og:description</c>）。</param>
/// <param name="FaviconUrl">
/// 渲染后页面上声明的图标地址（<c>link[rel~=icon]</c>，已解析成绝对地址）；没声明时为 <c>null</c>。
/// 这条路径拿到的图标地址比"站点根 /favicon.ico"可靠——被反爬墙拦的站点，根目录图标往往也一起被拦。
/// </param>
public sealed record RenderedPageMetadata(string? Title, string? Description, string? FaviconUrl = null);

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
/// <b>为什么是钩子而不是普通依赖</b>：浏览器内核与真实浏览器窗口都在有 UI 线程的宿主里。引擎另有
/// CLI / MCP 两个无头宿主：它们经能力桥复用同一份实现（同库同管道、当前用户）；没有桌面宿主时，
/// 由同一个 <c>LinkPocket.exe --capability-host</c> 按需提供。桥不可用时钩子为 <c>null</c>，
/// 抓取退回"仅 HTTP + 指纹"，并由结果字段如实说明能力不可用。
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

    /// <summary>
    /// <b>需要人点一下</b>的渲染器：把页面放进一个<b>可见的专用窗口</b>摆给用户，
    /// 等他完成人机校验，再读标题与描述。返回 <c>null</c> = 用户关掉窗口 / 超时 / 校验没通过。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不能自动</b>：有一类站点（Cloudflare Turnstile）的判断依据是"有没有真人操作"，
    /// 而不是"请求像不像浏览器"。实测：同一地址、同一出口，真浏览器（有界面）也必须点一下勾选框才放行；
    /// 无界面客户端连勾选框都不下发。合成点击也过不去——通过 DevTools 协议发出的点击会让
    /// <c>MouseEvent.screenX/screenY</c> 等于 client 坐标，这个特征被直接判成机器人。
    /// </para>
    /// <para>
    /// <b>纪律（隐私与打扰，两条都硬）</b>：
    /// ① 只允许<b>用户主动发起</b>的抓取调用它（编辑器里点"解析"）；批量 / 导入 / 后台预取一律不许弹窗。
    /// ② 窗口必须是<b>宿主自己的浏览器实例</b>（独立 profile 目录），绝不读取用户浏览器的 cookie 或登录态，
    /// 也不整页注入脚本；站点发的放行凭证只落在这个专用 profile 里，随"清除数据"一并删除。
    /// </para>
    /// </remarks>
    public static Func<string, CancellationToken, Task<RenderedPageMetadata?>>? InteractiveRenderer { get; set; }

    /// <summary>
    /// 按需装配能力（**无头宿主专用**）：真正需要渲染 / SVG / 验证时，由组合根启动能力宿主并接上钩子。
    /// <c>null</c> = 本进程没有可用的装配入口（桌面宿主自己就有本地实现）。
    /// </summary>
    public static Func<CancellationToken, Task<bool>>? EnsureProvider { get; set; }

    /// <summary>
    /// 在真正使用浏览器能力之前调用一次：已有任一钩子 → 直接可用；否则请组合根按需装配。
    /// 返回 <c>true</c> 只表示"某个能力已接上"，具体能力是否可用仍由调用方检查对应钩子。
    /// </summary>
    public static async Task<bool> EnsureAsync(CancellationToken ct)
    {
        // 桌面宿主、已连接的能力桥、以及测试里的手工钩子都走这一条快路径。
        if (Renderer is not null || InteractiveRenderer is not null || FaviconCache.SvgRasterizer is not null)
            return true;

        var ensure = EnsureProvider;
        if (ensure is null) return false;

        try
        {
            if (!await ensure(ct).ConfigureAwait(false)) return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        return Renderer is not null || InteractiveRenderer is not null || FaviconCache.SvgRasterizer is not null;
    }
}
