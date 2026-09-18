using Microsoft.EntityFrameworkCore;
using LinkPocket.Contracts;
using LinkPocket.Kernel;

namespace LinkPocket.Data;

/// <summary>
/// 树算法 EF 实现（方案 4.1，internal 黑盒——经 <see cref="IUnitOfWork.Trees"/> 暴露）。
/// 父链遍历/递归计数的唯一出处；同一工作单元上下文内操作，变更由引擎统一提交。
///
/// 父链一律「一次取索引 + 内存走链」（取代每级一次查询的 N+1）；
/// 环检测用**已访问集合**（真环检测），不靠深度硬截断——坏数据必须被识别而不是被静默吞掉。
/// </summary>
internal sealed class EfTreeService(LinkPocketDbContext db) : ITreeService
{
    public async Task<FolderLinkCounts> LinkCountsAsync(CancellationToken ct)
    {
        var parents = await LoadParentIndexAsync(ct);
        var direct = await DirectCountsAsync(ct);

        // 记忆化递归（审核 1.1）：原先「每个有直接链接的文件夹都独立沿父链上溯累加」复杂度 O(N×深度)，
        // 链式树形下退化为 O(N²)；这里把递归计数定义为 count(x) = direct[x] + Σ count(子)，
        // 每个节点恰好计算一次 → 整体 O(N)（文件夹数量级下递归深度受用户建树能力限制，安全）。
        var children = parents
            .Where(kv => kv.Value is not null)
            .GroupBy(kv => kv.Value!)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList(), StringComparer.Ordinal);

        var recursive = new Dictionary<string, int>(StringComparer.Ordinal);
        var memo = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);   // 环保护（坏数据不无限递归，也不把环上重复节点多算一次）

        int CountRecursive(string id)
        {
            if (memo.TryGetValue(id, out var cached)) return cached;
            if (!visiting.Add(id)) return 0;   // 环内重复访问：环上节点的计数值由首次进入的路径累加

            var total = direct.TryGetValue(new FolderId(id), out var dc) ? dc : 0;
            if (children.TryGetValue(id, out var kids))
                foreach (var kid in kids)
                    total += CountRecursive(kid);

            visiting.Remove(id);
            memo[id] = total;
            return total;
        }

        foreach (var id in parents.Keys)
            recursive[id] = CountRecursive(id);

        return new FolderLinkCounts(
            direct,
            recursive.ToDictionary(kv => new FolderId(kv.Key), kv => kv.Value));
    }

    public async Task<IReadOnlyList<FolderId>> AncestorsAsync(FolderId id, bool includeSelf, CancellationToken ct)
    {
        var parents = await LoadParentIndexAsync(ct);
        var chain = WalkExisting(id.Value, parents);
        if (!includeSelf && chain.Count > 0) chain.RemoveAt(0);
        return chain.Select(x => new FolderId(x)).ToList();
    }

    public async Task<bool> WouldCreateCycleAsync(FolderId id, FolderId targetParent, CancellationToken ct)
    {
        // targetParent 位于 id 子树内（或等于 id）→ 成环：沿 targetParent 上溯找 id
        var parents = await LoadParentIndexAsync(ct);
        return WalkExisting(targetParent.Value, parents).Contains(id.Value);
    }

    public async Task<string> PathDisplayAsync(FolderId? id, CancellationToken ct)
    {
        if (id is null) return FolderIds.RootDisplayName;

        var nodes = await LoadNodeIndexAsync(ct);
        // 审核 2.3：id 指向的文件夹在库里不存在（数据不一致/已被删）时，必须如实标记「未知目录」，
        // 不得把空路径伪装成「全部书签」——回收站的 OriginPath 会拿这个结果做快照，误导用户以为是根目录。
        if (!nodes.ContainsKey(id.Value.Value))
            return "未知目录";

        var names = WalkExisting(id.Value.Value, ToParentIndex(nodes))
            .Select(x => nodes[x].Name)
            .ToList();

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

    // ===== 内部原语 =====

    /// <summary>ID → 父 ID 索引（一次全量查询；文件夹数量有限，遍历一律在内存完成）。</summary>
    private async Task<Dictionary<string, string?>> LoadParentIndexAsync(CancellationToken ct)
        => await db.Folders.AsNoTracking()
            .Select(f => new { f.FolderId, f.ParentId })
            .ToDictionaryAsync(f => f.FolderId, f => f.ParentId, ct);

    private async Task<Dictionary<string, FolderNode>> LoadNodeIndexAsync(CancellationToken ct)
    {
        var rows = await db.Folders.AsNoTracking()
            .Select(f => new { f.FolderId, f.ParentId, f.Name })
            .ToListAsync(ct);
        return rows.ToDictionary(
            r => r.FolderId, r => new FolderNode(r.FolderId, r.ParentId, r.Name), StringComparer.Ordinal);
    }

    private static Dictionary<string, string?> ToParentIndex(Dictionary<string, FolderNode> nodes)
    {
        var parents = new Dictionary<string, string?>(nodes.Count, StringComparer.Ordinal);
        foreach (var (id, node) in nodes) parents[id] = node.ParentId;
        return parents;
    }

    private async Task<IReadOnlyDictionary<FolderId, int>> DirectCountsAsync(CancellationToken ct)
        => (await db.Links.AsNoTracking()
                .Where(l => l.ListId != null)
                .GroupBy(l => l.ListId!)
                .Select(g => new { FolderId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => new FolderId(x.FolderId), x => x.Count);

    /// <summary>
    /// 父链上溯原语：返回 folderId 自身 + 全部祖先（由近及远）；跟踪态实体（供原地写入）。
    /// 两趟：先取「ID → 父」索引走链，再按命中 ID 一次取回跟踪态实体（EF 身份解析复用已跟踪实例）。
    /// </summary>
    private async Task<List<Folder>> WalkAncestorsAsync(string? folderId, CancellationToken ct)
    {
        var start = folderId;
        if (start == null) return [];

        var chainIds = WalkExisting(start, await LoadParentIndexAsync(ct));
        if (chainIds.Count == 0) return [];

        var tracked = await db.Folders.Where(f => chainIds.Contains(f.FolderId)).ToListAsync(ct);
        var byId = tracked.ToDictionary(f => f.FolderId, StringComparer.Ordinal);

        // 按链序返回（EF 不保证返回顺序），并跳过链上已缺失的节点
        var chain = new List<Folder>(chainIds.Count);
        foreach (var id in chainIds)
            if (byId.TryGetValue(id, out var folder)) chain.Add(folder);
        return chain;
    }

    /// <summary>父链上溯（含起点、由近及远），并剔除不在索引内的悬空 ID（数据修复期可能出现的孤儿父引用）。</summary>
    private static List<string> WalkExisting(string start, IReadOnlyDictionary<string, string?> parents)
        => Walk(start, parents).Where(parents.ContainsKey).ToList();

    /// <summary>
    /// 沿父链上溯（含起点、由近及远）。**真环检测**：节点重复出现即停止——数据出现环时返回环内链条，
    /// 既不无限循环，也不靠深度硬截断把坏数据静默掩盖（环在 <see cref="WouldCreateCycleAsync"/> 处显式拒绝）。
    /// </summary>
    private static List<string> Walk(string start, IReadOnlyDictionary<string, string?> parents)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = start;

        while (current != null && seen.Add(current))
        {
            chain.Add(current);
            current = parents.TryGetValue(current, out var parent) ? parent : null;
        }

        return chain;
    }

    private sealed record FolderNode(string FolderId, string? ParentId, string Name);
}