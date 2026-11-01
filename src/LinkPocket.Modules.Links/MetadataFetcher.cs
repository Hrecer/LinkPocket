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
            return await FetchCoreAsync(url, ct, allowBrowserRender).ConfigureAwait(false);
        }
        catch (TransportStatusException refused) when (allowBrowserRender)
        {
            // 站点**明确拒绝了这次请求**（拿到了响应，只是状态码不是 2xx）。这类拒绝常常只是"它在挑客户端"，
            // 真页面要靠浏览器跑一遍才给——所以先把最后一档交给浏览器内核。
            var recovered = await RenderInsteadAsync(new Uri(url), ct).ConfigureAwait(false);
            if (recovered is not null) return recovered;

            // 浏览器也进不去：如果这个状态码本身就在拒绝访问（401/403/5xx…），就不要给用户一句
            // "解析失败（详情见日志）"——那既没说清原因，也像是我们坏了。如实说"站点要求浏览器验证"，
            // 并让这一条**不阻断保存**（编辑器把它显示成提示行）。
            if (IsAccessRefusal(refused.StatusCode))
            {
                LpLog.Info($"metadata blocked by the site [{refused.StatusCode}], browser render did not pass either",
                    category: "links.metadata");
                return BlockedMetadata(new Uri(url));
            }

            throw ToEngineException(refused);
        }
        catch (EngineException ex) when (allowBrowserRender && IsTransportRefusal(ex))
        {
            // 连接层面的失败（DNS / 超时 / TLS / 跳转超限）：浏览器也许能过，先试一次；
            // 仍然不行就**如实报错**——那不是"站点不让拿"，而是真的连不上。
            var recovered = await RenderInsteadAsync(new Uri(url), ct).ConfigureAwait(false);
            if (recovered is not null) return recovered;
            throw;
        }
        catch (TransportStatusException refused)
        {
            throw ToEngineException(refused);   // 写路径（不启渲染）：维持既有口径
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

    /// <summary>
    /// 传输层失败里"值得再让浏览器试一次"的那一类：网络/状态码层面的拒绝。
    /// <b>不含</b> <see cref="EngineErrors.InvalidUrl"/>——地址非法、重定向跑出 http/https 属于判据拒绝，
    /// 换谁来访问都不该放行。
    /// </summary>
    private static bool IsTransportRefusal(EngineException ex)
        => ex.Error.Code == EngineErrors.NetworkError;

    /// <summary>这个状态码是不是"拒绝为你服务"（而不是"没有这个页面"）：前者可以降级成提示，后者要如实报错。</summary>
    private static bool IsAccessRefusal(int statusCode)
        => statusCode is 401 or 403 or 405 or 406 or 451 || statusCode >= 500;

    /// <summary>把状态码拒绝还原成引擎错误（对外口径不变：仍是 LP.ENG.002）。</summary>
    private static EngineException ToEngineException(TransportStatusException refused)
        => new(EngineErrors.Of(EngineErrors.NetworkError, refused.Message, retryable: true));

    /// <summary>
    /// "站点拒绝、浏览器也没过"时的结果：标题/描述皆空 + 拦截标记（编辑器据此显示提示行），
    /// 图标按站点根回落——与"页面没有 link 标签"同一口径。
    /// </summary>
    private static PageMetadata BlockedMetadata(Uri target)
        => RegexMetadataParser.Instance.Parse(string.Empty, target) with { BlockedByVerification = true };

    private static async Task<PageMetadata> FetchCoreAsync(string url, CancellationToken ct, bool allowBrowserRender)
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

    /// <summary>
    /// 传输层被拒后的最后一档：交浏览器内核渲染。失败返回 <c>null</c>（调用方按原口径报错）。
    /// <para>
    /// 图标按站点根回落（<c>/favicon.ico</c>）：这条路径没有正文可解析，与"页面没有 link 标签"同一口径。
    /// </para>
    /// </summary>
    private static async Task<PageMetadata?> RenderInsteadAsync(Uri target, CancellationToken ct)
    {
        if (PageRenderHook.Renderer is not { } renderer) return null;

        try
        {
            // 与网络路径同一套连接前判据：渲染也不能成为绕过环回 / 链路本地拒绝的后门
            await MetadataTransport.EnsureRenderTargetAsync(target, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LpLog.Warn($"browser render refused by address policy: {target.Host}", ex, category: "links.metadata");
            return null;
        }

        var rendered = await TryRenderAsync(renderer, target, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rendered?.Title) && string.IsNullOrWhiteSpace(rendered?.Description))
            return null;

        var metadata = RegexMetadataParser.Instance.Parse(string.Empty, target);
        return metadata with
        {
            Title = string.IsNullOrWhiteSpace(rendered!.Title) ? null : rendered.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(rendered.Description) ? null : rendered.Description.Trim(),
            BlockedByVerification = false,
        };
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
