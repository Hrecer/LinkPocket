using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

/// <summary>
/// 回收站里的被删文件夹单元（Windows 式整树删除，v2 表名 trash_folders）：
/// 删除一个文件夹时，整个子树一次性镜像进本表 —— 删除根的 parent_id = NULL（挂在回收站根），
/// 子文件夹用 parent_id 指向回收站内的父单元，原样保留层级结构。
/// 每行都带删除时的位置标记（origin_folder_id + origin_parent_folder_id + origin_path）与
/// 元数据快照（v5 保真列），为「还原到原位置」备好数据。
/// 回收站里的文件夹是只读的层级展示单元（可进入浏览，不可编辑/改名）。
/// </summary>
[Table("trash_folders")]
public class TrashedFolder
{
    /// <summary>回收站保留原文件夹 ID（删除根 = 原文件夹 ID；子单元 = 原子文件夹 ID）。</summary>
    [Key]
    [Required]
    [MaxLength(20)]
    [Column("id")]
    public string TrashFolderId { get; set; } = Guid.NewGuid().ToString("N")[..16];

    /// <summary>回收站内的父单元；NULL = 回收站根（即「删除操作」的直接对象）。
    /// 属性名沿用旧称 ParentTrashFolderId（与域模型的命名统一另行处理）。</summary>
    [MaxLength(20)]
    [Column("parent_id")]
    public string? ParentTrashFolderId { get; set; }

    [Required]
    [MaxLength(255)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>删除时的原文件夹 ID（= 自身 ID 的身份副本；还原到原位置的**父目录**依据是
    /// <see cref="OriginParentFolderId"/>，不是本列）。</summary>
    [MaxLength(20)]
    [Column("origin_folder_id")]
    public string? OriginFolderId { get; set; }

    /// <summary>删除时该文件夹的父目录 ID（v5；原位还原的数据依据）。NULL = 原在根。
    /// v5 后所有进站单元必有记录（正常路径不存在"原目录未知"）；子单元同样按原父记录
    /// （其父在回收站镜像内，原位语义同样成立）。</summary>
    [MaxLength(20)]
    [Column("origin_parent_folder_id")]
    public string? OriginParentFolderId { get; set; }

    /// <summary>删除时的完整路径快照（如「全部书签 / 工作 / 资料」），回收站「原位置」列直接展示。</summary>
    [MaxLength(512)]
    [Column("origin_path")]
    public string? OriginPath { get; set; }

    /// <summary>删除前的描述快照（v5 保真列，还原时回填；NULL = v5 前进站的数据）。</summary>
    [Column("description")]
    public string? Description { get; set; }

    /// <summary>删除前的排序值快照（v5 保真列）。</summary>
    [Column("sort_order")]
    public int SortOrder { get; set; }

    /// <summary>删除前的创建时间快照（v5 保真列；NULL = v5 前进站的数据，如实未知不伪造）。</summary>
    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    /// <summary>删除前的最后查看时间快照（v5 保真列；NULL = 从未查看或 v5 前进站）。</summary>
    [Column("last_visited_at")]
    public DateTime? LastVisitedAt { get; set; }

    /// <summary>删除前的查看次数快照（v5 保真列）。</summary>
    [Column("visit_count")]
    public int VisitCount { get; set; }

    [Column("deleted_at")]
    public DateTime DeletedAt { get; set; } = DateTime.UtcNow;
}
