using LinkPocket.Data;

namespace LinkPocket.Kernel;

/// <summary>
/// 链接仓储契约：方法少而正交（Find/List/Count/Add/Update/Remove + 两个专用查询），
/// 一切复合读取在上层组合——避免"仓储方法爆炸"。实现于 LinkPocket.Data（EF），内部类型。
/// </summary>
public interface ILinkRepository
{
    Task<Link?> FindAsync(LinkId id, CancellationToken ct);

    /// <summary>按规格查询（SQL 下推；默认排序 = 名称升序 + ID 次序兜底）。</summary>
    Task<IReadOnlyList<Link>> ListAsync(LinkQuerySpec spec, CancellationToken ct);

    Task<int> CountAsync(LinkFilter filter, CancellationToken ct);

    /// <summary>按直接归属文件夹统计（不含递归；递归计数走 ITreeService）。</summary>
    Task<IReadOnlyDictionary<FolderId, int>> CountByFolderAsync(CancellationToken ct);

    Task<Link> AddAsync(Link link, CancellationToken ct);
    Task UpdateAsync(Link link, CancellationToken ct);
    Task RemoveAsync(LinkId id, CancellationToken ct);

    /// <summary>按 URL 找全部同址链接（Dedup 复用）。</summary>
    Task<IReadOnlyList<Link>> FindByUrlAsync(string url, CancellationToken ct);
}
