using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

[Table("links")]
public class Link
{
    /// <summary>主键：固定 16 位大小写字母+数字混合（与文件夹 12 位纯数字一眼区分），生成入口唯一在 <see cref="EntityIds"/>。</summary>
    [Key]
    [Required]
    [MaxLength(20)]
    [Column("link_id")]
    public string LinkId { get; set; } = EntityIds.NewLinkId();

    [Required]
    [MaxLength(2048)]
    public string Url { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Title { get; set; }

    [Column(TypeName = "text")]
    public string? Description { get; set; }

    [MaxLength(512)]
    [Column("favicon_url")]
    public string? FaviconUrl { get; set; }

    [Column("list_id")]
    public string? ListId { get; set; }

    [Column("last_visited_at")]
    public DateTime? LastVisitedAt { get; set; }

    [Column("visit_count")]
    public int VisitCount { get; set; } = 0;

    [Column("is_important")]
    public bool IsImportant { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    [ForeignKey(nameof(ListId))]
    public virtual Folder? Folder { get; set; }
}