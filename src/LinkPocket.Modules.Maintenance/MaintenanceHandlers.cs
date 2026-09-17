using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Maintenance;

/// <summary>maintenance.schema_version（Query）：当前库 schema 版本（schema_migrations MAX(version)，v2 全新建库起步）。</summary>
internal sealed class MaintenanceSchemaVersionHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "maintenance.schema_version",
        Category: "maintenance",
        Description: "取当前数据库 schema 版本",
        Parameters: [],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
        => CommandResult.Ok(JsonSerializer.SerializeToElement(
            new { schema_version = await ctx.Uow.SchemaVersionAsync(ctx.Ct) }));
}

/// <summary>
/// diagnostics.collect（Query）：脱敏诊断信息打包（版本 / schema / 各表计数 / 运行时可观测读数）。
/// runtime 段由组合根接线提供（阶段 12：查询缓存与事件存储读数）；未接线则为 null（不填假值）。
/// </summary>
internal sealed class DiagnosticsCollectHandler(Func<EngineRuntimeStats>? runtimeStats) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "diagnostics.collect",
        Category: "maintenance",
        Description: "收集诊断信息：应用版本 / schema 版本 / 各表计数 / 缓存与事件存储读数（脱敏）",
        Parameters: [],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var ct = ctx.Ct;
        var standalone = await ctx.Uow.Trash.ListStandaloneLinksAsync(ct);
        var units = await ctx.Uow.Trash.ListFoldersAsync(ct);
        var runtime = runtimeStats?.Invoke();

        var diagnostics = new
        {
            generated_at = DateTimeOffset.Now,
            app_version = GetAppVersion(),
            schema_version = await ctx.Uow.SchemaVersionAsync(ct),
            counts = new
            {
                folders = (await ctx.Uow.Folders.ListAllAsync(ct)).Count,
                links = await ctx.Uow.Links.CountAsync(new LinkFilter(), ct),
                root_links = await ctx.Uow.Links.CountAsync(new LinkFilter { Unfiled = true }, ct),
                trash_links = standalone.Count,
                trash_units = units.Count,
            },
            // null = 未接线（观测面纪律：不假装有数据）；接线后即为引擎缓存/事件存储真实读数
            runtime = runtime is null ? null : new
            {
                cache_entries = runtime.CacheEntries,
                cache_hits = runtime.CacheHits,
                cache_misses = runtime.CacheMisses,
                cache_evictions = runtime.CacheEvictions,
                cache_invalidations = runtime.CacheInvalidations,
                cache_hit_rate = Math.Round(runtime.CacheHitRate, 4),
                event_store_head = runtime.EventStoreHead,
            },
        };
        return CommandResult.Ok(JsonSerializer.SerializeToElement(diagnostics));
    }

    private static string GetAppVersion()
        => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
}

/// <summary>
/// maintenance.reinit（Mutation · Destructive 两阶段确认）：整库重置——
/// 清空全部数据表（链接/文件夹/回收站两表）并尽力清除图标缓存目录。
/// 旧实现是"删库文件再建"，引擎语义等价改为"单事务清空全部行"（同一用户可见终态：空库）。
/// 清空走 <see cref="IUnitOfWork.ClearAllDataAsync"/> 的**批量删除**：10k 库下逐条 DELETE 是一万次往返。
/// </summary>
internal sealed class MaintenanceReinitHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "maintenance.reinit",
        Category: "maintenance",
        Description: "整库重置：清空全部数据（链接/文件夹/回收站）并清除图标缓存；不可恢复，需两阶段确认",
        Parameters: [],
        Caps: CommandCaps.Mutation | CommandCaps.Destructive,
        Impact: ImpactSummary.Database);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        await ctx.Uow.ClearAllDataAsync(ctx.Ct);

        // —— 图标缓存（尽力而为；目录被占用等失败不阻断重置）——
        var faviconCleared = TryClearFaviconCache();

        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new { cleared = true, favicon_cache_cleared = faviconCleared }),
            new ChangeSet(
                Touched: [new EntityRef("database", "*")],
                Events: ["links.changed", "folders.changed", "trash.changed"],
                HumanSummary: "已清空全部数据"));
    }

    private static bool TryClearFaviconCache()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "favicons");
            if (!Directory.Exists(dir)) return true;
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
