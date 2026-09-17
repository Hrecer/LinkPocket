using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

/// <summary>
/// 回收站里的被删书签：
/// trash_folder_id = NULL 表示单独删除、挂在回收站根；
/// 指向 trash_folders 时表示随某个被删文件夹单元一起进来的（回收站树里挂在该单元下）。
/// origin_list_id + origin_path 是删除时的位置标记（还原到原位置的数据依据 +「原位置」列展示）。
/// </summary>
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

    [Column(TypeName = "text")]
    public string? Description { get; set; }

    [MaxLength(512)]
    [Column("favicon_url")]
    public string? FaviconUrl { get; set; }

    /// <summary>回收站内的父单元；NULL = 回收站根（单独删除的书签）。</summary>
    [MaxLength(20)]
    [Column("trash_folder_id")]
    public string? TrashFolderId { get; set; }

    /// <summary>删除时所在文件夹（位置标记）。</summary>
    [MaxLength(20)]
    [Column("origin_list_id")]
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

    /// <summary>必须显式小写：trashed_links 由原始 SQL 建表（created_at/updated_at），
    /// 缺 Column 特性时 EF 按属性名映射成 CreatedAt/UpdatedAt，运行时报 no such column。</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
