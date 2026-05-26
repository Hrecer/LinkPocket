using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Data;

public class LinkPocketDbContext : DbContext
{
    public string DbPath { get; }

    public LinkPocketDbContext()
    {
        var folder = Environment.SpecialFolder.LocalApplicationData;
        var path = Environment.GetFolderPath(folder);
        DbPath = System.IO.Path.Join(path, "linkpocket.db");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={DbPath}");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Link>(entity =>
        {
            entity.ToTable("links");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.LinkId).IsUnique();
            entity.HasIndex(e => e.Url);
            entity.HasIndex(e => e.ListId);
            entity.HasIndex(e => e.LastVisitedAt);
            entity.HasIndex(e => e.IsImportant);

            entity.HasOne(e => e.Folder)
                  .WithMany(f => f.Links)
                  .HasForeignKey(e => e.ListId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Folder>(entity =>
        {
            entity.ToTable("lists");
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Parent)
                  .WithMany(f => f.Children)
                  .HasForeignKey(e => e.ParentId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Password>(entity =>
        {
            entity.ToTable("passwords");
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<SearchHistory>(entity =>
        {
            entity.ToTable("search_histories");
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<TrashedLink>(entity =>
        {
            entity.ToTable("trashed_links");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.LinkId).IsUnique();
            entity.HasIndex(e => e.DeletedAt);
        });
    }

    public DbSet<Link> Links { get; set; }
    public DbSet<Folder> Folders { get; set; }
    public DbSet<Password> Passwords { get; set; }
    public DbSet<SearchHistory> SearchHistories { get; set; }
    public DbSet<TrashedLink> TrashedLinks { get; set; }
}