using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Services;

public class TagService
{
    private readonly LinkPocketDbContext _db;

    public TagService(LinkPocketDbContext db)
    {
        _db = db;
    }

    public async Task<List<Tag>> GetTagsAsync(string sortBy = "name", string sortOrder = "asc")
    {
        IQueryable<Tag> query = _db.Tags.Include(t => t.Links);

        query = sortBy.ToLower() switch
        {
            "view_count" => sortOrder == "asc" ? query.OrderBy(t => t.ViewCount) : query.OrderByDescending(t => t.ViewCount),
            "created_at" => sortOrder == "asc" ? query.OrderBy(t => t.CreatedAt) : query.OrderByDescending(t => t.CreatedAt),
            _ => sortOrder == "asc" ? query.OrderBy(t => t.Name) : query.OrderByDescending(t => t.Name)
        };

        return await query.ToListAsync();
    }

    public async Task<Tag?> GetTagByIdAsync(int id)
    {
        return await _db.Tags
            .Include(t => t.Links.Where(l => !l.IsDeleted))
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<Tag> CreateTagAsync(string name, string? color = null, string? description = null)
    {
        name = name.Trim();
        
        // 检查唯一性
        var existingTag = await _db.Tags.FirstOrDefaultAsync(t => t.Name == name);
        if (existingTag != null)
        {
            throw new Exception("Tag name already exists");
        }

        // 验证颜色格式
        if (!string.IsNullOrEmpty(color) && !System.Text.RegularExpressions.Regex.IsMatch(color, @"^#[0-9A-Fa-f]{6}$"))
        {
            throw new ArgumentException("Invalid color format. Must be hex color like #FF5733");
        }

        var tag = new Tag
        {
            Name = name,
            Color = color ?? "#1976D2",
            Description = description,
            ViewCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Tags.Add(tag);
        await _db.SaveChangesAsync();

        return tag;
    }

    public async Task<Tag> UpdateTagAsync(int id, string? name = null, string? color = null, string? description = null)
    {
        var tag = await _db.Tags.FindAsync(id) ?? throw new Exception("Tag not found");

        // 如果修改名称，检查唯一性
        if (!string.IsNullOrEmpty(name) && name != tag.Name)
        {
            var existingTag = await _db.Tags.FirstOrDefaultAsync(t => t.Name == name.Trim() && t.Id != id);
            if (existingTag != null)
                throw new Exception("Tag name already exists");
            
            tag.Name = name.Trim();
        }

        if (color != null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(color, @"^#[0-9A-Fa-f]{6}$"))
                throw new ArgumentException("Invalid color format");
            tag.Color = color;
        }

        if (description != null) tag.Description = description;

        tag.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return tag;
    }

    public async Task DeleteTagAsync(int id)
    {
        var tag = await _db.Tags
            .Include(t => t.Links)
            .FirstOrDefaultAsync(t => t.Id == id)
            ?? throw new Exception("Tag not found");

        var linkCount = tag.Links.Count;

        // 解除所有关联
        tag.Links.Clear();

        _db.Tags.Remove(tag);
        await _db.SaveChangesAsync();

        System.Diagnostics.Debug.WriteLine($"标签已删除: {tag.Name}, 解除了 {linkCount} 个链接关联");
    }

    public async Task<Tag> MergeTagsAsync(int sourceId, int targetId)
    {
        if (sourceId == targetId)
            throw new ArgumentException("Source and target tags cannot be the same");

        var sourceTag = await _db.Tags
            .Include(t => t.Links)
            .FirstOrDefaultAsync(t => t.Id == sourceId)
            ?? throw new Exception("Source tag not found");

        var targetTag = await _db.Tags
            .Include(t => t.Links)
            .FirstOrDefaultAsync(t => t.Id == targetId)
            ?? throw new Exception("Target tag not found");

        // 转移关联
        foreach (var link in sourceTag.Links.ToList())
        {
            if (!targetTag.Links.Any(l => l.Id == link.Id))
            {
                targetTag.Links.Add(link);
            }
        }

        sourceTag.Links.Clear();
        
        var sourceName = sourceTag.Name;
        var mergedCount = sourceTag.Links.Count;
        
        _db.Tags.Remove(sourceTag);
        await _db.SaveChangesAsync();

        return targetTag;
    }

    public async Task RecordTagViewAsync(int tagId)
    {
        var tag = await _db.Tags.FindAsync(tagId) ?? throw new Exception("Tag not found");
        tag.RecordView();
        await _db.SaveChangesAsync();
    }
}
