using LinkPocket.Data;

namespace LinkPocket.Kernel;

/// <summary>
/// 文件夹链接计数的两种口径（一次查询同时产出，杜绝"同一概念两个口径"）：
/// <see cref="Direct"/> = 该文件夹的**直接**子链接数；<see cref="Recursive"/> = 含全部子孙文件夹的链接总数。
/// </summary>
public sealed record FolderLinkCounts(
    IReadOnlyDictionary<FolderId, int> Direct,
    IReadOnlyDictionary<FolderId, int> Recursive);

/// <summary>
/// 树算法唯一出处：消灭各处重复的父链遍历。
/// 实现于 Data（经 EF 查询），模块经 UoW 所在组合获得。
/// </summary>
public interface ITreeService
{
    /// <summary>全库链接计数两口径（文件夹树列计数；配合缓存/事件失效由上层管理）。</summary>
    Task<FolderLinkCounts> LinkCountsAsync(CancellationToken ct);

    /// <summary>祖先链（includeSelf = true 时含自身，自底向上）。</summary>
    Task<IReadOnlyList<FolderId>> AncestorsAsync(FolderId id, bool includeSelf, CancellationToken ct);

    /// <summary>把 id 移到 targetParent 下是否会产生环（即 targetParent 位于 id 子树内或等于 id）。</summary>
    Task<bool> WouldCreateCycleAsync(FolderId id, FolderId targetParent, CancellationToken ct);

    /// <summary>路径显示：「全部书签 / A / B」；null = 根 =「全部书签」。</summary>
    Task<string> PathDisplayAsync(FolderId? id, CancellationToken ct);

    /// <summary>
    /// 事件：内容变动。把 id 及其全部祖先的 UpdatedAt 置为当前时间（事件驱动增量口径的唯一写入点）。
    /// id = null（根）时为 no-op。变更由引擎在提交时落库——本方法只登记，不保存。
    /// </summary>
    Task TouchModifiedAsync(FolderId? id, CancellationToken ct);

    /// <summary>
    /// 事件：子孙链接被查看。把 id 及其全部祖先的 LastVisitedAt 刷新为当前时间、VisitCount 各 +1。
    /// 与 <see cref="TouchModifiedAsync"/> 同一条父链、同一套事件驱动增量口径，区别只在写入字段。
    /// id = null（根）时为 no-op；本方法只登记，不保存。
    /// </summary>
    Task RecordFolderViewAsync(FolderId? id, CancellationToken ct);
}
