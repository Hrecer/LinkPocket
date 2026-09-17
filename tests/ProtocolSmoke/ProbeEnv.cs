using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Engine;

namespace ProtocolSmoke;

/// <summary>
/// 冒烟组装根（阶段 6 定稿）：全新临时 v2 库（SchemaMigrator 经工厂建库）+ 九模块全量注册
/// + EngineCore（写闸/幂等/确认令牌/审计）+ EngineClient（强类型门面）+ EngineWire（JSON-RPC 面）。
/// 「整库重置」语义 = 丢弃当前引擎与库文件、另起全新实例（引擎无删文件命令，等价终态：全新空库）。
/// </summary>
internal static class ProbeEnv
{
    public static (EngineClient Client, EngineWire Wire, string DbPath) Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lpsmoke_{Guid.NewGuid():N}.db");
        var client = CreateEngineOn(dbPath);
        var wire = new EngineWire(client.Engine);   // 构造期缓存目录 → 必须在注册完成后创建
        return (client, wire, dbPath);
    }

    public static EngineClient CreateEngineOn(string dbPath)
    {
        var factory = new LinkPocketDbContextFactory(dbPath);
        var registry = new CommandRegistry();

        // diagnostics.collect 的 runtime 段由组合根接线（阶段 12）：引擎在注册之后才构造，
        // 故用延迟读取的闭包 —— 引擎 = 观测对象本身，接线不得引入第二份统计源。
        EngineCore? engineRef = null;
        registry.RegisterAll(LinkPocket.Modules.Folders.FoldersModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Links.LinksModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Trash.TrashModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Search.SearchModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Bookmarks.BookmarksModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Backup.BackupModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Dedup.DedupModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Favicon.FaviconModule.CreateHandlers());
        registry.RegisterAll(LinkPocket.Modules.Maintenance.MaintenanceModule.CreateHandlers(
            () => engineRef!.RuntimeStats));

        // 阶段 11 编排层：批引擎/撤销协调器挂引擎 + 16 个编排命令入目录 + audit_log/idempotency 落表
        var engine = new EngineCore(registry, () => new EfUnitOfWork(factory.CreateDbContext()),
            audit: new CompositeAuditWriter(new InMemoryAuditWriter(), new SqlAuditWriter(() => factory.CreateDbContext())),
            idempotency: new SqlIdempotencyStore(() => factory.CreateDbContext()));
        engineRef = engine;
        var stagingRoot = Path.Combine(Path.GetDirectoryName(dbPath)!, $"lpsmoke_staging_{Path.GetFileNameWithoutExtension(dbPath)}");
        registry.RegisterAll(OrchestrationHost.CreateHandlers(engine, () => factory.CreateDbContext(), stagingRoot));
        return new EngineClient(engine);
    }

    /// <summary>尽力清理临时库（连接池句柄滞留会阻止删除，失败不打断流程）。</summary>
    public static void TryDelete(string dbPath)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
        catch
        {
            // 临时文件交给系统清理
        }
    }
}
