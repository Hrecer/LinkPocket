using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Data;

public class LinkPocketDbContext : DbContext
{
    public string DbPath { get; }

    public LinkPocketDbContext()
    {
        DbPath = System.IO.Path.Join(AppContext.BaseDirectory, "linkpocket.db");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={DbPath}");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Link>(entity =>
        {
            entity.ToTable("links");
            entity.HasKey(e => e.LinkId);
            entity.HasIndex(e => e.Url);
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
            entity.HasKey(e => e.FolderId);
            entity.HasOne(e => e.Parent)
                  .WithMany(f => f.Children)
                  .HasForeignKey(e => e.ParentId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TrashedLink>(entity =>
        {
            entity.ToTable("trashed_links");
            entity.HasKey(e => e.LinkId);
            entity.HasIndex(e => e.DeletedAt);
        });
    }

    public DbSet<Link> Links { get; set; }
    public DbSet<Folder> Folders { get; set; }
    public DbSet<TrashedLink> TrashedLinks { get; set; }
}