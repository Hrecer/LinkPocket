using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Data;

/// <summary>
/// 目录直接链接计数沿父链逐级刷新（原 Folder 实体实例方法平移为扩展方法，语义逐字保留）：
/// LinkCount = 导航集合 Links.Count → SaveChanges → 递归父级。
/// 调用方语义依赖：调用前必须已加载目标文件夹的 Links 导航（与原实现完全一致）。
/// </summary>
public static class FolderCountSync
{
    public static void UpdateLinkCount(this Folder folder, LinkPocketDbContext db)
    {
        folder.LinkCount = folder.Links.Count;
        db.SaveChanges();

        if (!string.IsNullOrEmpty(folder.ParentId))
        {
            var parent = db.Folders.Find(folder.ParentId);
            parent?.UpdateLinkCount(db);
        }
    }
}
