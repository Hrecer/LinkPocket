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
            .Where(f => !f.IsDeleted)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Name)
            .ToListAsync();
    }

    public async Task<Folder?> GetFolderByIdAsync(int id)
    {
        return await _db.Folders
            .Include(f => f.Parent)
            .Include(f => f.Children)
            .Include(f => f.Links.Where(l => !l.IsDeleted).OrderByDescending(l => l.CreatedAt))
            .FirstOrDefaultAsync(f => f.Id == id);
    }

    public async Task<Folder> CreateFolderAsync(string name, string? description = null, int? parentId = null)
    {
        // 校验父目录存在性
        if (parentId.HasValue)
        {
            var parent = await _db.Folders.FindAsync(parentId) 
                ?? throw new Exception("Parent folder not found");
        }

        var folder = new Folder
        {
            Name = name.Trim(),
            Description = description,
            ParentId = parentId,
            LinkCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Folders.Add(folder);
        await _db.SaveChangesAsync();

        return folder;
    }

    public async Task<Folder> UpdateFolderAsync(int id, string? name = null, string? description = null, int? parentId = null)
    {
        var folder = await _db.Folders.FindAsync(id) ?? throw new Exception("Folder not found");

        if (parentId.HasValue && parentId != folder.ParentId)
        {
            if (parentId == id)
                throw new ArgumentException("Cannot set folder as its own parent");

            // 检查循环引用
            if (await WouldCreateCycleAsync(id, parentId.Value))
                throw new ArgumentException("Moving would create a circular reference");

            // 验证新父目录存在
            if (parentId != 0)
            {
                var parent = await _db.Folders.FindAsync(parentId);
                if (parent == null) throw new Exception("Parent folder not found");
            }
        }

        if (!string.IsNullOrEmpty(name)) folder.Name = name.Trim();
        if (description != null) folder.Description = description;
        if (parentId != null) folder.ParentId = parentId == 0 ? null : parentId;

        folder.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return folder;
    }

    public async Task DeleteFolderAsync(int id, string cascade = "move_to_parent", int? targetListId = null)
    {
        var folder = await _db.Folders
            .Include(f => f.Children)
            .Include(f => f.Links)
            .FirstOrDefaultAsync(f => f.Id == id)
            ?? throw new Exception("Folder not found");

        // 获取所有子目录ID（包括自身）
        var descendantIds = await GetDescendantIdsAsync(id);
        descendantIds.Add(id);

        // 获取所有受影响的链接
        var affectedLinks = await _db.Links.Where(l => descendantIds.Contains(l.ListId ?? 0)).ToListAsync();

        switch (cascade)
        {
            case "delete_all":
                // 删除所有子目录和链接
                foreach (var link in affectedLinks)
                {
                    var notes = await _db.Notes.Where(n => n.LinkId == link.Id).ToListAsync();
                    _db.Notes.RemoveRange(notes);
                    link.Tags.Clear();
                    _db.Links.Remove(link);
                }
                
                var foldersToDelete = await _db.Folders.Where(f => descendantIds.Contains(f.Id)).ToListAsync();
                _db.Folders.RemoveRange(foldersToDelete);
                break;

            case "move_to_list":
                if (!targetListId.HasValue)
                    throw new ArgumentException("Target list ID is required for move_to_list mode");
                
                var targetFolder = await _db.Folders.FindAsync(targetListId) 
                    ?? throw new Exception("Target folder not found");

                foreach (var link in affectedLinks)
                {
                    link.ListId = targetListId;
                }

                var foldersToMove1 = await _db.Folders.Where(f => descendantIds.Contains(f.Id)).ToListAsync();
                _db.Folders.RemoveRange(foldersToMove1);
                targetFolder.UpdateLinkCount(_db);
                break;

            default: // move_to_parent
                foreach (var link in affectedLinks)
                {
                    link.ListId = folder.ParentId;
                }

                var foldersToMove2 = await _db.Folders.Where(f => descendantIds.Contains(f.Id)).ToListAsync();
                _db.Folders.RemoveRange(foldersToMove2);

                if (folder.ParentId.HasValue)
                {
                    var parent = await _db.Folders.FindAsync(folder.ParentId);
                    parent?.UpdateLinkCount(_db);
                }
                break;
        }

        await _db.SaveChangesAsync();
    }

    public async Task<object> GetFolderStatsAsync(int id)
    {
        var folder = await _db.Folders.FindAsync(id) ?? throw new Exception("Folder not found");

        var totalLinks = await _db.Links.CountAsync(l => l.ListId == id && !l.IsDeleted);

        var descendantIds = await GetDescendantIdsAsync(id);
        descendantIds.Add(id);

        var totalChildrenLinks = await _db.Links.CountAsync(l => descendantIds.Contains(l.ListId ?? 0) && !l.IsDeleted);

        var childrenCount = await _db.Folders.CountAsync(f => f.ParentId == id);

        return new
        {
            list_id = id,
            total_links = totalLinks,
            total_children_links = totalChildrenLinks,
            children_count = childrenCount
        };
    }

    public async Task UpdateSortAsync(int? parentId, List<int> itemIds)
    {
        IQueryable<Folder> query = _db.Folders;
        
        if (parentId == 0 || parentId == null)
        {
            query = query.Where(f => f.ParentId == null);
        }
        else
        {
            query = query.Where(f => f.ParentId == parentId);
        }

        var folders = await query.ToListAsync();

        // 验证所有ID都属于该父级
        var validIds = folders.Select(f => f.Id).ToList();
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

    private async Task<bool> WouldCreateCycleAsync(int folderId, int newParentId)
    {
        int? currentId = newParentId;
        var maxDepth = 100;

        while (currentId.HasValue && maxDepth-- > 0)
        {
            if (currentId == folderId)
                return true;

            var parent = await _db.Folders.FindAsync(currentId.Value);
            currentId = parent?.ParentId;
        }

        return false;
    }

    private async Task<List<int>> GetDescendantIdsAsync(int folderId)
    {
        var ids = new List<int>();
        var children = await _db.Folders.Where(f => f.ParentId == folderId).ToListAsync();

        foreach (var child in children)
        {
            ids.Add(child.Id);
            ids.AddRange(await GetDescendantIdsAsync(child.Id));
        }

        return ids;
    }

    public async Task SoftDeleteFolderAsync(int folderId)
    {
        var folder = await _db.Folders.FindAsync(folderId)
            ?? throw new Exception("Folder not found");

        folder.IsDeleted = true;
        folder.DeletedAt = DateTime.UtcNow;

        var descendantIds = await GetDescendantIdsAsync(folderId);
        foreach (var childId in descendantIds)
        {
            var child = await _db.Folders.FindAsync(childId);
            if (child != null)
            {
                child.IsDeleted = true;
                child.DeletedAt = DateTime.UtcNow;
            }
        }

        var allFolderIds = descendantIds.Concat(new[] { folderId }).ToList();
        var linksInFolders = await _db.Links
            .Where(l => l.ListId != null && allFolderIds.Contains(l.ListId.Value) && !l.IsDeleted)
            .ToListAsync();
        foreach (var link in linksInFolders)
        {
            link.IsDeleted = true;
            link.DeletedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
    }

    public async Task<List<Folder>> GetDeletedFoldersAsync()
    {
        return await _db.Folders
            .Where(f => f.IsDeleted)
            .OrderByDescending(f => f.DeletedAt)
            .ToListAsync();
    }
}
