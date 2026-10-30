using LinkPocket.Contracts;
using LinkPocket.Kernel;

namespace LinkPocket.Modules.Links;

/// <summary>
/// 元数据抓取<b>编排</b>（internal）：HTTP → 浏览器指纹 → 浏览器内核渲染，三级阶梯；
/// 拿到的 HTML 交给 <see cref="RegexMetadataParser"/>（L0 纯函数）解析。
/// 网络层（客户端 / 重定向 / SSRF 判据 / 指纹通道）在 <see cref="MetadataTransport"/>——
/// 本类型只决定"试谁、何时算成功、拿不到时怎么如实说"。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要阶梯</b>：反爬墙有两种，各自的解法不同。① <b>指纹墙</b>（火绒官网：403/503，Chrome 能过、
/// 脚本客户端一律拒）——请求头补全没用，只有换浏览器 TLS/HTTP2 指纹；② <b>JS 人机校验墙</b>
/// （36kr 首页：200 返回一张"正在进行安全检测"的空壳，靠页内脚本算 proof、写入 cookie 后自行重载）——
/// 指纹、headless 浏览器都不算浏览器，只有真浏览器内核能过。所以阶梯是"先便宜后贵"：
/// 原生 HTTP 拿到就不往下走（绝大多数站点），拿不到才升级，最后一档才起浏览器进程。
/// </para>
/// <para>
/// 网络命令在引擎里走读池（免写闸）——慢站点不阻塞任何数据操作（「网络在闸外」的落点）。
/// </para>
/// </remarks>
internal static class MetadataFetcher
{
    /// <summary>
    /// 浏览器渲染兜底的预算。与抓取自身的 10 s 分开：真浏览器要起进程、跑页内脚本、
    /// 等一次自行重载，10 s 是"取一个 HTML"的口径，套在渲染上会把能成的站点掐死。
    /// </summary>
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(15);

    /// <summary>抓取并解析页面元数据；网络/解析失败抛 <see cref="EngineException"/>（NETWORK_ERROR，可重试）。</summary>
    /// <param name="allowBrowserRender">
    /// 是否允许最后一档（浏览器内核渲染）。**写路径必须传 <c>false</c>**：<c>links.create(auto_fetch_metadata=true)</c>
    /// 的抓取发生在写闸内，起一个浏览器进程最坏能把全局写闸按住 15 s —— "网络在闸外"这条口径不能靠兜底绕过去。
    /// 渲染只服务闸外的查询（<c>links.metadata_fetch</c>）。
    /// </param>
    public static async Task<PageMetadata> FetchAsync(string url, CancellationToken ct, bool allowBrowserRender = true)
    {
        if (!MetadataTransport.IsValidUrl(url))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidUrl, $"URL is not a valid absolute http/https address: {url}"));

        try
        {
            var (html, finalUri) = await MetadataTransport.DownloadAsync(url, ct).ConfigureAwait(false);
            var interstitial = AntiBotInterstitial.Detect(html);
            var metadata = RegexMetadataParser.Instance.Parse(html, finalUri);

            if (!HasUsableText(metadata))
                metadata = await RecoverAsync(finalUri, metadata, interstitial, allowBrowserRender, ct)
                    .ConfigureAwait(false);

            // 走到这里还是只有空壳 ⇒ 如实带出"被验证页挡住了"这一事实（界面据此说真话）
            return metadata with { BlockedByVerification = interstitial && !HasUsableText(metadata) };
        }
        catch (EngineException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EngineException(EngineErrors.Of(
                EngineErrors.NetworkError,
                $"page metadata fetch failed: {ex.Message}",
                retryable: true));
        }
    }

    /// <summary>这份结果里有没有"够用"的文字：标题或描述任一非空即可（只有 favicon 不算）。</summary>
    private static bool HasUsableText(PageMetadata metadata)
        => !string.IsNullOrWhiteSpace(metadata.Title) || !string.IsNullOrWhiteSpace(metadata.Description);

    /// <summary>
    /// 两级兜底：① 拦截页改用浏览器指纹再读一次（纯指纹墙这一下就过）；
    /// ② 仍没有可用文字 → 交宿主的浏览器内核渲染一次（JS 校验页与 CSR 空壳页都靠它）。
    /// </summary>
    private static async Task<PageMetadata> RecoverAsync(
        Uri finalUri, PageMetadata metadata, bool interstitial, bool allowBrowserRender, CancellationToken ct)
    {
        if (interstitial
            && await MetadataTransport.TryGetImpersonatedHtmlAsync(finalUri, ct).ConfigureAwait(false) is { } retried
            && !AntiBotInterstitial.Detect(retried))
        {
            LpLog.Info($"anti-bot page cleared by browser fingerprint: {finalUri.Host}", category: "links.metadata");
            metadata = RegexMetadataParser.Instance.Parse(retried, finalUri);
            if (HasUsableText(metadata)) return metadata;
        }

        // 写路径不启用：见 FetchAsync 的 allowBrowserRender
        if (!allowBrowserRender || PageRenderHook.Renderer is not { } renderer)
            return metadata;   // 无界面宿主（CLI / MCP）同样停在这里：仅 HTTP 的既有口径

        var rendered = await TryRenderAsync(renderer, finalUri, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rendered?.Title) && string.IsNullOrWhiteSpace(rendered?.Description))
            return metadata;

        return metadata with
        {
            Title = string.IsNullOrWhiteSpace(rendered!.Title) ? metadata.Title : rendered.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(rendered.Description)
                ? metadata.Description
                : rendered.Description.Trim(),
        };
    }

    /// <summary>
    /// 调宿主的浏览器渲染器（<see cref="PageRenderHook"/>）；未装配、超时、渲染器自身抛错都返回 <c>null</c>。
    /// </summary>
    private static async Task<RenderedPageMetadata?> TryRenderAsync(
        Func<string, CancellationToken, Task<RenderedPageMetadata?>> renderer, Uri target, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RenderTimeout);
        try
        {
            var rendered = await renderer(target.AbsoluteUri, budget.Token).ConfigureAwait(false);
            LpLog.Info(
                $"page rendered by browser engine (title={(string.IsNullOrWhiteSpace(rendered?.Title) ? "no" : "yes")}): {target.Host}",
                category: "links.metadata");
            return rendered;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LpLog.Warn($"browser render timed out after {RenderTimeout.TotalSeconds:0}s: {target.Host}",
                null, category: "links.metadata");
            return null;
        }
        catch (Exception ex)
        {
            LpLog.Warn($"browser render failed: {target.Host}", ex, category: "links.metadata");
            return null;
        }
    }
}
