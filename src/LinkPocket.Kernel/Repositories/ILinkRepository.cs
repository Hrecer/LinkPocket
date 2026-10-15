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

    /// <summary>
    /// 批量删除**已读出的实体**（子树级联：调用方手里的那一批刚由查询取回，直接按实体登记删除）——
    /// 逐条 <see cref="RemoveAsync"/> 会为每一行发一条 SELECT（万行子树 = 万条点查 + 万次身份解析），
    /// 这是"删除一个大文件夹"实测卡顿的主因之一（见 WARNINGS）。
    /// 实体必须来自本库（键齐全）；同批重复 ID 无害（第二次起是空操作）。
    /// </summary>
    Task RemoveRangeAsync(IReadOnlyList<Link> links, CancellationToken ct);

    /// <summary>按 URL 找全部同址链接（Dedup 复用）。</summary>
    Task<IReadOnlyList<Link>> FindByUrlAsync(string url, CancellationToken ct);

    /// <summary>
    /// URL 出现多次的链接（去重扫描专用）：分组与计数**下推到 SQL**，只把重复项带回来。
    /// 原实现把全表拉进内存再分组 —— 10k 库上每次数据变更（防抖重跑）都要重付一遍全表扫描。
    /// </summary>
    Task<IReadOnlyList<Link>> ListDuplicatedAsync(CancellationToken ct);
}
