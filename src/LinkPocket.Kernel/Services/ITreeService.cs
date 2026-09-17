using LinkPocket.Data;

namespace LinkPocket.Kernel;

/// <summary>
/// 树算法唯一出处（方案 4.1）：消灭各处重复的父链遍历。
/// 实现于 Data（经 EF 查询），模块经 UoW 所在组合获得。
/// </summary>
public interface ITreeService
{
    /// <summary>全库递归链接计数（文件夹树列计数；配合缓存/事件失效由上层管理）。</summary>
    Task<IReadOnlyDictionary<FolderId, int>> RecursiveLinkCountsAsync(CancellationToken ct);

    /// <summary>祖先链（includeSelf = true 时含自身，自底向上）。</summary>
    Task<IReadOnlyList<FolderId>> AncestorsAsync(FolderId id, bool includeSelf, CancellationToken ct);

    /// <summary>把 id 移到 targetParent 下是否会产生环（即 targetParent 位于 id 子树内或等于 id）。</summary>
    Task<bool> WouldCreateCycleAsync(FolderId id, FolderId targetParent, CancellationToken ct);

    /// <summary>路径显示：「全部书签 / A / B」；null = 根 =「全部书签」。</summary>
    Task<string> PathDisplayAsync(FolderId? id, CancellationToken ct);
}
