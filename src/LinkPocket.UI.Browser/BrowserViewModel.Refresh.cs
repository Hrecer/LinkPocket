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
using LinkPocket.Models;

namespace LinkPocket.ViewModels;

/// <summary>
/// BrowserViewModel · 分区：刷新链与粘贴置尾（ApplySort/LoadAsync/RefreshAsync/置尾与待聚焦/RefreshPreservingSelection）（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class BrowserViewModel
{
    /// <summary>
    /// 应用排序（字段与方向已由共享表控件切换完毕）并重排。
    /// 走 RefreshPreservingSelectionAsync：重排不丢选中（Windows 点列头也不丢）。
    /// 点列头 = 真刷新：刚置入项的临时置尾同时归位（Windows 口径）。
    /// </summary>
    public void ApplySort(string? field, bool ascending)
    {
        if (string.IsNullOrEmpty(field)) return;
        SortBy = field;
        SortOrder = ascending ? "asc" : "desc";
        ClearRecentlyPinned();
        _ = RefreshPreservingSelectionAsync();
    }

    /// <summary>进入指定目录（null = 根）。首次显示页面时调用 LoadAsync(null)。
    /// 这是"用户发起的导航"（树行点击/面包屑/后退前进/返回上级/F5）→ 带加载遮罩（真刷新，
    /// 在 <see cref="RefreshAsync"/> 里归位置尾）；导航即作废未消费的粘贴定位请求。</summary>
    public async Task LoadAsync(string? folderId)
    {
        _pendingFocusId = null;
        Controller.NavigateTo(folderId);
        CurrentFolderId = Controller.CurrentFolderId;
        await RefreshAsync(navigating: true);
    }

    /// <summary>
    /// 重新加载当前目录（事件推送订阅 / 导航显式调用；写操作不自行刷新，见 WARNINGS #18）。
    /// 选中的唯一事实来源是 <see cref="Selection"/>（行与树均为投影），故此方法本身不恢复选中——
    /// 集合并未因刷新而消失。仅当 <paramref name="clearSelection"/> 为 true（导航切换目录）时清空选中。
    /// 重入守卫 = 「最后请求必被处理」：加载进行中又来新请求（导航切换 / 防抖事件刷新）只置挂起标志，
    /// 当前加载收尾后自动补刷一次——绝不静默吞掉请求（曾导致：导航后列表停在旧目录）。
    /// </summary>
    public async Task RefreshAsync(bool clearSelection = false, bool navigating = false)
    {
        // 导航切换目录：清空选中集合（行/树投影一起归零）；原地刷新则保留
        if (clearSelection)
            Selection.Clear();
        if (IsLoading)
        {
            _refreshPending = true;
            _clearSelectionOnPendingRefresh |= clearSelection;
            _navigatingOnPendingRefresh |= navigating;
            return;
        }
        // 数据持续高频变动时，补刷递归不能无限延续（见 finally 内的深度计数）
        if (_refreshRecursionDepth >= MaxRefreshRecursion)
        {
            _refreshPending = false;   // 弃掉挂起：交还 300ms 事件防抖继续追平（不丢数据，只是晚一拍）
            return;
        }
        // 遮罩只在"这次加载真的开始了"且属于**用户发起的导航/刷新**时亮：被挂起/被丢弃的请求不亮，
        // 挂起补刷按 _navigatingOnPendingRefresh 逐轮继承（事件驱动的后台刷新一律静默，不闪动画）。
        // 真刷新（导航加载 = 进入目录 / 点当前位置重载 / F5）同时让"刚置入项临时置尾"归位（Windows 口径）。
        if (navigating) { IsNavigating = true; _navigatingInChain = true; ClearRecentlyPinned(); }
        IsLoading = true;
        try
        {
            // 单快照：folders.overview 一次返回 目录页+全量树+根级计数，
            // 三个数据源在引擎同一读池 UoW 内（不再跨命令漂移；原三连查 FolderContents/Tree/Stats 已收敛为一条）。
            var contents = await _client.FoldersOverviewAsync(Controller.CurrentFolderId, sortBy: SortBy, sortOrder: SortOrder);

            // 文件夹映射：面包屑 + 返回上级需要父链；同时重建左侧文件夹树
            //（与目录页同快照的树/计数 + 全量链接叶子：每文件夹直接链接一并注入，Windows 资源管理器语义）
            var tree = contents.Tree ?? new List<FolderDto>();
            _folderMap = tree.ToDictionary(f => f.FolderId, f => (f.ParentId, f.Name));
            RebuildFolderTree(tree, contents.RootLinkCount ?? 0, contents.TreeLinks ?? new List<LinkDto>());
            // 树已重建：选中态由 Selection（唯一事实）派生重放，无需容器时序

            Rows.Clear();
            SetContextRow(null); // 行对象已重建：右键命中行引用作废（删除文案随之复位）

            // Windows 逻辑：升序时文件夹在前，降序时文件夹在后（任何排序维度都如此）
            var folderRows = new List<BrowserRowViewModel>();
            foreach (var folder in contents.SubFolders)
            {
                folderRows.Add(new BrowserRowViewModel(folder.FolderId, isFolder: true, folder.Name)
                {
                    LinkCount = folder.LinkCount,
                    ModifiedAt = folder.UpdatedAt, // 内核维护：文件夹内容（含子孙）最后变动时间
                    CreatedAt = folder.CreatedAt,  // 内核维护：文件夹创建时间
                    LastViewedAt = folder.LastVisitedAt, // 内核维护：子孙链接被查看时沿父链刷新
                    ViewCount = folder.VisitCount,       // 内核维护：子孙链接被查看时沿父链 +1
                    Host = this,
                    IsCut = _clipboardCtl.IsCutInClipboard(folder.FolderId, true)
                });
            }

            var linkRows = new List<BrowserRowViewModel>();
            foreach (var link in contents.Links)
            {
                linkRows.Add(new BrowserRowViewModel(link.LinkId, isFolder: false, link.Title)
                {
                    Url = link.Url,
                    ModifiedAt = link.UpdatedAt,        // 内核维护：内容变动时间（查看不影响）
                    CreatedAt = link.CreatedAt,          // 内核维护：链接创建时间
                    LastViewedAt = link.LastVisitedAt,   // 内核维护：链接最后查看时间
                    ViewCount = link.VisitCount,         // 内核维护：链接查看次数
                    Favicon = Services.FaviconService.LoadFromCache(link.FaviconUrl),
                    Host = this,
                    IsCut = _clipboardCtl.IsCutInClipboard(link.LinkId, false)
                });
            }

            // favicon 懒加载清单：磁盘缓存未命中时后台拉取，完成后补到对应行
            var missing = linkRows
                .Where(r => r.Favicon == null)
                .Select(r => contents.Links.First(l => l.LinkId == r.Id).FaviconUrl)
                .Where(url => !string.IsNullOrEmpty(url))
                .Distinct()
                .ToList();

            // 组装顺序：升序 = 文件夹 → 链接；降序 = 链接 → 文件夹（Windows 逻辑）。
            // ⚠️ 行必须先同步就位（favicon 属附属数据，网络预取绝不阻塞行渲染——
            //    曾因「await 预取再建行」在网络慢时把 Rows 长时间留在上一目录，跳转定位读到旧行集 → RowMissing 间歇回归）。
            var ordered = SortOrder == "desc"
                ? linkRows.Concat(folderRows).ToList()
                : folderRows.Concat(linkRows).ToList();

            // 临时置尾（Windows）：刚粘贴的项追加到列表末尾（不参与排序），直到真刷新才按排序归位。
            // 只对"属于当前目录且此刻仍在数据里"的 ID 生效——已被移走/删除的置尾项自动跳过。
            var pinned = ActivePinnedIds();
            if (pinned.Count == 0)
            {
                foreach (var row in ordered) Rows.Add(row);
            }
            else
            {
                var pinnedSet = new HashSet<string>(pinned, StringComparer.Ordinal);
                var byId = ordered.ToDictionary(r => r.Id, StringComparer.Ordinal);
                foreach (var row in ordered)
                    if (!pinnedSet.Contains(row.Id)) Rows.Add(row);
                foreach (var id in pinned)
                    if (byId.TryGetValue(id, out var row)) Rows.Add(row);
            }

            // favicon 后台预取 + Dispatcher 回填：行已可见，失败只丢图标（下次事件刷新追平）
            if (missing.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try { await Task.WhenAll(missing.Select(Services.FaviconService.PrefetchAndCacheAsync)); }
                    catch { /* 网络失败属预期波动，行保持无图标 */ }
                    foreach (var row in linkRows.Where(r => r.Favicon == null))
                    {
                        var dto = contents.Links.FirstOrDefault(l => l.LinkId == row.Id);
                        if (dto != null)
                        {
                            var img = Services.FaviconService.LoadFromCache(dto.FaviconUrl);
                            if (img != null)
                                System.Windows.Application.Current?.Dispatcher.Invoke(() => row.SetFavicon(img));
                        }
                    }
                });
            }

            // 选中的唯一事实来源是 Selection：Rows 已重建且行是投影，这里只需把集合同步到
            // 主栏行 + 树 + 派生状态（数量/详情/命令）。不改变 Selection 本身。
            ApplySelectionToView();
            // 重命名态同样是投影：刷新重建行/树后按会话状态重放（编辑框在重建出的行/节点上重新出现并自动聚焦）
            ApplyRenameToView();

            // 粘贴完成后的定位：新行已在本轮重建中就位 → 滚入视口（行不在本轮数据里则留待下次刷新）
            ConsumePendingFocus();

            // 面包屑（含 ID，可点击跳转；最后一级为当前目录，高亮显示）
            Breadcrumbs.Clear();
            var chain = BuildBreadcrumbIds(Controller.CurrentFolderId).ToList();
            Breadcrumbs.Add(new BrowserCrumbViewModel(null, "全部书签") { IsLast = chain.Count == 0 });
            for (int i = 0; i < chain.Count; i++)
            {
                Breadcrumbs.Add(new BrowserCrumbViewModel(chain[i].Id, chain[i].Name)
                {
                    IsLast = i == chain.Count - 1
                });
            }

            StatusText = $"共 {contents.SubFolders.Count + contents.Links.Count} 项" +
                         $"（{contents.SubFolders.Count} 个文件夹 / {contents.Links.Count} 个链接）";
        }
        catch (Exception ex)
        {
            StatusText = "加载失败";
            Services.Logger.Error("浏览目录刷新失败", ex);   // 失败必须留痕，不能只有一行状态文案
        }
        finally
        {
            IsLoading = false;
            CommandManager.InvalidateRequerySuggested();
            // 撤销/重做可用性轻量同步（Ctrl+Z/Y 的 CanExecute 要准）：每次刷新链收尾取一次 undo 栈态。
            // 只读查询、不产生事件 → 不会引发刷新循环；失败静默保持保守禁用（见 RefreshUndoStateAsync）。
            _ = RefreshUndoStateAsync();

            // 加载期间有新的刷新请求（导航/防抖事件）→ 立即补刷一次，保证最后请求被处理
            if (_refreshPending)
            {
                _refreshPending = false;
                var clear = _clearSelectionOnPendingRefresh;
                _clearSelectionOnPendingRefresh = false;
                var nav = _navigatingOnPendingRefresh;
                _navigatingOnPendingRefresh = false;
                _refreshRecursionDepth++;
                try { await RefreshAsync(clearSelection: clear, navigating: nav); }
                finally { _refreshRecursionDepth--; }
            }
            else
            {
                IsNavigating = false;   // 本轮（含挂起补刷链）全部结束 → 收加载遮罩
                var wasNavigation = _navigatingInChain;
                _navigatingInChain = false;
                RefreshCompleted?.Invoke(this, wasNavigation);   // 链结束只发一次（行入场动画据此判定）
            }
        }
    }

    /// <summary>置尾 ID（按置入顺序，后一批在后）；仅对 <see cref="_pinnedFolderId"/> 目录生效。</summary>
    private readonly List<string> _recentlyPinned = new();

    /// <summary>置尾所属目录（null = 根目录）；与当前目录不一致时置尾自动失效并清空。</summary>
    private string? _pinnedFolderId;

    /// <summary>粘贴完成后的定位目标（滚入视口）；行重建（事件刷新）后被消费一次。</summary>
    private string? _pendingFocusId;

    /// <summary>记录刚置入的项（粘贴完成时调用）：同 ID 先移除再追加（后到者排更后）。</summary>
    private void MarkRecentlyPinned(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return;
        _pinnedFolderId = Controller.CurrentFolderId;
        foreach (var id in list) _recentlyPinned.Remove(id);
        _recentlyPinned.AddRange(list);
    }

    /// <summary>清空置尾（真刷新：导航加载 / 点列头排序 / F5）。</summary>
    private void ClearRecentlyPinned()
    {
        _recentlyPinned.Clear();
        _pinnedFolderId = null;
    }

    /// <summary>当前生效的置尾 ID（目录不匹配即失效清空——换目录后置尾无意义）。</summary>
    private IReadOnlyList<string> ActivePinnedIds()
    {
        if (_recentlyPinned.Count == 0) return _recentlyPinned;
        if (_pinnedFolderId != Controller.CurrentFolderId) ClearRecentlyPinned();
        return _recentlyPinned;
    }

    /// <summary>消费粘贴定位请求：把目标行滚入视口（行不在本轮数据里则保持待命，下轮再试）。</summary>
    private void ConsumePendingFocus()
    {
        if (_pendingFocusId == null) return;
        var row = Rows.FirstOrDefault(r => r.Id == _pendingFocusId);
        if (row == null) return;
        _pendingFocusId = null;
        FocusRowRequested?.Invoke(this, row);
    }

    // —— Esc 分层（Windows 口径）——

    /// <summary>
    /// 就地刷新并保留当前选中。用于两类收尾：
    /// ① 非导航类操作（重命名 / 新建 / 移动 / 粘贴 / 排序 / 从树里删节点）——它们不改变所在目录；
    /// ② 后端数据变更事件驱动的刷新（<c>MainViewModel.OnBackendRefresh</c>，经 UiEventHub 防抖）。
    /// 选中的唯一事实来源是 <see cref="Selection"/>，行/树均为投影，刷新并不抹掉集合，
    /// 故此处只需不带清空标志地刷新（这是保留选中的关键——无需任何"重新选中"步骤）。
    /// 只有"切换目录"才用 <see cref="LoadAsync"/>（它带清空标志）。
    /// </summary>
    public Task RefreshPreservingSelectionAsync()
        => RefreshAsync(clearSelection: false);

}