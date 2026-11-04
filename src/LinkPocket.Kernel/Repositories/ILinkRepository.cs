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

    /// <summary>分页**直出 DTO**（服务端投影，与 ToDto() 同口径）：目录页每帧 2000 行的取数形态——
    /// 不物化实体、不再逐条映射（100K 实测省 ~35ms/页）。</summary>
    Task<IReadOnlyList<LinkPocket.Contracts.LinkDto>> ListDtosAsync(LinkQuerySpec spec, CancellationToken ct);

    /// <summary>库内引用的全部 favicon 地址（去重、非空）：图标缓存清扫（favicon.cache_gc）的引用集。</summary>
    Task<IReadOnlyList<string>> ListFaviconUrlsAsync(CancellationToken ct);

    Task<int> CountAsync(LinkFilter filter, CancellationToken ct);

    /// <summary>
    /// 搜索快照首屏：一次取到命中总数与前 N 行（仅 title 排序）。
    /// 纯文本范围先取首页（不满一页 = 已拿全，稀疏命中只扫一遍），满页再补 count；
    /// path 范围沿有序索引一次扫描同时计数 + 收首页 ID（避免对稀疏子树做两遍全扫）。
    /// 两段结果在同一读事务内取得，供会话快照使用。
    /// </summary>
    Task<(int Total, IReadOnlyList<LinkPocket.Contracts.LinkDto> FirstPage)> CountAndFirstPageAsync(
        LinkSearchScope scope, string sortBy, string sortOrder, int pageSize, CancellationToken ct);

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

    /// <summary>集合中存在的主表链接 ID（`id in (...)` 分块直读）：批量还原/撤销的冲突预检专用。</summary>
    Task<IReadOnlyList<string>> ListExistingIdsAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>集合中已存在的 (归属目录, URL) 对（`url in (...)` 分块直读）：批量还原的重复检查专用。</summary>
    Task<IReadOnlyList<(string? ListId, string Url)>> ListLandingUrlPairsAsync(
        IReadOnlyList<string> urls, CancellationToken ct);

    /// <summary>按 URL 找全部同址链接（Dedup 复用）。</summary>
    Task<IReadOnlyList<Link>> FindByUrlAsync(string url, CancellationToken ct);

    /// <summary>
    /// URL 出现多次的链接（去重扫描专用）：重复 URL 由 SQL 判定，链接**直出 DTO**（原生 reader，
    /// 不物化实体再逐条 ToDto）。返回的是**全量重复链接**（外部命令与界面看到同一份结果）；
    /// 顺序不保证，调用方按 URL/CreatedAt/LinkId 自行分组排序。
    /// </summary>
    Task<IReadOnlyList<LinkPocket.Contracts.LinkDto>> ListDuplicatedDtosAsync(CancellationToken ct);
}
