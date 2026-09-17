using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Data;

public class LinkPocketDbContext : DbContext
{
    public string DbPath { get; }

    private readonly string? _connectionStringOverride;

    public LinkPocketDbContext() : this(null)
    {
    }

    /// <summary>允许指定数据库文件路径（测试/数据工具用）；默认取程序运行目录。</summary>
    public LinkPocketDbContext(string? dbPath)
    {
        DbPath = dbPath ?? System.IO.Path.Join(AppContext.BaseDirectory, "linkpocket.db");
    }

    /// <summary>引擎工厂专用：直接给定连接串（含 WAL 库 + foreign_keys 连接项）。同一程序集内部使用。</summary>
    internal LinkPocketDbContext(string? contextPath, string connectionStringOverride)
    {
        DbPath = contextPath ?? System.IO.Path.Join(AppContext.BaseDirectory, "linkpocket.db");
        _connectionStringOverride = connectionStringOverride;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite(_connectionStringOverride ?? $"Data Source={DbPath}");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ⚠️ 索引的单一事实源是 SchemaMigrator 的 DDL 脚本（建库走原生 SQL，EF 的 EnsureCreated/迁移都不参与）。
        // 这里的 HasIndex 声明只用于「模型与库形状一致」的可读性/审计；阶段 12 索引复核已逐条对齐：
        // 删掉基线里并不存在的 links(is_important)，补上 v3 新加的 created_at / url COLLATE NOCASE / trash_folders(deleted_at)。
        modelBuilder.Entity<Link>(entity =>
        {
            entity.ToTable("links");
            entity.HasKey(e => e.LinkId);
            entity.HasIndex(e => e.Url);
            entity.HasIndex(e => e.LastVisitedAt);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.UpdatedAt);
            // ListId 的索引由下面的 FK 关系自动派生（idx_links_folder），不重复声明

            entity.HasOne(e => e.Folder)
                  .WithMany(f => f.Links)
                  .HasForeignKey(e => e.ListId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Folder>(entity =>
        {
            entity.ToTable("folders");
            entity.HasKey(e => e.FolderId);
            // parent_id 索引由自引用 FK 自动派生（idx_folders_parent），不重复声明
            entity.HasOne(e => e.Parent)
                  .WithMany(f => f.Children)
                  .HasForeignKey(e => e.ParentId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TrashedLink>(entity =>
        {
            entity.ToTable("trash_links");
            entity.HasKey(e => e.LinkId);
            entity.HasIndex(e => e.DeletedAt);
            entity.HasIndex(e => e.TrashFolderId);
        });

        modelBuilder.Entity<TrashedFolder>(entity =>
        {
            entity.ToTable("trash_folders");
            entity.HasKey(e => e.TrashFolderId);
            entity.HasIndex(e => e.DeletedAt);
            entity.HasIndex(e => e.ParentTrashFolderId);
        });
    }

    public DbSet<Link> Links { get; set; }
    public DbSet<Folder> Folders { get; set; }
    public DbSet<TrashedLink> TrashedLinks { get; set; }
    public DbSet<TrashedFolder> TrashedFolders { get; set; }

    // ===== 主键碰撞兜底（零成本：正常路径不多任何查询） =====
    // 新增 Folder/Link 的随机 ID 理论上可能撞上既有主键（概率宇宙级，见 EntityIds 注释）；
    // SQLite 会让插入抛 UNIQUE constraint failed 而非静默坏数据。这里捕获后给该批新增实体换号
    // （并同步修正同批次内互相引用的 ParentId/ListId）重试一次；二次仍失败则原样抛出。
    // 覆盖全部保存路径：新建/复制粘贴/书签导入/备份恢复统一走这两个重载。

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateException ex) when (IsFolderLinkPkCollision(ex))
        {
            if (!RegenerateConflictedIds()) throw;
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        try
        {
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsFolderLinkPkCollision(ex))
        {
            if (!RegenerateConflictedIds()) throw;
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }

    /// <summary>是否为 folders.id / links.id 主键唯一冲突（回收站表的冲突不在此列，语义不同）。</summary>
    private static bool IsFolderLinkPkCollision(DbUpdateException ex)
    {
        if (ex.InnerException is not SqliteException se || se.SqliteErrorCode != 19) return false;
        var msg = se.Message;
        return msg.Contains("folders.id") || msg.Contains("links.id");
    }

    /// <summary>给本批所有新增 Folder/Link 换新 ID，并把同批次内指向它们的 ParentId/ListId 一并改指新号。
    /// 返回 false 表示批内没有可换号的新增实体（冲突另有原因），交回调用方抛出。</summary>
    private bool RegenerateConflictedIds()
    {
        var folders = ChangeTracker.Entries<Folder>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        var links = ChangeTracker.Entries<Link>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        if (folders.Count == 0 && links.Count == 0) return false;

        var map = new Dictionary<string, string>();
        foreach (var f in folders) map[f.FolderId] = f.FolderId = EntityIds.NewFolderId();
        foreach (var l in links) map[l.LinkId] = l.LinkId = EntityIds.NewLinkId();
        foreach (var f in folders)
            if (f.ParentId != null && map.TryGetValue(f.ParentId, out var newParentId)) f.ParentId = newParentId;
        foreach (var l in links)
            if (l.ListId != null && map.TryGetValue(l.ListId, out var newListId)) l.ListId = newListId;
        return true;
    }
}