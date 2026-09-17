using Microsoft.EntityFrameworkCore;
using LinkPocket.Api;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>
/// 树算法 EF 实现（方案 4.1，internal 黑盒——经 <see cref="IUnitOfWork.Trees"/> 暴露）。
/// 父链遍历/递归计数的唯一出处；同一工作单元上下文内操作，变更由引擎统一提交。
/// </summary>
internal sealed class EfTreeService(LinkPocketDbContext db) : ITreeService
{
    /// <summary>父链遍历深度护栏（与既有实现一致；库内数据不可能达到）。</summary>
    private const int MaxChainDepth = 200;

    public async Task<IReadOnlyDictionary<FolderId, int>> RecursiveLinkCountsAsync(CancellationToken ct)
    {
        var folders = await db.Folders.AsNoTracking().ToListAsync(ct);
        var directCounts = await db.Links.AsNoTracking()
            .Where(l => l.ListId != null)
            .GroupBy(l => l.ListId!)
            .Select(g => new { FolderId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FolderId, x => x.Count, ct);

        // 子 → 父索引，用于沿父链上溯累加（与既有 GetRecursiveLinkCountsAsync 逐行等价）
        var parentOf = folders.ToDictionary(f => f.FolderId, f => f.ParentId ?? string.Empty);
        var totals = new Dictionary<string, int>();
        foreach (var f in folders)
        {
            var direct = directCounts.TryGetValue(f.FolderId, out var dc) ? dc : 0;
            var cur = f.FolderId;
            for (var i = 0; i < 256 && !string.IsNullOrEmpty(cur); i++)
            {
                totals[cur] = totals.TryGetValue(cur, out var t) ? t + direct : direct;
                if (!parentOf.TryGetValue(cur, out var p) || p == cur) break;
                cur = p;
            }
        }

        return totals.ToDictionary(kv => new FolderId(kv.Key), kv => kv.Value);
    }

    public async Task<IReadOnlyList<FolderId>> AncestorsAsync(FolderId id, bool includeSelf, CancellationToken ct)
    {
        var chain = new List<string>();
        var current = id.Value;
        for (var guard = 0; !string.IsNullOrEmpty(current) && guard < MaxChainDepth; guard++)
        {
            var folder = await db.Folders.AsNoTracking()
                .FirstOrDefaultAsync(f => f.FolderId == current, ct);
            if (folder == null) break;
            chain.Add(folder.FolderId);
            current = folder.ParentId ?? string.Empty;
        }

        if (!includeSelf && chain.Count > 0) chain.RemoveAt(0);
        return chain.Select(x => new FolderId(x)).ToList();
    }

    public async Task<bool> WouldCreateCycleAsync(FolderId id, FolderId targetParent, CancellationToken ct)
    {
        // targetParent 位于 id 子树内（或等于 id）→ 成环：沿 targetParent 上溯找 id
        var current = targetParent.Value;
        for (var guard = 0; !string.IsNullOrEmpty(current) && guard < MaxChainDepth; guard++)
        {
            if (current == id.Value) return true;
            var folder = await db.Folders.AsNoTracking()
                .FirstOrDefaultAsync(f => f.FolderId == current, ct);
            current = folder?.ParentId ?? string.Empty;
        }

        return false;
    }

    public async Task<string> PathDisplayAsync(FolderId? id, CancellationToken ct)
    {
        if (id is null) return FolderIds.RootDisplayName;

        var names = new List<string>();
        var current = id.Value.Value;
        for (var guard = 0; !string.IsNullOrEmpty(current) && guard < MaxChainDepth; guard++)
        {
            var folder = await db.Folders.AsNoTracking()
                .FirstOrDefaultAsync(f => f.FolderId == current, ct);
            if (folder == null) break;
            names.Add(folder.Name);
            current = folder.ParentId ?? string.Empty;
        }

        names.Reverse();
        return FolderIds.RootDisplayName + (names.Count > 0 ? " / " + string.Join(" / ", names) : "");
    }

    public async Task TouchModifiedAsync(FolderId? id, CancellationToken ct)
    {
        var chain = await WalkAncestorsAsync(id?.Value, ct);
        var stamp = DateTime.UtcNow;
        foreach (var folder in chain) folder.UpdatedAt = stamp;
    }

    public async Task RecordFolderViewAsync(FolderId? id, CancellationToken ct)
    {
        var chain = await WalkAncestorsAsync(id?.Value, ct);
        var stamp = DateTime.UtcNow;
        foreach (var folder in chain)
        {
            folder.LastVisitedAt = stamp;
            folder.VisitCount++;
        }
    }

    /// <summary>父链上溯原语：返回 folderId 自身 + 全部祖先（由近及远）；跟踪态实体（供原地写入）。</summary>
    private async Task<List<Folder>> WalkAncestorsAsync(string? folderId, CancellationToken ct)
    {
        var chain = new List<Folder>();
        var current = FolderIds.Normalize(folderId);

        for (var guard = 0; current != null && guard < MaxChainDepth; guard++)
        {
            var folder = await db.Folders.FindAsync([current], ct);
            if (folder == null) break;
            chain.Add(folder);
            current = FolderIds.Normalize(folder.ParentId);
        }

        return chain;
    }
}
