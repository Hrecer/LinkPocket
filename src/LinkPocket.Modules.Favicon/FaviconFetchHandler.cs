using System.Diagnostics;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Favicon;

/// <summary>
/// favicon.fetch（Query）：**等待**一批图标地址在缓存里就绪，并逐项返回结果/原因。
/// <para>与后台预取、界面回填、备份搬运共用 <see cref="FaviconCache.EnsureCachedAsync(string?, CancellationToken)"/>
/// 这一条管线（同一个并发闸、同地址在飞去重、同一份磁盘文件），不是另算一遍。</para>
/// <para>只读查询、闸外执行；每项自带超时。调用方据此说明"为什么这张图没有/慢了"，而不是只看到一张占位图。</para>
/// </summary>
internal sealed class FaviconFetchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "favicon.fetch",
        Category: "favicon",
        Description: "Wait for favicon cache availability for many URLs and return per-item status (cached / inline_data / not_found / blocked / svg_rasterizer_unavailable / timeout / ...); uses the same cache pipeline as prefetch and the UI",
        Parameters: [
            ParamSpec.Req<IReadOnlyList<string>>("favicon_urls", "Favicon URLs (duplicates are fetched once)"),
            ParamSpec.Opt<int>("timeout_ms", "Per-item timeout in milliseconds (default 10000, max 60000)"),
        ],
        Caps: CommandCaps.Query | CommandCaps.NetworkOutsideGate |
              CommandCaps.LongRunning | CommandCaps.SupportsCancellation,
        Client: new("FaviconFetchAsync", typeof(FaviconFetchDto)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var urls = CommandArgs.StringArray(args, "favicon_urls");
        if (urls.Count == 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, "favicon_urls must contain at least one URL", correlationId: ctx.CorrelationId));

        var timeoutMs = Math.Clamp(CommandArgs.OptionalInt(args, "timeout_ms", 10_000), 1_000, 60_000);
        var distinct = urls.Distinct(StringComparer.Ordinal).ToList();
        var started = Stopwatch.GetTimestamp();

        var items = new FaviconFetchItemDto[distinct.Count];
        using var gate = new SemaphoreSlim(4, 4);
        await Task.WhenAll(distinct.Select(async (url, index) =>
        {
            try
            {
                await gate.WaitAsync(ctx.Ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                items[index] = CancelledItem(url);
                return;
            }

            try
            {
                items[index] = await FetchOneAsync(url, timeoutMs, ctx.Ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var list = items.ToList();
        return CommandResult.Ok(new FaviconFetchDto
        {
            Items = list,
            Summary = new FaviconFetchSummaryDto
            {
                Total = list.Count,
                Cached = list.Count(i => i.Cached),
                Failed = list.Count(i => !i.Cached),
                ElapsedMs = ElapsedMs(started),
                // 取结束时的状态：SVG 项可能刚刚触发按需装配。
                SvgRasterizerAvailable = FaviconCache.SvgRasterizer is not null,
            },
        });
    }

    private static async Task<FaviconFetchItemDto> FetchOneAsync(
        string url, int timeoutMs, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeoutMs);
        try
        {
            var outcome = await FaviconCache.EnsureCachedDetailedAsync(url, budget.Token).ConfigureAwait(false);
            return new FaviconFetchItemDto
            {
                FaviconUrl = url,
                Cached = outcome.Cached,
                CachePath = outcome.Cached ? FaviconCache.TryGetCacheFilePath(url) : null,
                Reason = outcome.Reason,
                ElapsedMs = ElapsedMs(started),
            };
        }
        catch (OperationCanceledException)
        {
            return new FaviconFetchItemDto
            {
                FaviconUrl = url,
                Reason = ct.IsCancellationRequested
                    ? FaviconFetchReasons.Cancelled
                    : FaviconFetchReasons.Timeout,
                ElapsedMs = ElapsedMs(started),
            };
        }
        catch (Exception ex)
        {
            LpLog.Warn($"favicon.fetch failed: {ex.GetType().Name}", ex, category: "favicon");
            return new FaviconFetchItemDto
            {
                FaviconUrl = url,
                Reason = FaviconFetchReasons.Failed,
                ElapsedMs = ElapsedMs(started),
            };
        }
    }

    private static FaviconFetchItemDto CancelledItem(string url) => new()
    {
        FaviconUrl = url,
        Reason = FaviconFetchReasons.Cancelled,
    };

    private static long ElapsedMs(long started)
        => (long)Math.Min(long.MaxValue,
            (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
}
