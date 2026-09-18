using System.Text.Json;
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
        var runtime = runtimeStats?.Invoke();

        var diagnostics = new
        {
            // 诊断时间统一 UTC（审核 2.8：跨时区调试不混淆）
            generated_at = DateTimeOffset.UtcNow,
            app_version = GetAppVersion(),
            schema_version = await ctx.Uow.SchemaVersionAsync(ct),
            counts = new
            {
                // 审核 1.3：只计数，绝不把整表拉进内存（此前 folders/trash 全量 SELECT）
                folders = await ctx.Uow.Folders.CountAsync(ct),
                links = await ctx.Uow.Links.CountAsync(new LinkFilter(), ct),
                root_links = await ctx.Uow.Links.CountAsync(new LinkFilter { Unfiled = true }, ct),
                // 审核 2.1/2.2：字段名与口径严格对齐——
                // standalone_trash_links = 单独删除的书签（不含随单元删的）；trash_folders_total = 全部被删文件夹（含子单元）
                standalone_trash_links = await ctx.Uow.Trash.CountStandaloneLinksAsync(ct),
                trash_folders_total = await ctx.Uow.Trash.CountFoldersAsync(ct),
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
                observation_failures = runtime.ObservationFailures,
            },
        };
        return CommandResult.Ok(JsonSerializer.SerializeToElement(diagnostics));
    }

    private static string GetAppVersion()
        // 审核 2.7：模块程序集版本才是「LinkPocket 的版本」——GetEntryAssembly 在测试/工具宿主下
        // 会返回宿主版本，语义漂移
        => typeof(MaintenanceModule).Assembly.GetName().Version?.ToString() ?? typeof(MaintenanceModule).Assembly.GetName().Name ?? "unknown";
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
        // 审核 1.2：声明支持取消（ClearAllDataAsync 全程响应 ct；中途取消 → 事务回滚，零部分状态）
        Caps: CommandCaps.Mutation | CommandCaps.Destructive | CommandCaps.SupportsCancellation,
        Impact: ImpactSummary.Database);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        await ctx.Uow.ClearAllDataAsync(ctx.Ct);

        // —— 图标缓存（尽力而为；目录被占用等失败不阻断重置）——
        // dryRun 必须零副作用：文件系统操作不受事务保护，预演时跳过（审核 2.8）。
        // Task.Run：Directory.Delete(递归) 是同步 IO，避免在 UI 线程续体上卡住（审核 2.6）。
        var faviconCleared = ctx.DryRun
            ? false
            : await Task.Run(() => TryClearFaviconCache(), ctx.Ct);

        return CommandResult.Ok(
            // 审核 4.3：强类型 DTO 落形（JsonElement 承载——既有 ExecuteAsync<JsonElement> 调用
            // 形状不变；强类型消费者以 Client.ExecuteAsync<MaintenanceReinitResult> 反序列化获得
            // record 形态，两种调用面都成立）
            JsonSerializer.SerializeToElement(new MaintenanceReinitResult(Cleared: true, FaviconCacheCleared: faviconCleared)),
            new ChangeSet(
                Touched: [new EntityRef("database", "*")],
                Events: [DomainEventNames.LinksChanged, DomainEventNames.FoldersChanged, DomainEventNames.TrashChanged],
                // 审核 2.9：措辞与实现一致——audit_log/idempotency/macros/schema_migrations 有保留策略，不清
                HumanSummary: "已清空业务数据（书签/文件夹/回收站）"));
    }

    private static bool TryClearFaviconCache()
    {
        try
        {
            // 必须与 FaviconStore.CacheDirectory 保持同步（审核 2.3）：Maintenance 模块不引 UIKit，
            // 无法直接引用该常量——未来若图标缓存换目录，这里必须一起改
            var dir = Path.Combine(AppContext.BaseDirectory, "favicons");
            if (!Directory.Exists(dir)) return true;
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            // 审核 2.4：失败要暴露——不得只返回 false 后静默
            System.Diagnostics.Trace.TraceWarning("清空图标缓存失败：{0}", ex.Message);
            return false;
        }
    }
}
