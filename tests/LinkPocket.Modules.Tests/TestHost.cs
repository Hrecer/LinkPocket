using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Engine;

namespace LinkPocket.Modules.Tests;

/// <summary>测试支撑：临时文件库（真实 WAL 语义）+ 九模块全量注册。</summary>
internal static class TestHost
{
    public static (EngineCore Engine, LinkPocketDbContextFactory Factory, string DbPath) Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lpmod_{Guid.NewGuid():N}.db");
        // 工厂构造内已完成 WAL 启用 + schema v2 建库（SchemaMigrator），无需 EnsureCreated
        var factory = new LinkPocketDbContextFactory(dbPath);

        var registry = new CommandRegistry();
        registry.RegisterAll(Modules.Folders.FoldersModule.CreateHandlers());
        registry.RegisterAll(Modules.Links.LinksModule.CreateHandlers());
        registry.RegisterAll(Modules.Trash.TrashModule.CreateHandlers());
        registry.RegisterAll(Modules.Search.SearchModule.CreateHandlers());
        registry.RegisterAll(Modules.Bookmarks.BookmarksModule.CreateHandlers());
        registry.RegisterAll(Modules.Backup.BackupModule.CreateHandlers());
        registry.RegisterAll(Modules.Dedup.DedupModule.CreateHandlers());
        registry.RegisterAll(Modules.Favicon.FaviconModule.CreateHandlers());
        registry.RegisterAll(Modules.Maintenance.MaintenanceModule.CreateHandlers());

        return (new EngineCore(registry, () => new EfUnitOfWork(factory.CreateDbContext())), factory, dbPath);
    }
}
