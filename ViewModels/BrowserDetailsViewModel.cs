using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LinkPocket.Api;

namespace LinkPocket.ViewModels;

/// <summary>
/// 浏览页右侧详情栏（P4.5）：参考链接页 DetailPanel 的信息结构重新设计。
/// 单选书签 / 单选文件夹 / 多选 / 空四种状态，全部经属性通知驱动 XAML 渲染，
/// 不在视图层代码中拼控件。书签的描述、访问统计等扩展字段按需异步补拉。
/// </summary>
public class BrowserDetailsViewModel : INotifyPropertyChanged
{
    private static ILinkPocketApi Api => Services.AppServices.Api;

    private BrowserViewModel? _host;

    /// <summary>选中代次：异步补拉返回时校验，避免旧结果覆盖新选中。</summary>
    private int _generation;

    // —— 状态 ——
    public bool HasSelection { get; private set; }
    public bool IsPlaceholder => !HasSelection;
    public bool IsMulti { get; private set; }
    public bool IsSingle => HasSelection && !IsMulti;
    public bool IsFolder { get; private set; }
    public bool IsLink => IsSingle && !IsFolder;

    // —— 单选公共 ——
    public string DisplayName { get; private set; } = "";
    public string IdText { get; private set; } = "";
    public string PathText { get; private set; } = "";
    public string ModifiedText { get; private set; } = "—";
    public BitmapImage? Favicon { get; private set; }
    public bool HasFavicon => Favicon != null;

    // —— 单选书签 ——
    public string UrlText { get; private set; } = "";
    public string DescriptionText { get; private set; } = "";
    public bool HasDescription => IsLink && !string.IsNullOrWhiteSpace(DescriptionText);
    public string UpdatedText { get; private set; } = "—";
    public string LastVisitedText { get; private set; } = "—";
    public string VisitText { get; private set; } = "0 次";
    public string CreatedText { get; private set; } = "—";

    // —— 单选文件夹 ——
    public string ViewCountText { get; private set; } = "0 次";

    public int FolderBookmarkCount { get; private set; }
    public string BookmarkCountText => $"{FolderBookmarkCount} 个链接";

    // —— 多选 ——
    public int SelectedTotal { get; private set; }
    public int SelectedFolders { get; private set; }
    public int SelectedLinks { get; private set; }

    /// <summary>
    /// 单选操作卡里铅笔按钮的文案：链接是「编辑」（打开整页编辑器，可改 URL/名称/描述/图标），
    /// 文件夹是「重命名」（文件夹本身只有名称）。
    /// </summary>
    public string EditLabel => IsFolder ? "重命名" : "编辑";

    // —— 命令：复用 Host 的既有能力，避免第二套业务逻辑 ——
    /// <summary>打开：链接 → 打开链接详情页；文件夹 → 进入该目录。</summary>
    public ICommand OpenCommand => _openCommand ??= new RelayCommand(
        () =>
        {
            var row = _host?.SelectedRows.FirstOrDefault();
            if (row == null || _host == null) return;
            if (row.IsFolder) _host.OpenSelectionCommand.Execute(null);
            else _ = _host.OpenDetailPageAsync(row);
        },
        () => _host?.OpenSelectionCommand.CanExecute(null) == true);
    private RelayCommand? _openCommand;

    /// <summary>查看链接详情页（仅单选链接可用；复用 Host 的详情页能力）。</summary>
    public ICommand ShowDetailCommand => _showDetailCommand ??= new RelayCommand(
        () => _ = _host?.OpenDetailPageAsync(_host.SelectedRows.FirstOrDefault()),
        () => _host != null && IsSingle && !IsFolder);
    private RelayCommand? _showDetailCommand;

    public ICommand CopyUrlCommand => _copyUrlCommand ??= new RelayCommand(CopyUrl, () => IsLink && !string.IsNullOrEmpty(UrlText));
    private RelayCommand? _copyUrlCommand;

    public ICommand RenameCommand => _renameCommand ??= new RelayCommand(
        () => _host?.RenameSelectionCommand.Execute(null),
        () => _host?.RenameSelectionCommand.CanExecute(null) == true);
    private RelayCommand? _renameCommand;

