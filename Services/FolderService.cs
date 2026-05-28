using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Services;

public class FolderService
{
    private readonly LinkPocketDbContext _db;

    public FolderService(LinkPocketDbContext db)
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

    public async Task<Folder> UpdateFolderAsync(string id, string? name = null, string? description = null, string? parentId = null)
    {
        var folder = await _db.Folders.FindAsync(id) ?? throw new Exception("Folder not found");

        if (parentId != null && parentId != folder.ParentId)
        {
            if (parentId == id)
                throw new ArgumentException("Cannot set folder as its own parent");

            // 检查循环引用
            if (!string.IsNullOrEmpty(parentId) && await WouldCreateCycleAsync(id, parentId))
                throw new ArgumentException("Moving would create a circular reference");

            // 验证新父目录存在
            if (!string.IsNullOrEmpty(parentId) && parentId != "0")
            {
                var parent = await _db.Folders.FindAsync(parentId);
                if (parent == null) throw new Exception("Parent folder not found");
            }
        }

        if (!string.IsNullOrEmpty(name)) folder.Name = name.Trim();
        if (description != null) folder.Description = description;
        if (parentId != null) folder.ParentId = string.IsNullOrEmpty(parentId) || parentId == "0" ? null : parentId;

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

            default: // trash_links: 将书签移至回收站
                foreach (var link in affectedLinks)
                {
                    var trashedLink = new TrashedLink
                    {
                        LinkId = link.LinkId,
                        Url = link.Url,
                        Title = link.Title,
                        Description = link.Description,
                        FaviconUrl = link.FaviconUrl,
                        ListId = link.ListId,
                        LastVisitedAt = link.LastVisitedAt,
                        VisitCount = link.VisitCount,
                        IsImportant = link.IsImportant,
                        DeletedAt = DateTime.UtcNow,
                        CreatedAt = link.CreatedAt,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _db.TrashedLinks.Add(trashedLink);
                    _db.Links.Remove(link);
                }

                var foldersToTrash = await _db.Folders.Where(f => descendantIds.Contains(f.FolderId)).ToListAsync();
                _db.Folders.RemoveRange(foldersToTrash);
                break;
        }

        await _db.SaveChangesAsync();
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

    public async Task UpdateSortAsync(string? parentId, List<string> itemIds)
    {
        IQueryable<Folder> query = _db.Folders;

        if (string.IsNullOrEmpty(parentId) || parentId == "0")
        {
            query = query.Where(f => f.ParentId == null);
        }
        else
        {
            query = query.Where(f => f.ParentId == parentId);
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

    private async Task<bool> WouldCreateCycleAsync(string folderId, string newParentId)
    {
        string? currentId = newParentId;
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
}
