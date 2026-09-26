using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.Models;

namespace LinkPocket.ViewModels;

/// <summary>
/// BrowserViewModel · 分区：目录树重建（folders.overview 快照 → FolderTree）（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class BrowserViewModel
{
    /// <summary>由 folders.overview 的树快照重建左侧树（ParentId == null 即根级）。保留既有展开状态。
    /// rootLinkCount = 同快照的根级直挂链接数。
    /// 纯同步：无 IO/等待，签名用 void 不误导调用方。
    /// ParentId == FolderId 的自环坏数据排除（绝不把自己挂成自己的子节点）。</summary>
    /// <remarks>
    /// <b>链接叶子不在这一步注入</b>：它们改为"节点展开时按需加载"（<see cref="EnsureNodeLinksLoadedAsync"/>）。
    /// 每次刷新重建全库叶子（真实库 27 070 个节点）在十万级数据上是纯浪费——用户一眼只看得到根级，
    /// 而每重建一次都要付"造对象 + 万级集合通知"的钱。
    /// 展开状态照旧保留：重建后 <c>IsExpanded=true</c> 的节点会自己触发一次加载（见 <see cref="FolderNode.IsExpanded"/>）。
    /// </remarks>
    private void RebuildFolderTree(List<FolderDto> tree, int rootLinkCount)
    {
        var expandedIds = new HashSet<string?>();
        CollectExpandedIds(FolderTree, expandedIds);

        // 旧树里**已加载且内容未变**的叶子搬到新节点上（按归属目录），避免每次刷新重查同一目录的叶子：
        // 刷新很频繁（写操作后 300ms 防抖、导航、排序），而"叶子已经从引擎取过、且目录内容没再变过"
        // 这件事与刷新无关。叶子对象本身不带状态（选中/落点/改名都是宿主投影，重建后会整体重放），可以安全复用。
        // **内容变过的目录一律不搬**：叶子是取回那一刻的快照，搬运陈旧叶子就是让界面停在旧数据上
        //（实测：回溯把链接挪回去后，已展开节点下的叶子还是旧的）——版本不一致就丢弃搬运，
        // 交给"恢复展开态 → 懒加载"（已展开）或"下次展开"（未展开）自动重取。
        // ⚠️ 用列表而不是字典：虚根的 FolderId 是 null，而 `Dictionary` 的键**不允许 null**
        //    （曾因此让"刷新"整体抛异常：界面表现为"写操作后列表再也不更新"，日志里是 folder view refresh failed）。
        var carriedLeaves = new List<(string? FolderId, List<FolderNode> Leaves, DateTime Version)>();
        CollectLoadedLeaves(FolderTree, carriedLeaves);

        FolderTree.Clear();

        // ⚠️ 展开状态**不在这里设**：`IsExpanded=true` 会触发叶子懒加载，而加载在进程内可能**同步**完成
        //    （缓存命中时）——那会把叶子插到"文件夹子节点还没挂上"的位置，树里就出现"链接骑在文件夹之前"。
        //    统一挪到下面"文件夹子节点全部挂好之后"恢复。
        var root = new FolderNode { IsRoot = true, Name = BookmarkPath.RootToken, IconKind = "folder-open-outline", Host = this };
        var nodes = tree.ToDictionary(
            f => f.FolderId,
            f => new FolderNode
            {
                FolderId = f.FolderId,
                ParentId = f.ParentId,
                Name = f.Name,
                LinkCount = f.LinkCount,
                LeavesVersion = f.UpdatedAt,   // 本快照的目录内容版本（搬运判等依据，见上）
                Host = this,
            });

        foreach (var node in nodes.Values)
        {
            if (node.ParentId != null
                && node.ParentId != node.FolderId   // 自环坏数据 → 按根级兜底，避免自引用节点
                && nodes.TryGetValue(node.ParentId, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                root.Children.Add(node);
            }
        }

        // 搬运已加载的叶子（叶子恒排在文件夹子节点之后 = Windows 口径的构建顺序）
        foreach (var (folderId, leaves, version) in carriedLeaves)
        {
            if (folderId is null)
            {
                // 虚根（「全部书签」）没有 FolderDto 可依：用根级直挂链接数当版本——
                // 根级链接的增删/移入移出都会改数量（改名不入撤销栈，且本页改名有乐观改名兜住）。
                if (rootLinkCount != _lastRootLinkCount) continue;
                root.Children.AddRange(leaves);
                root.LinksLoaded = true;
                continue;
            }
            if (!nodes.TryGetValue(folderId, out var host) || host.LeavesVersion != version) continue;
            host.Children.AddRange(leaves);
            host.LinksLoaded = true;
        }
        _lastRootLinkCount = rootLinkCount;

        // 根节点计数 = 顶层文件夹递归计数之和 + 根级直挂链接数（内核递归计数）
        root.LinkCount = tree.Where(f => f.ParentId == null)
            .Sum(f => f.LinkCount) + rootLinkCount;

        FolderTree.Add(root);

        // 最后一步才恢复展开状态（虚根恒展开）——此时文件夹子节点与搬运来的叶子都已就位，
        // 新触发的懒加载一定追加在它们之后。
        root.IsExpanded = true;
        foreach (var (id, node) in nodes)
            if (expandedIds.Contains(id)) node.IsExpanded = true;
    }

    /// <summary>上次重建时的根级直挂链接数（虚根叶子的版本判据：根不是文件夹实体、
    /// 没有 UpdatedAt 可依——内核的父链 Touch 对根级是空操作，见 RebuildFolderTree）。</summary>
    private int _lastRootLinkCount = -1;

    /// <summary>收集旧树里**已加载叶子**的节点（键 = 归属目录 ID，虚根用 null；
    /// 版本 = 该批叶子取回时的目录内容版本），供重建时**按版本判等**决定是否搬运。</summary>
    private static void CollectLoadedLeaves(IEnumerable<FolderNode> nodes,
        List<(string? FolderId, List<FolderNode> Leaves, DateTime Version)> into)
    {
        foreach (var node in nodes)
        {
            if (node.IsLink) continue;
            if (node.LinksLoaded)
            {
                var leaves = node.Children.Where(c => c.IsLink).ToList();
                if (leaves.Count > 0) into.Add((node.FolderId, leaves, node.LeavesVersion));
            }
            CollectLoadedLeaves(node.Children, into);
        }
    }

    // —— 树叶子懒加载（展开时才取该目录的直接链接） ——

    /// <summary>树节点展开 → 按需加载其直接链接叶子（由 <see cref="FolderNode.IsExpanded"/> 触发）。</summary>
    internal void OnTreeNodeExpanded(FolderNode node) => _ = EnsureNodeLinksLoadedAsync(node);

    /// <summary>
    /// 等待某节点的叶子加载收尾（懒加载是异步的）。界面不需要它——界面靠数据绑定自己刷新；
    /// 它是给**测试与自动化**钉时序用的（与 <see cref="WaitForIdleAsync"/> 同一性质）。
    /// </summary>
    public async Task WaitForTreeLinksAsync(FolderNode node, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (node.LinksLoading && Environment.TickCount64 < deadline)
            await Task.Delay(5);
    }

    /// <summary>
    /// 确保某节点的直接链接叶子已注入（幂等；失败可重试）。
    /// 只取**名称/地址**投影（树叶子只画名字）：完整 `LinkDto` 会把每条的图标地址（真实库里是
    /// 500 字符的内嵌 data URI）、描述、时间戳一起搬过来，展开一个 15 000 条的文件夹就是十几 MB。
    /// </summary>
    private async Task EnsureNodeLinksLoadedAsync(FolderNode node)
    {
        if (node.IsLink || node.LinksLoaded || node.LinksLoading) return;
        node.LinksLoading = true;   // 进行中标记：同一次展开可能连来多个触发（chevron + 恢复展开态）

        try
        {
            // 轻量出口命令：只带回 id / 标题 / 地址（树叶子只画名称）
            var links = await _client.QueryAsync<List<TreeLinkDto>>("folders.tree_links",
                new { folder_id = node.FolderId });

            // 排序口径与树一致（名称升序，中文排序），并挂在文件夹子节点**之后**（Windows 口径）
            var leaves = (links ?? new List<TreeLinkDto>())
                .OrderBy(l => string.IsNullOrWhiteSpace(l.Title) ? (l.Url ?? "") : l.Title, NameOrder.Comparer)
                .Select(l => new FolderNode
                {
                    IsLink = true,
                    Id = l.LinkId,
                    ParentId = node.FolderId,
                    Name = string.IsNullOrWhiteSpace(l.Title) ? (l.Url ?? "") : l.Title,
                    Host = this,
                    LinksLoaded = true,                       // 叶子本身没有"下级叶子"
                    IsSelected = Selection.Contains(l.LinkId)     // 只投影自身（不遍历整表：1 万行 × 每次展开）
                })
                .ToList();

            // 叶子恒排在文件夹子节点之后：先摘掉既有叶子再追加（幂等，防止重排时出现"链接在文件夹之前"）
            foreach (var stale in node.Children.Where(c => c.IsLink).ToList())
                node.Children.Remove(stale);
            node.Children.AddRange(leaves);
            node.LinksLoaded = true;
        }
        catch (Exception ex)
        {
            LpLog.Error("tree leaf lazy-load failed", ex);   // 失败不静默：下次展开会重试（LinksLoaded 仍为 false）
        }
        finally
        {
            node.LinksLoading = false;
        }
    }

    private static void CollectExpandedIds(IEnumerable<FolderNode> nodes, HashSet<string?> ids)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded && node.FolderId != null) ids.Add(node.FolderId);
            CollectExpandedIds(node.Children, ids);
        }
    }

    // —— 右键菜单 / 拖拽移动（参考 Files、Alist 的文件管理范式）——

    /// <summary>目标文件夹是否为 folderId 自身或其后代（用于阻止把文件夹移进自己）。</summary>
    public bool IsSelfOrDescendant(string folderId, string? targetId)
    {
        var current = targetId;
        var visited = new HashSet<string>();   // 环保护：坏数据（父链成环）时终止而非死循环
        while (current != null && visited.Add(current))
        {
            if (current == folderId) return true;
            current = _folderMap.TryGetValue(current, out var info) ? info.ParentId : null;
        }
        return false;
    }

}