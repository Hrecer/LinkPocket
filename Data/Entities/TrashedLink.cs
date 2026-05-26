using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

[Table("trashed_links")]
public class TrashedLink
{
    [Key]
    [Required]
    [MaxLength(20)]
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

    [Column("last_visited_at")]
    public DateTime? LastVisitedAt { get; set; }

    [Column("visit_count")]
    public int VisitCount { get; set; } = 0;

    [Column("is_important")]
    public bool IsImportant { get; set; } = false;

    [Column("deleted_at")]
    public DateTime DeletedAt { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
