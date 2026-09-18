using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;

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
                if (_host != null) _host.StatusText = "已复制 ID";
            }
            catch { }
        });
    private RelayCommand? _copyIdCommand;

    /// <summary>查看链接详情页（仅单选链接可用；复用 Host 的详情页能力）。</summary>
    public ICommand ShowDetailCommand => _showDetailCommand ??= new RelayCommand(
        () => _ = _host?.OpenDetailPageAsync(_host.SelectedRows.FirstOrDefault()),
        () => _host != null && IsSingle && !IsFolder);
    private RelayCommand? _showDetailCommand;

    public BrowserDetailsViewModel(EngineClient client)
    {
        _client = client;
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
        RenameCommand = new RelayCommand(
            () => _host?.RenameSelectionCommand.Execute(null),
            () => _host?.RenameSelectionCommand.CanExecute(null) == true);
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
                    if (_host != null) _host.StatusText = "已复制链接";
                }
                catch { }
            },
            () => IsLink && !string.IsNullOrEmpty(UrlText));
    }

    /// <summary>
    /// 打开网站 = 系统默认浏览器打开 URL 并记录一次访问（与链接详情页「打开网站」同口径）。
    /// 记账后回读列表，让「最后查看 / 累计查看」的派生统计及时反映这一次。
    /// await 刷新防止详情栏闪"读取中…"（2.5-24）。
    /// </summary>
    private async Task OpenWebsiteInBrowserAsync()
    {
        if (string.IsNullOrEmpty(UrlText)) return;
        var host = _host;
        if (host == null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UrlText) { UseShellExecute = true }); }
        catch { Services.Logger.Error($"打开网站失败: {UrlText}", null); }   // 观测面：失败留痕而非完全静默
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
                    new() { IconKind = "folder-outline", Label = "位置", Value = path },
                    new() { IconKind = "link-variant", Label = "链接数", Value = BookmarkCountText, IsAccent = true },
                    new() { IconKind = "refresh", Label = "最后更新", Value = updated },
                    new() { IconKind = "history", Label = "最后查看", Value = lastVisited },
                    new() { IconKind = "trending-up", Label = "查看次数", Value = ViewCountText },
                    new() { IconKind = "plus-circle-outline", Label = "创建时间", Value = created },
                    new() { IconKind = "fingerprint", Label = "ID", Value = row.Id, IsMono = true, CopyCommand = CopyIdCommand, CopyToolTip = "复制 ID" },
                });
            }
            else
            {
                // 同步先用行内已有数据渲染，再异步补拉描述/统计/路径
                SetRows(new List<DetailSidebarRow>
                {
                    new() { IconKind = "folder-outline", Label = "位置", Value = LoadingPlaceholder },
                    new() { IconKind = "refresh", Label = "最后更新", Value = row.ModifiedText },
                    new() { IconKind = "history", Label = "最后查看", Value = "—" },
                    new() { IconKind = "trending-up", Label = "查看次数", Value = "—" },
                    new() { IconKind = "plus-circle-outline", Label = "创建时间", Value = "—" },
                    new() { IconKind = "fingerprint", Label = "ID", Value = row.Id, IsMono = true, CopyCommand = CopyIdCommand, CopyToolTip = "复制 ID" },
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
                MarkUnavailable();   // 源已删除/宿主缺失：占位回落，不留"读取中…"（E10）
                return;
            }

            DescriptionText = link.Description ?? "";

            // 异步补拉结果原位写回数据行（INPC 通知，无需重建整卡）
            var pathRow = FindRow("位置");
            if (pathRow != null) pathRow.Value = _host.GetFolderPathDisplay(link.ListId);
            var updatedRow = FindRow("最后更新");
            if (updatedRow != null)
                updatedRow.Value = link.UpdatedAt.Year <= 1 ? "—" : link.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var visitedRow = FindRow("最后查看");
            if (visitedRow != null)
                visitedRow.Value = link.LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未";
            var visitRow = FindRow("查看次数");
            if (visitRow != null) visitRow.Value = $"{link.VisitCount} 次";
            var createdRow = FindRow("创建时间");
            if (createdRow != null)
                createdRow.Value = link.CreatedAt.Year <= 1 ? "—" : link.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

            OnPropertyChanged(nameof(DescriptionText));
            OnPropertyChanged(nameof(HasDescription));
        }
        catch
        {
            if (gen != _generation) return;   // 2.4：切选后到达的异常不得影响新选中信息卡
            Services.Logger.Error("详情栏链接补拉失败（保持行内基础信息）", null);   // 观测面留痕
            MarkUnavailable();   // 补拉失败：占位回落，绝不让详情栏永久"读取中…"（E10）
        }
    }

    /// <summary>「读取中…」占位文案（UpdateFrom 与 MarkUnavailable 共用单一数据源，3.3：防文案改动静默失效）。</summary>
    private const string LoadingPlaceholder = "读取中…";

    /// <summary>补拉失败/源已删除的收口：把「读取中…」占位回落为中性值（其余占位本就是 —/从未）。</summary>
    private void MarkUnavailable()
    {
        var pathRow = FindRow("位置");
        if (pathRow != null && pathRow.Value == LoadingPlaceholder) pathRow.Value = "未获取到信息";
        var visitedRow = FindRow("最后查看");
        if (visitedRow != null && visitedRow.Value == "—") visitedRow.Value = "从未";
    }
}
