using System.Diagnostics;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.metadata_fetch_batch（Query）：一次抓取多个地址的元数据。
/// <para><b>批处理绝不弹人机验证窗口</b>：需要人点的项以 <see cref="MetadataBatchItemDto.NeedsHumanVerification"/>
/// 标出，其余项照常完成；调用方只对这些项逐条调用 <c>links.metadata_fetch</c>（interactive=true）。
/// 大批量里只有一两项需要弹窗时，不会因为那一两项打断整批。</para>
/// </summary>
internal sealed class LinkMetadataFetchBatchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.metadata_fetch_batch",
        Category: "links",
        Description: "Fetch page metadata for many URLs (HTTP -> browser fingerprint -> optional browser render). Never opens a human-verification window: items that need a human are marked needs_human_verification, and the caller can retry just those with links.metadata_fetch interactive=true. Returns per-item status + summary",
        Parameters: [
            ParamSpec.Req<IReadOnlyList<string>>("urls", "Page URLs (absolute http/https); duplicates are fetched once"),
            ParamSpec.Opt<int>("concurrency", "Parallel fetches (default 4, max 8)"),
            ParamSpec.Opt<bool>("render", "Allow the browser-render fallback when HTTP/fingerprint cannot read the page (default true)"),
        ],
        Caps: CommandCaps.Query | CommandCaps.NetworkOutsideGate |
              CommandCaps.LongRunning | CommandCaps.SupportsCancellation,
        Client: new("LinkMetadataFetchBatchAsync", typeof(MetadataBatchDto)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var urls = CommandArgs.StringArray(args, "urls");
        if (urls.Count == 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, "urls must contain at least one URL", correlationId: ctx.CorrelationId));

        var concurrency = Math.Clamp(CommandArgs.OptionalInt(args, "concurrency", 4), 1, 8);
        var render = CommandArgs.OptionalBool(args, "render", true);
        var distinct = urls.Distinct(StringComparer.Ordinal).ToList();
        var started = Stopwatch.GetTimestamp();

        var items = new MetadataBatchItemDto[distinct.Count];
        using var gate = new SemaphoreSlim(concurrency, concurrency);
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
                items[index] = await FetchOneAsync(url, ctx.Ct, render).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var list = items.ToList();
        return CommandResult.Ok(new MetadataBatchDto
        {
            Items = list,
            Summary = new MetadataBatchSummaryDto
            {
                Total = list.Count,
                Ok = list.Count(i => i.Ok),
                Empty = list.Count(i => !i.Ok && i.ErrorCode is null && !i.BlockedByVerification),
                NeedsHumanVerification = list.Count(i => i.NeedsHumanVerification),
                Blocked = list.Count(i => i.BlockedByVerification && !i.NeedsHumanVerification),
                Failed = list.Count(i => i.ErrorCode is not null),
                ElapsedMs = ElapsedMs(started),
                // 取**结束时**的装配状态：批量里的渲染兜底可能刚刚按需把能力宿主拉起来。
                BrowserRenderAvailable = PageRenderHook.Renderer is not null,
                HumanVerificationAvailable = PageRenderHook.InteractiveRenderer is not null,
            },
        });
    }

    private static async Task<MetadataBatchItemDto> FetchOneAsync(
        string url, CancellationToken ct, bool render)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var outcome = await MetadataFetcher.FetchDetailedAsync(
                url, ct, allowBrowserRender: render, allowHumanVerification: false).ConfigureAwait(false);
            var metadata = outcome.Metadata;
            return new MetadataBatchItemDto
            {
                Url = url,
                // 与页面解析口径一致：有标题或描述才算这条"取到了"；只有图标不算成功。
                Ok = !string.IsNullOrWhiteSpace(metadata.Title) || !string.IsNullOrWhiteSpace(metadata.Description),
                Title = metadata.Title,
                Description = metadata.Description,
                FaviconUrl = metadata.FaviconUrl,
                Resolution = outcome.Resolution,
                NeedsHumanVerification = outcome.NeedsHumanVerification,
                HumanVerificationAvailable = PageRenderHook.InteractiveRenderer is not null,
                BlockedByVerification = metadata.BlockedByVerification,
                ElapsedMs = outcome.ElapsedMs,
            };
        }
        catch (OperationCanceledException)
        {
            return new MetadataBatchItemDto
            {
                Url = url,
                NeedsHumanVerification = false,
                ErrorCode = EngineErrors.Cancelled,
                ErrorMessage = "cancelled",
                ElapsedMs = ElapsedMs(started),
            };
        }
        catch (EngineException ex)
        {
            return new MetadataBatchItemDto
            {
                Url = url,
                ErrorCode = ex.Error.Code,
                ErrorMessage = ex.Error.Message,
                Retryable = ex.Error.Retryable,
                ElapsedMs = ElapsedMs(started),
            };
        }
        catch (Exception ex)
        {
            return new MetadataBatchItemDto
            {
                Url = url,
                ErrorCode = EngineErrors.Internal,
                ErrorMessage = ex.Message,
                ElapsedMs = ElapsedMs(started),
            };
        }
    }

    private static MetadataBatchItemDto CancelledItem(string url) => new()
    {
        Url = url,
        ErrorCode = EngineErrors.Cancelled,
        ErrorMessage = "cancelled",
    };

    private static long ElapsedMs(long started)
        => (long)Math.Min(long.MaxValue,
            (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
}