    public ICommand DeleteCommand => _deleteCommand ??= new RelayCommand(
        () => _host?.DeleteSelectionCommand.Execute(null),
        () => _host?.DeleteSelectionCommand.CanExecute(null) == true);
    private RelayCommand? _deleteCommand;

    public ICommand CopyIdCommand => _copyIdCommand ??= new RelayCommand(
        () => { try { if (!string.IsNullOrEmpty(IdText)) System.Windows.Clipboard.SetText(IdText); } catch { } });
    private RelayCommand? _copyIdCommand;

    /// <summary>由 BrowserViewModel 在选中态变化时调用。rows 需为快照列表。</summary>
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
            UpdatedText = row.ModifiedText;
            LastVisitedText = "—";
            VisitText = "—";
            CreatedText = "—";

            if (row.IsFolder)
            {
                FolderBookmarkCount = row.LinkCount;
                ViewCountText = $"{row.ViewCount} 次";
                PathText = host.GetFolderPathDisplay(row.Id, includeSelf: false);
                CreatedText = row.CreatedAt.Year <= 1
                    ? "—"
                    : row.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                LastVisitedText = row.LastViewedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未";
            }
            else
            {
                PathText = "读取中…";
                // 同步先用行内已有数据渲染，再异步补拉描述/统计/路径
                _ = LoadLinkDetailsAsync(row.Id, gen);
            }
        }
        else
        {
            DisplayName = "";
            IdText = "";
            UrlText = "";
            PathText = "";
            Favicon = null;
            DescriptionText = "";
        }

        RaiseAll();
    }

    private async Task LoadLinkDetailsAsync(string linkId, int gen)
    {
        try
        {
            var link = (await Api.GetAllLinksAsync()).FirstOrDefault(l => l.LinkId == linkId);
            if (gen != _generation || link == null || _host == null) return; // 已切换选中或源已删除

            DescriptionText = link.Description ?? "";
            UpdatedText = link.UpdatedAt.Year <= 1 ? "—" : link.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            LastVisitedText = link.LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未";
            VisitText = $"{link.VisitCount} 次";
            CreatedText = link.CreatedAt.Year <= 1 ? "—" : link.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            PathText = _host.GetFolderPathDisplay(link.ListId);

            OnPropertyChanged(nameof(DescriptionText));
            OnPropertyChanged(nameof(HasDescription));
            OnPropertyChanged(nameof(UpdatedText));
            OnPropertyChanged(nameof(LastVisitedText));
            OnPropertyChanged(nameof(VisitText));
            OnPropertyChanged(nameof(CreatedText));
            OnPropertyChanged(nameof(PathText));
        }
        catch { /* 补拉失败时保留行内基础信息 */ }
    }

    private void CopyUrl()
    {
        try { if (!string.IsNullOrEmpty(UrlText)) System.Windows.Clipboard.SetText(UrlText); } catch { }
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ViewCountText));
        OnPropertyChanged(nameof(IsPlaceholder));
        OnPropertyChanged(nameof(IsMulti));
        OnPropertyChanged(nameof(IsSingle));
        OnPropertyChanged(nameof(IsFolder));
        OnPropertyChanged(nameof(IsLink));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IdText));
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(ModifiedText));
        OnPropertyChanged(nameof(Favicon));
        OnPropertyChanged(nameof(HasFavicon));
        OnPropertyChanged(nameof(UrlText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(LastVisitedText));
        OnPropertyChanged(nameof(VisitText));
        OnPropertyChanged(nameof(CreatedText));
        OnPropertyChanged(nameof(FolderBookmarkCount));
        OnPropertyChanged(nameof(BookmarkCountText));
        OnPropertyChanged(nameof(EditLabel));
        OnPropertyChanged(nameof(SelectedTotal));
        OnPropertyChanged(nameof(SelectedFolders));
        OnPropertyChanged(nameof(SelectedLinks));
        CommandManager.InvalidateRequerySuggested();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
