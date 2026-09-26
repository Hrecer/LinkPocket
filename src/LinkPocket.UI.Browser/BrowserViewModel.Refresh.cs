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
/// BrowserViewModel · 分区：刷新链与粘贴置尾（ApplySort/LoadAsync/RefreshAsync/置尾与待聚焦/RefreshPreservingSelection）（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class BrowserViewModel
{
    /// <summary>刷新阶段耗时的日志分类（与 MainViewModel / MainWindow 同一支：现场日志按它取）。</summary>
    private const string NavLogCategory = "app.nav";

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
    /// 等待当前刷新链（**含"挂起补刷"**）彻底落地；无在途刷新时立即完成。
    ///
    /// <para>为什么需要它：树键盘导航（<c>MoveTreeSelection</c>）与部分入口是 **fire-and-forget**
    /// （<c>_ = SelectTreeNodeAsync(...)</c>）——"选中"同步落地，但"进入目录"要等加载链收尾
    /// （若此时已有在途加载，本次请求按 <see cref="RefreshAsync"/> 的"最后请求必被处理"语义**挂起**，
    /// 由前一条链收尾时补刷）。因此"命令之后立刻断言已进入某目录"存在时序不确定性
    /// （单测曾偶发失败；渲染检查/自动化用它把时序钉死）。</para>
    ///
    /// <para>界面交互路径**不需要**它：界面靠事件 + 300ms 防抖驱动刷新，不依赖"何时落地"。</para>
    /// </summary>
    public async Task WaitForIdleAsync()
    {
        while (IsLoading || _refreshPending)
            await Task.Delay(5);
    }

    /// <summary>
    /// 重新加载当前目录（事件推送订阅 / 导航显式调用；写操作不自行刷新，见 WARNINGS #18）。
    /// 选中的唯一事实来源是 <see cref="Selection"/>（行与树均为投影），故此方法本身不恢复选中——
    /// 集合并未因刷新而消失。
    ///
    /// <para>
    /// <b>导航（进入目录）不清空选中</b>：树里"选中该文件夹 + 进入"是同一件事的两面（高亮由 Selection 派生），
    /// 清空会把刚点中的文件夹高亮一起抹掉；链接同理——选中集合是独立事实，切走再切回来按 ID 重新投影出现。
    /// <paramref name="clearSelection"/> 留给确实要清空的调用方（如定位/重置类路径），导航路径不传。
    /// </para>
    ///
    /// 重入守卫 = 「最后请求必被处理」：加载进行中又来新请求（导航切换 / 防抖事件刷新）只置挂起标志，
    /// 当前加载收尾后自动补刷一次——绝不静默吞掉请求（曾导致：导航后列表停在旧目录）。
    /// </summary>
    public async Task RefreshAsync(bool clearSelection = false, bool navigating = false)
    {
        // 清空选中（仅显式请求时）：行/树投影一起归零；原地刷新与导航都保留
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
        // 遮罩只在"该次加载真的开始了"且属于**用户发起的导航/刷新**时亮：被挂起/被丢弃的请求不亮，
        // 挂起补刷按 _navigatingOnPendingRefresh 逐轮继承（事件驱动的后台刷新一律静默，不闪动画）。
        // 真刷新（导航加载 = 进入目录 / 点当前位置重载 / F5）同时让"刚置入项临时置尾"归位（Windows 口径）。
        if (navigating) { IsNavigating = true; _navigatingInChain = true; ClearRecentlyPinned(); }
        IsLoading = true;
        // 阶段耗时留痕（cat=app.nav）：切页/防抖刷新"到底贵在哪一段"的取证入口。
        // 只读 Stopwatch，不改变任何时序；marks = 各阶段结束时的累计毫秒（日志里换算成每段增量）。
        var navWatch = Stopwatch.StartNew();
        var navMarks = new long[5];
        try
        {
            // 单快照：folders.overview 一次返回 目录页+全量树+根级计数，
            // 三个数据源在引擎同一读池 UoW 内（不再跨命令漂移；原三连查 FolderContents/Tree/Stats 已收敛为一条）。
            // **首页分页**（2026-09-26）：只取前 PageChunkSize 条链接（SQL LIMIT），滚动接近底部自动续载
            // （RequestNextPageAsync）。10 000 条全量 = 引擎 432ms + 万行 VM 115ms，冷开 680ms 里的大头；
            // 十万级下"一次全量"根本不可行。文件夹不参与分页（引擎整页返回）。
            var contents = await _client.FoldersOverviewAsync(Controller.CurrentFolderId, sortBy: SortBy, sortOrder: SortOrder,
                perPage: PageChunkSize);
            _loadedPage = contents.CurrentPage;
            _lastPage = contents.PerPage > 0 ? Math.Max(contents.LastPage, 1) : 1;
            _directLinkTotal = contents.DirectLinkCount;
            navMarks[0] = navWatch.ElapsedMilliseconds;   // 引擎读（folders.overview 单快照）

            // 文件夹映射：面包屑 + 返回上级需要父链；同时重建左侧文件夹树（只建文件夹节点——
            // 链接叶子改为"节点展开时按需加载"，见 BrowserViewModel.Tree.cs 的懒加载注释）
            var tree = contents.Tree ?? new List<FolderDto>();
            _folderMap = tree.ToDictionary(f => f.FolderId, f => (f.ParentId, f.Name));
            RebuildFolderTree(tree, contents.RootLinkCount ?? 0);
            navMarks[1] = navWatch.ElapsedMilliseconds;   // 目录树重建
            // 树已重建：选中态由 Selection（唯一事实）派生重放，无需容器时序

            // ⚠️ 这里**不**先清空 Rows：行集要不要换，等目标行序算完再做等价判定（见下方"内容一致 → 不动集合"）。
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
            // Id → 图标地址：行序与来源列表同长，逐个 First(...) 找回来是 O(行 × 链接)
            //（10k 库冷缓存时是千万级字符串比较，且发生在 UI 线程）
            var faviconUrlById = new Dictionary<string, string?>(contents.Links.Count, StringComparer.Ordinal);
            foreach (var link in contents.Links)
            {
                faviconUrlById[link.LinkId] = link.FaviconUrl;
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

            // 组装顺序：升序 = 文件夹 → 链接；降序 = 链接 → 文件夹（Windows 逻辑）。
            // ⚠️ 行必须先同步就位（favicon 属附属数据，网络预取绝不阻塞行渲染——
            //    曾因「await 预取再建行」在网络慢时把 Rows 长时间留在上一目录，跳转定位读到旧行集 → RowMissing 间歇回归）。
            var ordered = SortOrder == "desc"
                ? linkRows.Concat(folderRows).ToList()
                : folderRows.Concat(linkRows).ToList();

            // 临时置尾（Windows）：刚粘贴的项追加到列表末尾（不参与排序），直到真刷新才按排序归位。
            // 只对"属于当前目录且此刻仍在数据里"的 ID 生效——已被移走/删除的置尾项自动跳过。
            // 目标行序（含置尾）先算好，再决定**要不要换集合**：
            // 内容完全一致（含顺序）时保持原集合不动——逐条 Clear/Add 会触发 N 次 CollectionChanged，
            // 视图（模板模式）随之整表重建/重排，是"切页偶发卡顿"的主要来源。
            var pinned = ActivePinnedIds();
            var nextRows = new List<BrowserRowViewModel>(ordered.Count);
            if (pinned.Count == 0)
            {
                nextRows.AddRange(ordered);
            }
            else
            {
                var pinnedSet = new HashSet<string>(pinned, StringComparer.Ordinal);
                var byId = ordered.ToDictionary(r => r.Id, StringComparer.Ordinal);
                foreach (var row in ordered)
                    if (!pinnedSet.Contains(row.Id)) nextRows.Add(row);
                foreach (var id in pinned)
                    if (byId.TryGetValue(id, out var row)) nextRows.Add(row);
            }

            // **差分刷新**：行序列（Id + 顺序 + 类型）没变时**不换集合**，只把变了的展示字段原地写回
            // （每处各发一次属性通知）—— 一次 Reset 都没有 ⇒ 视口内的行容器（连同右键菜单、布局行为、
            // 列宽投影）原地保留，只重画真变了的那几格。实测：大目录里"数据刚被批脚本改过再打开/刷新"
            // 这一下从 64ms 布局降到个位数。行的增减 / 重排 / 换目录仍走下面的整表替换
            // （那时容器与数据项的对应关系已经变了，必须重建）。
            if (BrowserRowViewModel.SameIdentity(Rows, nextRows))
            {
                for (var i = 0; i < nextRows.Count; i++) Rows[i].ApplyFrom(nextRows[i]);
            }
            else if (!BrowserRowViewModel.SameSequence(Rows, nextRows))
            {
                // 整体替换 = **一次** Reset 通知（逐条 Add 在 10 001 行时会发 10 001 次，见 BulkObservableCollection）
                _rowsReplaced = true;   // 新行对象：选中/改名投影不必逐行通知（绑定首次求值即读宿主状态）
                Rows.ReplaceAll(nextRows);
                SetContextRow(null);   // 行对象已重建：右键命中行引用作废（删除文案随之复位）
            }

            // favicon 后台预取 + 回填（与续载共用一条链路，见 StartFaviconPrefetch）
            StartFaviconPrefetch(linkRows, faviconUrlById);
            navMarks[2] = navWatch.ElapsedMilliseconds;   // 建行（含 favicon 本地解码）

            // 选中的唯一事实来源是 Selection：Rows 已重建且行是投影，这里只需把集合同步到
            // 主栏行 + 树 + 派生状态（数量/详情/命令）。不改变 Selection 本身。
            ApplySelectionToView();
            // 重命名态同样是投影：刷新重建行/树后按会话状态重放（编辑框在重建出的行/节点上重新出现并自动聚焦）
            ApplyRenameToView();

            // 粘贴完成后的定位：新行已在重建中就位 → 滚入视口（行不在当前数据里则留待下次刷新）
            ConsumePendingFocus();
            navMarks[3] = navWatch.ElapsedMilliseconds;   // 侧栏/选中/改名投影 + 定位

            // 面包屑（含 ID，可点击跳转；最后一级为当前目录，高亮显示）
            Breadcrumbs.Clear();
            var chain = BuildBreadcrumbIds(Controller.CurrentFolderId).ToList();
            Breadcrumbs.Add(new BrowserCrumbViewModel(null, BookmarkPath.RootToken) { IsLast = chain.Count == 0 });
            for (int i = 0; i < chain.Count; i++)
            {
                Breadcrumbs.Add(new BrowserCrumbViewModel(chain[i].Id, chain[i].Name)
                {
                    IsLast = i == chain.Count - 1
                });
            }

            navMarks[4] = navWatch.ElapsedMilliseconds;   // 面包屑

            // 分页态如实说出来（§2.5 教训：截断不许静默）——"共 N 项"按**引擎总数**报，不是已加载数
            if (HasMorePages)
            {
                StatusText = Loc.K("browser.status.totalPaged",
                    contents.SubFolders.Count + _directLinkTotal, contents.SubFolders.Count, _directLinkTotal,
                    contents.SubFolders.Count + TotalLoadedLinks);
            }
            else
            {
                StatusText = Loc.K("browser.status.totalWithBreakdown",
                    contents.SubFolders.Count + _directLinkTotal, contents.SubFolders.Count, _directLinkTotal);
            }
        }
        catch (Exception ex)
        {
            StatusText = Loc.K("status.loadFailed");
            LpLog.Error("folder view refresh failed", ex);   // 失败必须留痕，不能只有一行状态文案
        }
        finally
        {
            // 阶段耗时留痕（cat=app.nav）：成功与失败都记（失败时后面的阶段为 0）。
            // 放在 finally 的**最前**：后面的"挂起补刷"递归各自记自己那一行，不混进本次。
            LpLog.Write(LogLevel.Info, NavLogCategory,
                $"refresh:folder={Controller.CurrentFolderId ?? "root"} nav={navigating} rows={Rows.Count}"
                + $" overview={navMarks[0]}ms tree={navMarks[1] - navMarks[0]}ms"
                + $" rows-b={navMarks[2] - navMarks[1]}ms side={navMarks[3] - navMarks[2]}ms"
                + $" crumbs={navMarks[4] - navMarks[3]}ms",
                elapsedMs: navWatch.ElapsedMilliseconds);
            IsLoading = false;
            // 观测定痕（cat=app.nav）：`CommandRefresh.Request` 内是**全局** CommandManager 重查
            //（InvalidateRequerySuggested ⇒ 遍历可视树里的 CommandBinding），每次刷新还在 finally 与
            // RefreshUndoStateAsync 里各调一次 —— "导航后那块阻塞"的并列嫌疑（只记耗时，不改行为）。
            var cmdWatch = Stopwatch.StartNew();
            CommandRefresh.Request();
            LpLog.Write(LogLevel.Info, NavLogCategory, "cmdrefresh:requery(refresh-finally)",
                elapsedMs: cmdWatch.ElapsedMilliseconds);
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
                IsNavigating = false;   // 刷新链（含挂起补刷）全部结束 → 收加载遮罩
                var wasNavigation = _navigatingInChain;
                _navigatingInChain = false;
                RefreshCompleted?.Invoke(this, wasNavigation);   // 链结束只发一次（行入场动画据此判定）
                if (wasNavigation)
                {
                    // 遮罩可见窗口的收尾时刻（cat=app.nav）：与 `refresh:` 行的时间戳相减 = 遮罩亮了多久。
                    // 遮罩是导航态每帧 InvalidateVisual 的那一支（见 SmartProbe/TOOL.md 的饿死记录），
                    // 导航结束后的淡出帧也落在这之后。
                    LpLog.Write(LogLevel.Info, NavLogCategory,
                        "overlay:hidden (navigation chain settled)", elapsedMs: navWatch.ElapsedMilliseconds);
                }
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

    /// <summary>消费粘贴定位请求：把目标行滚入视口（行不在当前数据里则保持待命，下次刷新再试）。</summary>
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
    {
        // **改名进行中 → 推迟重建**：整表 ReplaceAll 会销毁承载改名编辑框的行——
        // 编辑中的文本/焦点/光标全部丢失（乐观插入新建文件夹后，事件刷新恰好落在打字中途，
        // 实测必现）。推迟到提交/取消时立即补跑（<see cref="FlushDeferredRefresh"/>），
        // 窗口 = 改名时长（秒级）；期间的其它写操作由后续事件刷新照常追平。
        if (_rename.IsActive)
        {
            _refreshDeferredByRename = true;
            return Task.CompletedTask;
        }
        return RefreshAsync(clearSelection: false);
    }

    private bool _refreshDeferredByRename;

    /// <summary>首页/续载的每页链接数（SQL LIMIT）：2 000 条 ≈ 引擎 ~100ms + VM ~25ms，
    /// 首开无感；十万级目录靠滚动续载铺开，单次成本恒定。文件夹不参与分页（引擎整页返回）。</summary>
    internal const int PageChunkSize = 2000;

    private int _loadedPage = 1;
    private int _lastPage = 1;
    private int _directLinkTotal;
    private bool _pageLoadInFlight;

    /// <summary>还有未加载的链接页（状态栏"已加载前 N 项"与续载触发的判据）。</summary>
    public bool HasMorePages => _loadedPage < _lastPage;

    /// <summary>当前已加载的链接数（min(已到页号 × 页大小, 引擎总数)）。</summary>
    private int TotalLoadedLinks => Math.Min(_loadedPage * PageChunkSize, _directLinkTotal);

    /// <summary>
    /// 分页续载：滚动接近底部（<see cref="SortableDataTable.ScrollNearBottom"/>）时取下一页链接并**追加**
    /// （BulkObservableCollection.AddRange = 一次 Reset 通知）。续载是纯追加：不重建、不触碰选中/改名会话
    /// （改名编辑中的行对象原样保留）。降序时链接在前，新页插到首个文件夹行之前保持顺序。
    /// 目录已切换 / 已有更新刷新 → 丢弃（按发起时的目录 ID 与请求页号核对）。
    /// </summary>
    public async Task RequestNextPageAsync()
    {
        // 改名进行中不续载：改名编辑框在行尾时会"自动滚到底"跟焦（WPF 聚焦语义），
        // 续载追加 → 集合 Reset → 编辑框重建再聚焦 → 再滚底 → 再触发……实测把剩余页全部拉完。
        // 改名结束的补跑刷新本就回到首页态，这里续了也白续。
        if (!HasMorePages || _pageLoadInFlight || IsLoading || _rename.IsActive) return;
        var folderId = Controller.CurrentFolderId;
        var nextPage = _loadedPage + 1;
        _pageLoadInFlight = true;
        try
        {
            var dto = await _client.FoldersOverviewAsync(folderId, sortBy: SortBy, sortOrder: SortOrder,
                page: nextPage, perPage: PageChunkSize);
            if (Controller.CurrentFolderId != folderId || dto.CurrentPage != nextPage) return;   // 已切换 → 丢弃

            var rows = dto.Links.Select(l => new BrowserRowViewModel(l.LinkId, isFolder: false, l.Title)
            {
                Url = l.Url,
                ModifiedAt = l.UpdatedAt,
                CreatedAt = l.CreatedAt,
                LastViewedAt = l.LastVisitedAt,
                ViewCount = l.VisitCount,
                Favicon = Services.FaviconService.LoadFromCache(l.FaviconUrl),
                Host = this,
                IsCut = _clipboardCtl.IsCutInClipboard(l.LinkId, false),
            }).ToList();

            if (SortOrder == "desc")
            {
                // 降序 = 链接 → 文件夹：新页插到**第一个文件夹行**之前（置尾项在最尾，不受影响）
                var insertAt = Rows.Count;
                for (var i = 0; i < Rows.Count; i++)
                    if (Rows[i].IsFolder) { insertAt = i; break; }   // 降序：首个文件夹行 = 链接区的末尾
                for (var i = rows.Count - 1; i >= 0; i--) Rows.Insert(insertAt, rows[i]);
            }
            else
            {
                Rows.AddRange(rows);   // 升序 = 文件夹 → 链接：追加到尾
            }

            _loadedPage = dto.CurrentPage;
            StatusText = HasMorePages
                ? Loc.K("browser.status.totalPaged",
                    dto.SubFolders.Count + _directLinkTotal, dto.SubFolders.Count, _directLinkTotal,
                    dto.SubFolders.Count + TotalLoadedLinks)
                : Loc.K("browser.status.totalWithBreakdown",
                    dto.SubFolders.Count + _directLinkTotal, dto.SubFolders.Count, _directLinkTotal);

            var faviconUrlById = new Dictionary<string, string?>(dto.Links.Count, StringComparer.Ordinal);
            foreach (var l in dto.Links) faviconUrlById[l.LinkId] = l.FaviconUrl;
            StartFaviconPrefetch(rows, faviconUrlById);
        }
        catch (Exception ex)
        {
            LpLog.Error("folder page continuation failed", ex);   // 续载失败留痕；下次滚到底自动重试
        }
        finally
        {
            _pageLoadInFlight = false;
        }
    }

    /// <summary>
    /// favicon 后台预取 + 回填：行已可见，失败只丢图标（下次事件刷新追平）。
    /// 下载的并发闸/去重/大小上限统一在 Contracts.FaviconCache；这里只负责
    /// "下载完把图补到**当前显示的行**" —— 逐行 Dispatcher.Invoke 是每行一次跨线程往返，
    /// 且回填前的 FirstOrDefault 曾在后台线程上读 UI 拥有的 Rows（集合可能正在被替换）。
    /// 主刷新与分页续载共用（续载行同样只画名称 + 图标）。
    /// </summary>
    private void StartFaviconPrefetch(List<BrowserRowViewModel> linkRows,
        Dictionary<string, string?> faviconUrlById)
    {
        // favicon 懒加载清单：磁盘缓存未命中时后台拉取，完成后补到对应行
        var missing = linkRows
            .Where(r => r.Favicon == null)
            .Select(r => faviconUrlById.GetValueOrDefault(r.Id))
            .Where(url => !string.IsNullOrEmpty(url))
            .Select(url => url!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (missing.Count == 0) return;
        var pendingIds = linkRows.Where(r => r.Favicon == null && !string.IsNullOrEmpty(faviconUrlById.GetValueOrDefault(r.Id)))
                                 .Select(r => r.Id)
                                 .ToList();
        _ = Task.Run(async () =>
        {
            try { await Task.WhenAll(missing.Select(Services.FaviconService.PrefetchAndCacheAsync)); }
            catch { /* 网络失败属预期波动，行保持无图标 */ }

            var landed = new List<(string Id, System.Windows.Media.Imaging.BitmapImage Image)>();
            foreach (var id in pendingIds)
            {
                var img = Services.FaviconService.LoadFromCache(faviconUrlById.GetValueOrDefault(id));
                if (img != null) landed.Add((id, img));
            }

            if (landed.Count == 0) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            // 单次批量回填：索引在 UI 线程上建一次，之后 O(1) 命中
            _ = dispatcher.BeginInvoke(() =>
            {
                // 观测定痕（cat=app.nav）：这段是**刷新链之后的异步续体**（网络取完图标才回填），
                // 回填 = 给行换图标 ⇒ 重绑 + 重排 + 重绘 —— 正是"导航后 +240~380ms 那块阻塞"的嫌疑。
                // 记条数与自身耗时，与 `refresh:` 行按时间戳对一下即可归因（不改任何行为）。
                var watch = Stopwatch.StartNew();
                var live = new Dictionary<string, BrowserRowViewModel>(Rows.Count, StringComparer.Ordinal);
                foreach (var row in Rows) live.TryAdd(row.Id, row);
                var applied = 0;
                foreach (var (id, image) in landed)
                    if (live.TryGetValue(id, out var row) && row.Favicon == null)
                    {
                        row.SetFavicon(image);
                        applied++;
                    }
                LpLog.Write(LogLevel.Info, NavLogCategory,
                    $"favicon:backfill fetched={missing.Count} landed={landed.Count} applied={applied} rows={Rows.Count}",
                    elapsedMs: watch.ElapsedMilliseconds);
            });
        });
    }

    /// <summary>补跑被改名推迟的刷新（提交/取消收尾时调用；无推迟 = 空操作）。</summary>
    private void FlushDeferredRefresh()
    {
        if (!_refreshDeferredByRename) return;
        _refreshDeferredByRename = false;
        _ = RefreshPreservingSelectionAsync();
    }

}