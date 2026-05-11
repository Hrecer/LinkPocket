using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

[Table("links")]
public class Link
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(36)]
    [Column("link_id")]
    public string LinkId { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [MaxLength(2048)]
    public string Url { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Title { get; set; }

    [MaxLength(255)]
    [Column("original_title")]
    public string? OriginalTitle { get; set; }

    [Column(TypeName = "text")]
    public string? Description { get; set; }

    [MaxLength(512)]
    [Column("favicon_url")]
    public string? FaviconUrl { get; set; }

    [Column("list_id")]
    public int? ListId { get; set; }

    [Column("last_visited_at")]
    public DateTime? LastVisitedAt { get; set; }

    [Column("visit_count")]
    public int VisitCount { get; set; } = 0;

    public int Rating { get; set; } = 0;

    [Column("is_important")]
    public bool IsImportant { get; set; } = false;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; } = false;

    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    [ForeignKey(nameof(ListId))]
    public virtual Folder? Folder { get; set; }
    public virtual ICollection<Tag> Tags { get; set; } = new List<Tag>();
    public virtual ICollection<Note> Notes { get; set; } = new List<Note>();
}
