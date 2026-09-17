using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

[Table("folders")]
public class Folder
{
    /// <summary>主键：固定 12 位纯数字（与链接 16 位混合串一眼区分），生成入口唯一在 <see cref="EntityIds"/>。</summary>
    [Key]
    [Required]
    [MaxLength(20)]
    [Column("id")]
    public string FolderId { get; set; } = EntityIds.NewFolderId();

    [Required]
    [MaxLength(255)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Column("parent_id")]
    public string? ParentId { get; set; }

    /// <summary>
    /// v2 folders 表不再落 link_count 列（方案 6.1）：目录计数一律经
    /// ITreeService.RecursiveLinkCountsAsync 即时计算并进 DTO，本属性仅存内存语义
    /// （导入/删除等路径的批内直接计数），<c>[NotMapped]</c> 不参与任何 SQL。
    /// </summary>
    [NotMapped]
    public int LinkCount { get; set; } = 0;

    [Column("sort_order")]
    public int SortOrder { get; set; } = 0;

    /// <summary>最后查看（递归继承口径）：子树内任何链接被查看详情/访问时，沿父链所有祖先刷新为当前时间。
    /// 事件：查看。由 FolderService.RecordFolderViewAsync 维护，界面层只读。</summary>
    [Column("last_visited_at")]
    public DateTime? LastVisitedAt { get; set; }

    /// <summary>查看次数（递归继承口径）：子树内任何链接被查看详情/访问时，沿父链所有祖先 +1。
    /// 事件驱动增量计数，不从链接 VisitCount 聚合——移动链接不会转移历史计数。</summary>
    [Column("visit_count")]
    public int VisitCount { get; set; } = 0;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>最后更新 = 内容（含全部子孙）最后变动时间。事件：内容变动
    /// （新增/删除/改名/移入移出链接或子文件夹、链接内容被编辑等）。查看不算变动。
    /// 由 FolderService.TouchModifiedAsync 沿父链维护，界面层只读、不参与计算。</summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    [ForeignKey(nameof(ParentId))]
    public virtual Folder? Parent { get; set; }
    public virtual ICollection<Folder> Children { get; set; } = new List<Folder>();
    public virtual ICollection<Link> Links { get; set; } = new List<Link>();
}
