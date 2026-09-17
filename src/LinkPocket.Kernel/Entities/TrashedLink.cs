using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

/// <summary>
/// 回收站里的被删书签（v2 表名 trash_links）：
/// trash_folder_id = NULL 表示单独删除、挂在回收站根；
/// 指向 trash_folders 时表示随某个被删文件夹单元一起进来的（回收站树里挂在该单元下）。
/// origin_folder_id + origin_path 是删除时的位置标记（还原到原位置的数据依据 +「原位置」列展示）。
/// </summary>
[Table("trash_links")]
public class TrashedLink
{
    /// <summary>回收站保留原链接 ID（不再生成合成回收站 ID）。</summary>
    [Key]
    [Required]
    [MaxLength(20)]
    [Column("id")]
    public string LinkId { get; set; } = Guid.NewGuid().ToString();

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

    /// <summary>回收站内的父单元；NULL = 回收站根（单独删除的书签）。</summary>
    [MaxLength(20)]
    [Column("trash_folder_id")]
    public string? TrashFolderId { get; set; }

    /// <summary>删除时所在文件夹（位置标记）。属性名沿用旧称 OriginListId，语义归位随模型归位阶段统一处理。</summary>
    [MaxLength(20)]
    [Column("origin_folder_id")]
    public string? OriginListId { get; set; }

    /// <summary>删除时的完整路径快照（如「全部书签 / 工作」），「原位置」列展示。</summary>
    [MaxLength(512)]
    [Column("origin_path")]
    public string? OriginPath { get; set; }

    [Column("last_visited_at")]
    public DateTime? LastVisitedAt { get; set; }

    [Column("visit_count")]
    public int VisitCount { get; set; } = 0;

    [Column("is_important")]
    public bool IsImportant { get; set; } = false;

    [Column("deleted_at")]
    public DateTime DeletedAt { get; set; } = DateTime.UtcNow;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
