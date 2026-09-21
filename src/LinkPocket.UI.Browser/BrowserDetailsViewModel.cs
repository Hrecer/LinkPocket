using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels;

/// <summary>
/// 浏览页右侧详情栏数据模型：继承通用 <see cref="DetailSidebarModel"/>，
/// 只负责「选中行快照 → 数据」的映射与异步补拉；渲染全部交给 Views/DetailSidebar 控件。
/// 单选书签 / 单选文件夹 / 多选 / 空四种状态；信息卡行按选中对象类型构建（数据驱动）。
/// </summary>
public class BrowserDetailsViewModel : DetailSidebarModel
{
    /// <summary>引擎客户端门面（分层 API 面，由组合根注入）。</summary>
    private readonly EngineClient _client;

    /// <summary>定位组件（「跳转」用：进它所在目录 + 选中该行）——与结果页 / ID 跳转同一条流水线。</summary>
    private readonly Services.IContentLocator? _locator;

    private BrowserViewModel? _host;

    /// <summary>选中代次：异步补拉返回时校验，避免旧结果覆盖新选中。</summary>
    private int _generation;

    // —— 浏览页特有（通用模型之外的字段） ——
    public string ModifiedText { get; private set; } = "—";
    public string ViewCountText { get; private set; } = "0 次";

    public int FolderBookmarkCount { get; private set; }
    public string BookmarkCountText => $"{FolderBookmarkCount} 个链接";

    public ICommand CopyIdCommand => _copyIdCommand ??= new RelayCommand(
        () =>
        {
            try
            {
                if (string.IsNullOrEmpty(IdText)) return;
                System.Windows.Clipboard.SetText(IdText);
                if (_host != null) _host.StatusText = Loc.T("status.idCopied");
            }
            catch { }
        });
    private RelayCommand? _copyIdCommand;

    /// <summary>查看链接详情页（仅单选链接可用；复用 Host 的详情页能力）。</summary>
    public ICommand ShowDetailCommand => _showDetailCommand ??= new RelayCommand(
        () => _ = _host?.OpenDetailPageAsync(_host.SelectedRows.FirstOrDefault()),
        () => _host != null && IsSingle && !IsFolder);
    private RelayCommand? _showDetailCommand;

    public BrowserDetailsViewModel(EngineClient client, Services.IContentLocator? locator = null)
    {
        _client = client;
        _locator = locator;
        // 页面动作命令：复用 Host 的既有能力，避免第二套业务逻辑
        OpenCommand = new RelayCommand(
            () =>
            {
                var row = _host?.SelectedRows.FirstOrDefault();
                if (row == null || _host == null) return;
                if (row.IsFolder) _host.OpenSelectionCommand.Execute(null);
                else _ = _host.OpenDetailPageAsync(row);
            },
            () => _host?.OpenSelectionCommand.CanExecute(null) == true);
        OpenWebsiteCommand = new RelayCommand(
            () => _ = OpenWebsiteInBrowserAsync(),
            () => IsLink && !string.IsNullOrEmpty(UrlText));
        // 铅笔槽（一枚钮，按行类型分派）：链接 = 打开整页编辑器；文件夹 = 就地改标题。
        // 「重命名」图标钮（独立那枚）= 就地改标题，只对链接开——两者都复用 Host 既有命令，不写第二套逻辑。
        RenameCommand = new RelayCommand(
            () =>
            {
                var row = _host?.SelectedRows.FirstOrDefault();
                if (row == null || _host == null) return;
                if (row.IsFolder) _host.RenameSelectionCommand.Execute(null);
                else _host.EditRowCommand.Execute(row);
            },
            () => IsFolder
                ? _host?.RenameSelectionCommand.CanExecute(null) == true
                : IsLink && _host != null);
        // 「重命名」图标钮的命令在 UpdateFrom 里直接挂宿主既有命令实例（同一实例，不是第二套逻辑）
        DeleteCommand = new RelayCommand(
            () => _host?.DeleteSelectionCommand.Execute(null),
            () => _host?.DeleteSelectionCommand.CanExecute(null) == true);
        CopyUrlCommand = new RelayCommand(
            () =>
            {
                try
                {
                    if (string.IsNullOrEmpty(UrlText)) return;
                    System.Windows.Clipboard.SetText(UrlText);
                    if (_host != null) _host.StatusText = Loc.T("status.linkCopied");
                }
                catch { }
            },
            () => IsLink && !string.IsNullOrEmpty(UrlText));
        // 「跳转」= 把选中项带到眼前（经定位组件：进它所在目录 + 选中该行；**已在同目录时就是"选中并滚入视口"**）。
        // 浏览页顶部**不加**跳转（本页就是定位的落点），但同一目录里条目多、选中的那一行在视口外时同样需要它。
        // 只对**单一目标**开（多选没有"某一项"可定位——与结果页同一口径）。
        JumpCommand = new RelayCommand(
            () => _ = JumpToSelectionAsync(),
            () => IsSingle && _locator != null);
    }

