using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

/// <summary>
/// 回收站里的被删文件夹单元（Windows 式整树删除）：
/// 删除一个文件夹时，整个子树一次性镜像进本表 —— 删除根的 parent_trash_folder_id = NULL（挂在回收站根），
/// 子文件夹用 parent_trash_folder_id 指向回收站内的父单元，原样保留层级结构。
/// 每行都带删除时的位置标记（origin_folder_id + origin_path），为「还原到原位置」备好数据。
/// 回收站里的文件夹只是层级展示单元，不可被打开/导航。
/// </summary>
[Table("trash_folders")]
public class TrashedFolder
{
    [Key]
    [Required]
    [MaxLength(20)]
    [Column("trash_folder_id")]
    public string TrashFolderId { get; set; } = Guid.NewGuid().ToString("N")[..16];

    /// <summary>回收站内的父单元；NULL = 回收站根（即「删除操作」的直接对象）。</summary>
    [MaxLength(20)]
    [Column("parent_trash_folder_id")]
    public string? ParentTrashFolderId { get; set; }

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>删除时的原文件夹 ID（还原到原位置的数据依据）。</summary>
    [MaxLength(20)]
    [Column("origin_folder_id")]
    public string? OriginFolderId { get; set; }

    /// <summary>删除时的完整路径快照（如「全部书签 / 工作 / 资料」），回收站「原位置」列直接展示。</summary>
    [MaxLength(512)]
    [Column("origin_path")]
    public string? OriginPath { get; set; }

    [Column("deleted_at")]
    public DateTime DeletedAt { get; set; } = DateTime.UtcNow;
}
