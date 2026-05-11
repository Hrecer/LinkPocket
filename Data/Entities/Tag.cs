using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

[Table("tags")]
public class Tag
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(7)]
    public string? Color { get; set; }

    [Column(TypeName = "text")]
    public string? Description { get; set; }

    [Column("view_count")]
    public int ViewCount { get; set; } = 0;

    [Column("last_viewed_at")]
    public DateTime? LastViewedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    public virtual ICollection<Link> Links { get; set; } = new List<Link>();

    public void RecordView()
    {
        ViewCount++;
        LastViewedAt = DateTime.UtcNow;
    }
}