    /// <summary>
    /// 「跳转」：进选中项所在目录 + 选中该行（定位组件 <see cref="Services.IContentLocator"/>，
    /// 与「ID 跳转」/ 结果页跳转同一条流水线，类型（链接 / 文件夹）由引擎 `locate.resolve` 判别）。
    /// 失败按本页口径走状态栏（本页的操作结果一律状态栏播报），并写日志——绝不静默。
    /// </summary>
    private async Task JumpToSelectionAsync()
    {
        var host = _host;
        var row = host?.SelectedRows.FirstOrDefault();
        if (host == null || row == null) return;
        if (_locator == null)
        {
            LpLog.Error("jump failed: the locator component is unavailable", null);   // 观测面：失败留痕
            return;
        }

        var result = await _locator.LocateAsync(row.Id);
        if (result.IsSuccess) return;

        host.StatusText = result.Message ?? result.Status switch
        {
            Services.LocateStatus.NotFound => "未找到该项 ID",
            Services.LocateStatus.RowMissing => "目标行未出现在所在目录（可能刚被移动或删除）",
            Services.LocateStatus.Failed => "locate failed，请稍后重试",
            _ => "定位未完成",
        };
    }

    /// <summary>
    /// 打开网站 = 系统默认浏览器打开 URL 并记录一次访问（与链接详情页「打开网站」同口径）。
    /// 记账后回读列表，让「最后查看 / 累计查看」的派生统计及时反映这一次。
    /// await 刷新防止详情栏闪"读取中…"。
    /// </summary>
    private async Task OpenWebsiteInBrowserAsync()
    {
        if (string.IsNullOrEmpty(UrlText)) return;
        var host = _host;
        if (host == null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UrlText) { UseShellExecute = true }); }
        catch { LpLog.Error($"failed to open the site: {UrlText}", null); }   // 观测面：失败留痕而非完全静默
        try
        {
            await _client.LinkVisitRecordAsync(IdText);
            await host.RefreshPreservingSelectionAsync();
        }
        catch { /* 记账失败不打断 */ }
    }

    /// <summary>
    /// 由 BrowserViewModel 在选中态变化时调用。rows 需为快照列表。
    /// 信息卡行按选中对象类型构建：文件夹含「链接数」，链接经异步补拉填充统计/路径。
    /// </summary>
    internal void UpdateFrom(IReadOnlyList<BrowserRowViewModel> rows, BrowserViewModel host)
    {
        _host = host;
        _generation++;
        var gen = _generation;

        HasSelection = rows.Count > 0;
        IsMulti = rows.Count > 1;
        IsFolder = rows.Count == 1 && rows[0].IsFolder;
        ConfigureSidebarActionLabels(IsFolder);   // 动作面缺省文案/列宽（共享基类，按行类型）
        // 侧栏动作面（浏览页）：两行排布（药丸一行 / 图标钮一行靠右——286 宽同排必然裁字，
        // 与回收站右栏同一套能力位与同一对按钮模板，不是第二套详情栏）；
        // 「重命名」图标钮只对**链接**开（就地改标题；文件夹由铅笔承担，避免同一动作两枚钮）。
        StackedActions = rows.Count > 0;   // 有选中时动作卡才出现；本页一律两行排布
        ShowRenameAction = IsLink;
        RenameActionCommand = host.RenameSelectionCommand;   // 同一实例：复用本页就地改名命令，不写第二套
        // 「跳转」只对**单一目标**开（多选没有"某一项"可定位；与结果页同一口径）
        ShowJumpAction = rows.Count == 1;

        SelectedTotal = rows.Count;
        SelectedFolders = rows.Count(r => r.IsFolder);
        SelectedLinks = rows.Count - SelectedFolders;

        if (rows.Count == 1)
        {
            var row = rows[0];
            DisplayName = row.Name;
            IdText = row.Id;
            ModifiedText = row.ModifiedText;
            Favicon = row.Favicon;
            UrlText = row.Url ?? "";
            DescriptionText = "";

            if (row.IsFolder)
            {
                FolderBookmarkCount = row.LinkCount;
                ViewCountText = $"{row.ViewCount} 次";
                var path = host.GetFolderPathDisplay(row.Id, includeSelf: false);
                var updated = row.ModifiedText;
                var lastVisited = row.LastViewedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未";
                var created = row.CreatedAt.Year <= 1
                    ? "—"
                    : row.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

                SetRows(new List<DetailSidebarRow>
                {
                    new() { IconKind = "folder-outline", LabelKey = "ui.noun.location", Value = path },
                    new() { IconKind = "link-variant", LabelKey = "ui.noun.linkCount", Value = BookmarkCountText, IsAccent = true },
                    new() { IconKind = "refresh", LabelKey = "ui.noun.updatedAt", Value = updated },
                    new() { IconKind = "history", LabelKey = "ui.noun.lastVisited", Value = lastVisited },
                    new() { IconKind = "trending-up", LabelKey = "ui.noun.visitCount", Value = ViewCountText },
                    new() { IconKind = "plus-circle-outline", LabelKey = "ui.noun.createdAt", Value = created },
                    new() { IconKind = "fingerprint", LabelKey = "ui.noun.id", Value = row.Id, IsMono = true, CopyCommand = CopyIdCommand, CopyToolTip = "复制 ID" },
                });
            }
            else
            {
                // 同步先用行内已有数据渲染，再异步补拉描述/统计/路径
                SetRows(new List<DetailSidebarRow>
                {
                    new() { IconKind = "folder-outline", LabelKey = "ui.noun.location", Value = LoadingPlaceholder },
                    new() { IconKind = "refresh", LabelKey = "ui.noun.updatedAt", Value = row.ModifiedText },
                    new() { IconKind = "history", LabelKey = "ui.noun.lastVisited", Value = "—" },
                    new() { IconKind = "trending-up", LabelKey = "ui.noun.visitCount", Value = "—" },
                    new() { IconKind = "plus-circle-outline", LabelKey = "ui.noun.createdAt", Value = "—" },
                    new() { IconKind = "fingerprint", LabelKey = "ui.noun.id", Value = row.Id, IsMono = true, CopyCommand = CopyIdCommand, CopyToolTip = "复制 ID" },
                });
                _ = LoadLinkDetailsAsync(row.Id, gen);
            }
        }
        else
        {
            DisplayName = "";
            IdText = "";
            UrlText = "";
            Favicon = null;
            DescriptionText = "";
        }

        RaiseAll();
    }

    private async Task LoadLinkDetailsAsync(string linkId, int gen)
    {
        try
        {
            LinkPocket.Contracts.LinkDto? link;
            try { link = await _client.LinkGetAsync(linkId); }   // 单点查询（原全量拉取后 FirstOrDefault，100k 库下点击即全表）
            catch (LinkPocket.Contracts.EngineException) { link = null; }
            if (gen != _generation) return; // 已切换选中：本批结果作废，新选中会重建信息卡

            if (link == null || _host == null)
            {
                MarkUnavailable();   // 源已删除/宿主缺失：占位回落，不留"读取中…"
                return;
            }

            DescriptionText = link.Description ?? "";

            // 异步补拉结果原位写回数据行（INPC 通知，无需重建整卡）
            var pathRow = FindRow("ui.noun.location");
            if (pathRow != null) pathRow.Value = _host.GetFolderPathDisplay(link.ListId);
            var updatedRow = FindRow("ui.noun.updatedAt");
            if (updatedRow != null)
                updatedRow.Value = link.UpdatedAt.Year <= 1 ? "—" : link.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var visitedRow = FindRow("ui.noun.lastVisited");
            if (visitedRow != null)
                visitedRow.Value = link.LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未";
            var visitRow = FindRow("ui.noun.visitCount");
            if (visitRow != null) visitRow.Value = $"{link.VisitCount} 次";
            var createdRow = FindRow("ui.noun.createdAt");
            if (createdRow != null)
                createdRow.Value = link.CreatedAt.Year <= 1 ? "—" : link.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

            OnPropertyChanged(nameof(DescriptionText));
            OnPropertyChanged(nameof(HasDescription));
        }
        catch
        {
            if (gen != _generation) return;   // 切选后到达的异常不得影响新选中信息卡
            LpLog.Error("detail pane link re-fetch failed (row basics kept)", null);   // 观测面留痕
            MarkUnavailable();   // 补拉失败：占位回落，绝不让详情栏永久"读取中…"
        }
    }

    /// <summary>「读取中…」占位文案（UpdateFrom 与 MarkUnavailable 共用单一数据源，防文案改动静默失效）。</summary>
    private const string LoadingPlaceholder = "读取中…";

    /// <summary>补拉失败/源已删除的收口：把「读取中…」占位回落为中性值（其余占位本就是 —/从未）。</summary>
    private void MarkUnavailable()
    {
        var pathRow = FindRow("ui.noun.location");
        if (pathRow != null && pathRow.Value == LoadingPlaceholder) pathRow.Value = "未获取到信息";
        var visitedRow = FindRow("ui.noun.lastVisited");
        if (visitedRow != null && visitedRow.Value == "—") visitedRow.Value = "从未";
    }
}
