using LinkPocket.Data;
using LinkPocket.Api;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Services;

public class FolderService
{
    private LinkPocketDbContext _db;

    public FolderService(LinkPocketDbContext db)
    {
        _db = db;
    }

    public void SetDb(LinkPocketDbContext db)
    {
        _db = db;
    }

    public async Task<List<Folder>> GetTreeAsync()
    {
        return await _db.Folders
            .Include(f => f.Children.OrderBy(c => c.Name))
            .Include(f => f.Links)
            .Where(f => f.ParentId == null)
            .OrderBy(f => f.Name)
            .ToListAsync();
    }

    public async Task<List<Folder>> GetAllFoldersAsync()
    {
        return await _db.Folders
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Name)
            .ToListAsync();
    }

    /// <summary>
    /// 父链上溯原语：返回 folderId 自身 + 全部祖先文件夹（由近及远），null = 根目录。
    /// 文件夹的两个派生时间字段与查看次数都只经这一条链传播，
    /// 保证「事件驱动增量」的口径完全一致：一次事件 → 自身 + 全部祖先同步刷新。
    /// </summary>
    private async Task<List<Folder>> WalkAncestorsAsync(string? folderId)
    {
        var chain = new List<Folder>();
        var current = folderId;

        for (var guard = 0; current != null && guard < 200; guard++)
        {
            var folder = await _db.Folders.FindAsync(current);
            if (folder == null) break;

            chain.Add(folder);
            current = folder.ParentId;
        }

        return chain;
    }

    /// <summary>
    /// 事件：内容变动。把 folderId 及其全部祖先的 UpdatedAt 置为当前时间。
    /// 语义 = 文件夹内容发生了变化（新增/删除/改名/移入移出链接或子文件夹、链接内容被编辑等）。
    /// 注意：仅由「内容变动」驱动，查看链接不算内容变动。
    /// 统一由内核在写路径上调用，界面层只读该字段、不参与计算。
    /// </summary>
    public async Task TouchModifiedAsync(string? folderId, DateTime? at = null)
    {
        var chain = await WalkAncestorsAsync(folderId);
        if (chain.Count == 0) return;

        var stamp = at ?? DateTime.UtcNow;
        foreach (var folder in chain) folder.UpdatedAt = stamp;

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// 事件：子孙链接被查看。把 folderId 及其全部祖先的 LastVisitedAt 刷新为当前时间、VisitCount 各 +1。
    /// 与 <see cref="TouchModifiedAsync"/> 完全同构（同一条父链、同一套事件驱动增量口径），
    /// 区别只在触发事件与写入字段：查看不影响 UpdatedAt，内容变动不影响 LastVisitedAt/VisitCount。
    /// </summary>
    public async Task RecordFolderViewAsync(string? folderId, DateTime? at = null)
    {
        var chain = await WalkAncestorsAsync(folderId);
        if (chain.Count == 0) return;

        var stamp = at ?? DateTime.UtcNow;
        foreach (var folder in chain)
        {
            folder.LastVisitedAt = stamp;
            folder.VisitCount++;
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// 批量刷新全部文件夹的“内容修改时间”，用于导入 / 备份恢复这类一次性大批量写入之后。
    /// </summary>
    public async Task TouchAllModifiedAsync(DateTime? at = null)
    {
        var stamp = at ?? DateTime.UtcNow;
        var folders = await _db.Folders.ToListAsync();
        foreach (var folder in folders) folder.UpdatedAt = stamp;
        await _db.SaveChangesAsync();
    }

    public async Task<Folder?> GetFolderByIdAsync(string id)
    {
        return await _db.Folders
            .Include(f => f.Parent)
            .Include(f => f.Children)
            .Include(f => f.Links.OrderByDescending(l => l.CreatedAt))
            .FirstOrDefaultAsync(f => f.FolderId == id);
    }

    public async Task<Folder> CreateFolderAsync(string name, string? description = null, string? parentId = null)
    {
        // 校验父目录存在性
        if (!string.IsNullOrEmpty(parentId))
        {
            var parent = await _db.Folders.FindAsync(parentId)
                ?? throw new Exception("Parent folder not found");
        }

        var folder = new Folder
        {
            Name = name.Trim(),
            Description = description,
            ParentId = string.IsNullOrEmpty(parentId) ? null : parentId,
            LinkCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Folders.Add(folder);
        await _db.SaveChangesAsync();

        return folder;
    }

    /// <summary>改名 / 改描述（不换父——换父是 <see cref="MoveFolderAsync"/> 的语义，一个动作一个入口）。</summary>
    public async Task<Folder> UpdateFolderAsync(string id, string? name = null, string? description = null)
    {
        var folder = await _db.Folders.FindAsync(id) ?? throw new Exception("Folder not found");

        if (!string.IsNullOrEmpty(name)) folder.Name = name.Trim();
        if (description != null) folder.Description = description;

        folder.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return folder;
    }

    public async Task DeleteFolderAsync(string id, string cascade = "move_to_parent", string? targetListId = null)
    {
        var folder = await _db.Folders
            .Include(f => f.Children)
            .Include(f => f.Links)
            .FirstOrDefaultAsync(f => f.FolderId == id)
            ?? throw new Exception("Folder not found");

        // 获取所有子目录ID（包括自身）
        var descendantIds = await GetDescendantIdsAsync(id);
        descendantIds.Add(id);

        // 获取所有受影响的链接
        var affectedLinks = await _db.Links.Where(l => l.ListId != null && descendantIds.Contains(l.ListId)).ToListAsync();

        switch (cascade)
        {
            case "delete_all":
                foreach (var link in affectedLinks)
                {
                    _db.Links.Remove(link);
                }

                var foldersToDelete = await _db.Folders.Where(f => descendantIds.Contains(f.FolderId)).ToListAsync();
                _db.Folders.RemoveRange(foldersToDelete);
                break;

            case "move_to_list":
                if (string.IsNullOrEmpty(targetListId))
                    throw new ArgumentException("Target list ID is required for move_to_list mode");

                var targetFolder = await _db.Folders.FindAsync(targetListId)
                    ?? throw new Exception("Target folder not found");

                foreach (var link in affectedLinks)
                {
                    link.ListId = targetListId;
                }

                var foldersToMove1 = await _db.Folders.Where(f => descendantIds.Contains(f.FolderId)).ToListAsync();
                _db.Folders.RemoveRange(foldersToMove1);
                targetFolder.UpdateLinkCount(_db);
                break;

            default: // trash_links: 整个文件夹子树镜像进回收站（Windows 式）
                // ① 回收站保留原有 ID（用户定稿 2026-09-17）：TrashFolderId = 原文件夹 ID，
                //    不再生成独立的回收站 ID 系统；子单元的 ParentTrashFolderId = 原父 ID（仅当父也在本子树内，
                //    删除根的父是活目录 → 置 NULL 挂回收站根）。同一文件夹不可能同时存在于主表与回收站，无 ID 冲突。
                // ② 每行定格删除时位置（origin_folder_id/origin_list_id + origin_path）
                // 单次 SaveChanges = 单事务，主表删行与回收站写入原子生效。
                var trashedAt = DateTime.UtcNow;
                var subtreeFolders = await _db.Folders.Where(f => descendantIds.Contains(f.FolderId)).ToListAsync();
                var subtreeIdSet = subtreeFolders.Select(f => f.FolderId).ToHashSet();

                foreach (var f in subtreeFolders)
                {
                    _db.TrashedFolders.Add(new TrashedFolder
                    {
                        TrashFolderId = f.FolderId,
                        ParentTrashFolderId = f.ParentId != null && subtreeIdSet.Contains(f.ParentId) ? f.ParentId : null,
                        Name = f.Name,
                        OriginFolderId = f.FolderId,
                        OriginPath = await OriginPath.BuildFolderPathAsync(_db, f.FolderId),
                        DeletedAt = trashedAt
                    });
                }

                foreach (var link in affectedLinks)
                {
                    _db.TrashedLinks.Add(new TrashedLink
                    {
                        LinkId = link.LinkId,
                        Url = link.Url,
                        Title = link.Title,
                        Description = link.Description,
                        FaviconUrl = link.FaviconUrl,
                        TrashFolderId = link.ListId,
                        OriginListId = link.ListId,
                        OriginPath = await OriginPath.BuildListPathAsync(_db, link.ListId),
                        LastVisitedAt = link.LastVisitedAt,
                        VisitCount = link.VisitCount,
                        IsImportant = link.IsImportant,
                        DeletedAt = trashedAt,
                        CreatedAt = link.CreatedAt,
                        UpdatedAt = DateTime.UtcNow
                    });
                    _db.Links.Remove(link);
                }

                var foldersToTrash = await _db.Folders.Where(f => descendantIds.Contains(f.FolderId)).ToListAsync();
                _db.Folders.RemoveRange(foldersToTrash);
                break;
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>回收站树：全部被删文件夹单元（含删除根与子单元），由 UI 层组装层级。</summary>
    public async Task<List<TrashedFolder>> GetAllTrashFoldersAsync()
    {
        return await _db.TrashedFolders
            .OrderBy(f => f.DeletedAt)
            .ToListAsync();
    }

    /// <summary>
    /// 永久删除一个回收站文件夹单元（含其全部子单元与单元内全部书签快照）。
    /// 仅影响回收站表，主表不受影响。
    /// </summary>
    public async Task PurgeTrashFolderSubtreeAsync(string trashFolderId)
    {
        var all = await _db.TrashedFolders.ToListAsync();
        var ids = new List<string> { trashFolderId };

        // 内存里收拢子树（回收站数据量小，O(n²) 收敛循环足够）
        var added = true;
        while (added)
        {
            added = false;
            foreach (var f in all)
            {
                if (f.ParentTrashFolderId != null && ids.Contains(f.ParentTrashFolderId) && !ids.Contains(f.TrashFolderId))
                {
                    ids.Add(f.TrashFolderId);
                    added = true;
                }
            }
        }

        var links = await _db.TrashedLinks.Where(l => l.TrashFolderId != null && ids.Contains(l.TrashFolderId)).ToListAsync();
        _db.TrashedLinks.RemoveRange(links);
        _db.TrashedFolders.RemoveRange(all.Where(f => ids.Contains(f.TrashFolderId)));

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// 被删文件夹单元的内容（回收站「打开目录」用）：直接子单元（folder 条目）
    /// + 子树内全部书签快照（link 条目）。schema 与平铺条目一致，UI 可直接复用表格/详情。
    /// </summary>
    public async Task<List<TrashEntryDto>> GetTrashUnitContentsAsync(string trashFolderId)
    {
        var all = await _db.TrashedFolders.ToListAsync();
        if (!all.Any(f => f.TrashFolderId == trashFolderId))
            throw new Exception("Trash folder not found");

        // 收拢子树单元 ID（含自身）——与 PurgeTrashFolderSubtreeAsync 同款收敛循环
        var ids = new List<string> { trashFolderId };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var f in all)
            {
                if (f.ParentTrashFolderId != null && ids.Contains(f.ParentTrashFolderId) && !ids.Contains(f.TrashFolderId))
                {
                    ids.Add(f.TrashFolderId);
                    added = true;
                }
            }
        }

        var result = new List<TrashEntryDto>();

        // 直接子单元（folder 条目；子单元内部的更深层内容随其自身被再次打开）
        foreach (var f in all.Where(f => f.ParentTrashFolderId == trashFolderId).OrderBy(f => f.DeletedAt))
        {
            result.Add(new TrashEntryDto
            {
                Id = f.TrashFolderId,
                EntryType = "folder",
                Name = f.Name,
                OriginPath = f.OriginPath,
                DeletedAt = f.DeletedAt
            });
        }

        // 子树内全部书签快照
        var links = await _db.TrashedLinks
            .Where(l => l.TrashFolderId != null && ids.Contains(l.TrashFolderId))
            .OrderByDescending(l => l.DeletedAt)
            .ToListAsync();
        foreach (var l in links)
        {
            result.Add(new TrashEntryDto
            {
                Id = l.LinkId,
                EntryType = "link",
                Name = l.Title ?? l.Url ?? string.Empty,
                Url = l.Url,
                FaviconUrl = l.FaviconUrl,
                OriginPath = l.OriginPath,
                DeletedAt = l.DeletedAt
            });
        }

        return result;
    }

    public async Task<object> GetFolderStatsAsync(string id)
    {
        var folder = await _db.Folders.FindAsync(id) ?? throw new Exception("Folder not found");

        var totalLinks = await _db.Links.CountAsync(l => l.ListId == id);

        var descendantIds = await GetDescendantIdsAsync(id);
        descendantIds.Add(id);

        var totalChildrenLinks = await _db.Links.CountAsync(l => l.ListId != null && descendantIds.Contains(l.ListId));

        var childrenCount = await _db.Folders.CountAsync(f => f.ParentId == id);

        return new
        {
            list_id = id,
            total_links = totalLinks,
            total_children_links = totalChildrenLinks,
            children_count = childrenCount
        };
    }

    /// <summary>重排某父目录下的文件夹顺序；<paramref name="parentId"/> 为 <c>null</c> 表示根目录下的一级文件夹。</summary>
    public async Task UpdateSortAsync(string? parentId, List<string> itemIds)
    {
        IQueryable<Folder> query = _db.Folders;
        var parent = parentId;

        if (parent == null)
        {
            query = query.Where(f => f.ParentId == null);
        }
        else
        {
            query = query.Where(f => f.ParentId == parent);
        }

        var folders = await query.ToListAsync();

        // 验证所有ID都属于该父级
        var validIds = folders.Select(f => f.FolderId).ToList();
        foreach (var itemId in itemIds)
        {
            if (!validIds.Contains(itemId))
                throw new ArgumentException($"Invalid folder ID: {itemId}");
        }

        // 更新排序权重
        for (int i = 0; i < itemIds.Count; i++)
        {
            var folder = await _db.Folders.FindAsync(itemIds[i]);
            if (folder != null)
            {
                folder.SortOrder = i;
            }
        }

        await _db.SaveChangesAsync();
    }

    public async Task<bool> WouldCreateCycleAsync(string folderId, string targetParentId)
    {
        return await WouldCreateCycleInternalAsync(folderId, targetParentId);
    }

    private async Task<bool> WouldCreateCycleInternalAsync(string folderId, string targetParentId)
    {
        string? currentId = targetParentId;
        var maxDepth = 100;

        while (!string.IsNullOrEmpty(currentId) && maxDepth-- > 0)
        {
            if (currentId == folderId)
                return true;

            var parent = await _db.Folders.FindAsync(currentId);
            currentId = parent?.ParentId;
        }

        return false;
    }

    private async Task<List<string>> GetDescendantIdsAsync(string folderId)
    {
        var ids = new List<string>();
        var children = await _db.Folders.Where(f => f.ParentId == folderId).ToListAsync();

        foreach (var child in children)
        {
            ids.Add(child.FolderId);
            ids.AddRange(await GetDescendantIdsAsync(child.FolderId));
        }

        return ids;
    }

    /// <summary>移动文件夹；<paramref name="targetParentId"/> 为 <c>null</c> 表示移到根目录（全部书签）。</summary>
    public async Task MoveFolderAsync(string folderId, string? targetParentId)
    {
        var folder = await _db.Folders.FindAsync(folderId) ?? throw new Exception("Folder not found");
        var target = targetParentId;

        if (target == folderId)
            throw new ArgumentException("Cannot move a folder into itself");

        if (target != null)
        {
            if (await WouldCreateCycleInternalAsync(folderId, target))
                throw new ArgumentException("Moving would create a circular reference");
            if (await _db.Folders.FindAsync(target) == null)
                throw new Exception("Parent folder not found");
        }

        folder.ParentId = target;
        folder.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<string> CopyFolderDeepAsync(string folderId, string? targetParentId)
    {
        var source = await _db.Folders
            .Include(f => f.Children)
            .Include(f => f.Links)
            .FirstOrDefaultAsync(f => f.FolderId == folderId)
            ?? throw new Exception("Folder not found");

        var newFolder = new Folder
        {
            Name = source.Name,
            Description = source.Description,
            ParentId = targetParentId,
            LinkCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Folders.Add(newFolder);
        await _db.SaveChangesAsync();

        foreach (var link in source.Links)
        {
            var newLink = new Link
            {
                Url = link.Url,
                Title = link.Title,
                Description = link.Description,
                FaviconUrl = link.FaviconUrl,
                ListId = newFolder.FolderId,
                LastVisitedAt = link.LastVisitedAt,
                VisitCount = link.VisitCount,
                IsImportant = link.IsImportant,
                CreatedAt = link.CreatedAt,
                UpdatedAt = DateTime.UtcNow
            };
            _db.Links.Add(newLink);
        }

        await CopyChildrenDeepAsync(source.FolderId, newFolder.FolderId);

        await _db.SaveChangesAsync();
        return newFolder.FolderId;
    }

    private async Task CopyChildrenDeepAsync(string sourceParentId, string destParentId)
    {
        var children = await _db.Folders
            .Include(f => f.Links)
            .Where(f => f.ParentId == sourceParentId)
            .ToListAsync();

        foreach (var child in children)
        {
            var newChild = new Folder
            {
                Name = child.Name,
                Description = child.Description,
                ParentId = destParentId,
                LinkCount = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _db.Folders.Add(newChild);
            await _db.SaveChangesAsync();

            foreach (var link in child.Links)
            {
                var newLink = new Link
                {
                    Url = link.Url,
                    Title = link.Title,
                    Description = link.Description,
                    FaviconUrl = link.FaviconUrl,
                    ListId = newChild.FolderId,
                    LastVisitedAt = link.LastVisitedAt,
                    VisitCount = link.VisitCount,
                    IsImportant = link.IsImportant,
                    CreatedAt = link.CreatedAt,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.Links.Add(newLink);
            }

            await CopyChildrenDeepAsync(child.FolderId, newChild.FolderId);
        }
    }
}
