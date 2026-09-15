using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Api;
using LinkPocket.Models;

namespace LinkPocket.ViewModels;

/// <summary>
/// 资源管理器式浏览页（P4）：一切数据经 folders.contents 协议获取，
/// 渲染由 XAML ItemsControl + DataTemplate 完成，本类不持有任何控件引用。
/// </summary>
public class BrowserViewModel : INotifyPropertyChanged
{
    private static ILinkPocketApi Api => Services.AppServices.Api;

    public Managers.NavigationController Controller { get; } = new();

    public ObservableCollection<BrowserRowViewModel> Rows { get; } = new();
    public ObservableCollection<BrowserCrumbViewModel> Breadcrumbs { get; } = new();

    private string _statusText = "就绪";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public ICommand GoBackCommand { get; }
    public ICommand GoForwardCommand { get; }
    public ICommand GoUpCommand { get; }
    public ICommand RowClickCommand { get; }
    public ICommand RowOpenCommand { get; }
    public ICommand CrumbClickCommand { get; }

    /// <summary>文件夹 ID → 父 ID 映射（含名称），用于面包屑与"返回上级"。</summary>
    private Dictionary<string, (string? ParentId, string Name)> _folderMap = new();

    public BrowserViewModel()
    {
        GoBackCommand = new RelayCommand(() => _ = LoadAsync(Controller.GoBack()), () => Controller.CanGoBack);
        GoForwardCommand = new RelayCommand(() => _ = LoadAsync(Controller.GoForward()), () => Controller.CanGoForward);
        GoUpCommand = new RelayCommand(() => _ = LoadAsync(GetParentId(Controller.CurrentFolderId)), () => !IsAtRoot());
        RowClickCommand = new RelayCommand<BrowserRowViewModel?>(SelectRow);
        RowOpenCommand = new RelayCommand<BrowserRowViewModel?>(row => _ = OpenRowAsync(row));
        CrumbClickCommand = new RelayCommand<BrowserCrumbViewModel?>(crumb => _ = LoadAsync(crumb?.FolderId));
    }

    /// <summary>进入指定目录（null/"0" = 根）。首次显示页面时调用 LoadAsync(null)。</summary>
    public async Task LoadAsync(string? folderId)
    {
        Controller.NavigateTo(folderId);
        await RefreshAsync();
    }

    /// <summary>重新加载当前目录（供事件推送订阅调用）。</summary>
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var contents = await Api.GetFolderContentsAsync(Controller.CurrentFolderId);

            // 文件夹映射：面包屑 + 返回上级需要父链
            var tree = await Api.GetFolderTreeAsync();
            _folderMap = tree.ToDictionary(f => f.FolderId, f => (f.ParentId, f.Name));

            Rows.Clear();
            foreach (var folder in contents.SubFolders)
            {
                Rows.Add(new BrowserRowViewModel(folder.FolderId, isFolder: true, folder.Name)
                {
                    LinkCount = folder.LinkCount,
                    ModifiedAt = DateTime.UtcNow
                });
            }

            var linkRows = new List<BrowserRowViewModel>();
            foreach (var link in contents.Links)
            {
                linkRows.Add(new BrowserRowViewModel(link.LinkId, isFolder: false, link.Title)
                {
                    Url = link.Url,
                    IsImportant = link.IsImportant,
                    ModifiedAt = link.UpdatedAt,
                    Favicon = Services.FaviconService.LoadFromCache(link.FaviconUrl)
                });
            }

            // favicon 懒加载：磁盘缓存未命中时拉取，完成后补到对应行
            var missing = linkRows
                .Where(r => r.Favicon == null)
                .Select(r => contents.Links.First(l => l.LinkId == r.Id).FaviconUrl)
                .Where(url => !string.IsNullOrEmpty(url))
                .Distinct()
                .ToList();
            if (missing.Count > 0)
            {
                await Task.WhenAll(missing.Select(Services.FaviconService.PrefetchAndCacheAsync));
                foreach (var row in linkRows.Where(r => r.Favicon == null))
                {
                    var dto = contents.Links.FirstOrDefault(l => l.LinkId == row.Id);
                    if (dto != null)
                        row.SetFavicon(Services.FaviconService.LoadFromCache(dto.FaviconUrl));
                }
            }

            foreach (var row in linkRows) Rows.Add(row);

            // 面包屑（含 ID，可点击跳转；最后一级为当前目录，高亮显示）
            Breadcrumbs.Clear();
            Breadcrumbs.Add(new BrowserCrumbViewModel(null, "全部书签"));
            var chain = BuildBreadcrumbIds(Controller.CurrentFolderId).ToList();
            for (int i = 0; i < chain.Count; i++)
            {
                Breadcrumbs.Add(new BrowserCrumbViewModel(chain[i].Id, chain[i].Name)
                {
                    IsLast = i == chain.Count - 1
                });
            }

            StatusText = $"共 {contents.SubFolders.Count + contents.Links.Count} 项" +
                         $"（{contents.SubFolders.Count} 个文件夹 / {contents.Links.Count} 个书签）";
        }
        catch (Exception)
        {
            StatusText = "加载失败";
        }
        finally
        {
            IsLoading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // —— 交互 ——

    private void SelectRow(BrowserRowViewModel? row)
    {
        if (row == null) return;
        foreach (var r in Rows)
            r.IsSelected = ReferenceEquals(r, row);
    }

    public void ClearSelection()
    {
        foreach (var r in Rows)
            r.IsSelected = false;
    }

    private async Task OpenRowAsync(BrowserRowViewModel? row)
    {
        if (row == null) return;
        SelectRow(row);

        if (row.IsFolder)
        {
            await LoadAsync(row.Id);
            return;
        }

        // 双击书签：默认浏览器打开 + 记录访问
        if (!string.IsNullOrEmpty(row.Url))
        {
            try
            {
                Process.Start(new ProcessStartInfo(row.Url) { UseShellExecute = true });
            }
            catch { /* 无法打开时保持静默 */ }
            _ = Task.Run(async () => { try { await Api.RecordVisitAsync(row.Id); } catch { } });
        }
    }

    private string GetParentId(string? folderId)
    {
        if (IsAtRoot()) return "0";
        var id = folderId!;
        return _folderMap.TryGetValue(id, out var info) ? info.ParentId ?? "0" : "0";
    }

    private bool IsAtRoot()
        => string.IsNullOrEmpty(Controller.CurrentFolderId) || Controller.CurrentFolderId == "0";

    private IEnumerable<(string Id, string Name)> BuildBreadcrumbIds(string? folderId)
    {
        if (IsAtRoot()) yield break;

        var chain = new List<(string Id, string Name)>();
        var current = folderId;
        while (!string.IsNullOrEmpty(current) && _folderMap.TryGetValue(current, out var info))
        {
            chain.Add((current, info.Name));
            current = info.ParentId;
        }
        chain.Reverse();
        foreach (var item in chain) yield return item;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
