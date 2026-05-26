using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

[Table("lists")]
public class Folder
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [Column(TypeName = "text")]
    public string? Description { get; set; }

    [Column("parent_id")]
    public int? ParentId { get; set; }

    [Column("link_count")]
    public int LinkCount { get; set; } = 0;

    [Column("sort_order")]
    public int SortOrder { get; set; } = 0;

    [Column("last_visited_at")]
    public DateTime? LastVisitedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    [ForeignKey(nameof(ParentId))]
    public virtual Folder? Parent { get; set; }
    public virtual ICollection<Folder> Children { get; set; } = new List<Folder>();
    public virtual ICollection<Link> Links { get; set; } = new List<Link>();

    // 辅助方法
    public void UpdateLinkCount(LinkPocketDbContext db)
    {
        LinkCount = Links.Count;
        db.SaveChanges();
        
        if (ParentId.HasValue)
        {
            var parent = db.Folders.Find(ParentId);
            parent?.UpdateLinkCount(db);
        }
    }
}
