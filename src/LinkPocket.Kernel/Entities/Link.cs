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
    [Column("id")]
    public string LinkId { get; set; } = EntityIds.NewLinkId();

    [Required]
    [MaxLength(2048)]
    [Column("url")]
    public string Url { get; set; } = string.Empty;

    [MaxLength(255)]
    [Column("title")]
    public string? Title { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [MaxLength(512)]
    [Column("favicon_url")]
    public string? FaviconUrl { get; set; }

    /// <summary>所在文件夹（v2 列名 folder_id）；NULL = 根级书签（根不是实体，无哨兵）。
    /// 属性名沿用旧称 ListId（与域模型的命名统一另行处理）。</summary>
    [Column("folder_id")]
    public string? ListId { get; set; }

    [Column("last_visited_at")]
    public DateTime? LastVisitedAt { get; set; }

    [Column("visit_count")]
    public int VisitCount { get; set; } = 0;

    [Column("is_important")]
    public bool IsImportant { get; set; } = false;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    [ForeignKey(nameof(ListId))]
    public virtual Folder? Folder { get; set; }
}
