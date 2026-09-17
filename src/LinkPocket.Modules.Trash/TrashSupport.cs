using LinkPocket.Data;

namespace LinkPocket.Modules.Trash;

/// <summary>回收站内部支撑：子树收拢（与既有收敛循环逐行等价）与树计数。</summary>
internal static class TrashSupport
{
    /// <summary>
    /// 收拢回收站子树单元 ID（含自身）：parent 在集合内即并入——回收站数据量小，
    /// O(n²) 收敛循环足够（与既有 PurgeTrashFolderSubtreeAsync / GetTrashUnitContentsAsync 同款）。
    /// </summary>
    public static List<string> CollectSubtreeIds(IReadOnlyList<TrashedFolder> all, string rootId)
    {
        var ids = new List<string> { rootId };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var f in all)
            {
                if (f.ParentTrashFolderId != null && ids.Contains(f.ParentTrashFolderId)
                    && !ids.Contains(f.TrashFolderId))
                {
                    ids.Add(f.TrashFolderId);
                    added = true;
                }
            }
        }

        return ids;
    }
}
