using LinkPocket.Contracts;
using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Services;

/// <summary>
/// 回收站「原位置」路径快照助手：
/// 删除发生时把被删对象的完整层级路径（含根显示名）定格成字符串，
/// 之后无论原文件夹是否被删除/移动，回收站都能展示与还原参照这条快照。
/// 路径形态：「全部书签 / A / B」；根级对象 = 「全部书签」。
/// </summary>
public static class OriginPath
{
    /// <summary>某个链接/文件夹所在的容器路径（不含自身）。listId = null 表示根级。</summary>
    public static async Task<string> BuildListPathAsync(LinkPocketDbContext db, string? listId)
    {
        if (string.IsNullOrEmpty(listId)) return FolderIds.RootDisplayName;

        var names = new List<string>();
        var current = listId;
        for (var guard = 0; !string.IsNullOrEmpty(current) && guard < 200; guard++)
        {
            var folder = await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.FolderId == current);
            if (folder == null) break;
            names.Add(folder.Name);
            current = folder.ParentId;
        }

        names.Reverse();
        return FolderIds.RootDisplayName + (names.Count > 0 ? " / " + string.Join(" / ", names) : "");
    }

    /// <summary>文件夹自身的完整路径（含自身）。用于被删文件夹单元的 origin_path。</summary>
    public static Task<string> BuildFolderPathAsync(LinkPocketDbContext db, string folderId)
        => BuildListPathAsync(db, folderId);
}
