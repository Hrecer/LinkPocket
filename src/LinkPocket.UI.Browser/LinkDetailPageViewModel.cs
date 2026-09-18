using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>
/// 浏览模块「链接详情页」视图模型（全页形态，参考链接页书签详情，风格与浏览右侧栏一致）。
/// 打开时记录一次访问（与链接页 ShowDetail 一致）；「打开网站」为显式按钮。
/// </summary>
public class LinkDetailPageViewModel : INotifyPropertyChanged
{
    /// <summary>引擎客户端门面（分层 API 面，由组合根注入）。</summary>
    private readonly EngineClient _client;

    private readonly BrowserViewModel _host;
    private string? _linkId;
    private int _generation;

    public LinkDetailPageViewModel(EngineClient client, BrowserViewModel host)
    {
        _client = client;
        _host = host;
        BackCommand = new RelayCommand(() => _ = BackAsync());
        // ⚠️ 不设 CanExecute：详情页打开的瞬间数据还在异步加载（_linkId/Url 尚空），
        // 若按 CanExecute 禁用，按钮会先以 0.4 透明度渲染、加载完又突然恢复 → 肉眼可见的闪烁。
        // 命令内部对空 URL 有守卫，提前点击只是无操作。
        OpenWebsiteCommand = new RelayCommand(() => OpenWebsite());
        CopyUrlCommand = new RelayCommand(CopyUrl, () => !string.IsNullOrEmpty(Url));
        EditCommand = new RelayCommand(Edit, () => _linkId != null);
        CopyIdCommand = new RelayCommand(CopyId, () => !string.IsNullOrEmpty(IdText));
        DeleteCommand = new RelayCommand(() => _ = DeleteAsync(), () => _linkId != null);
    }

    public ICommand BackCommand { get; }
    public ICommand OpenWebsiteCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand CopyIdCommand { get; }
    public ICommand DeleteCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // —— 绑定属性 ——

    private string _title = string.Empty;
    public string Title { get => _title; private set { if (_title != value) { _title = value; OnPropertyChanged(); } } }

    private string _url = string.Empty;
    public string Url { get => _url; private set { if (_url != value) { _url = value; OnPropertyChanged(); } } }

    private string _description = string.Empty;
    public string Description { get => _description; private set { if (_description != value) { _description = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDescription)); } } }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    private string _pathText = string.Empty;
    public string PathText { get => _pathText; private set { if (_pathText != value) { _pathText = value; OnPropertyChanged(); } } }

    private string _updatedAtText = "—";
    public string UpdatedAtText { get => _updatedAtText; private set { if (_updatedAtText != value) { _updatedAtText = value; OnPropertyChanged(); } } }

    private string _lastVisitedText = "从未";
    public string LastVisitedText { get => _lastVisitedText; private set { if (_lastVisitedText != value) { _lastVisitedText = value; OnPropertyChanged(); } } }

    private string _visitCountText = "0 次";
    public string VisitCountText { get => _visitCountText; private set { if (_visitCountText != value) { _visitCountText = value; OnPropertyChanged(); } } }

    private string _createdAtText = "—";
    public string CreatedAtText { get => _createdAtText; private set { if (_createdAtText != value) { _createdAtText = value; OnPropertyChanged(); } } }

    private string _idText = "";
    /// <summary>链接 ID（信息卡展示 + 复制）。</summary>
    public string IdText { get => _idText; private set { if (_idText != value) { _idText = value; OnPropertyChanged(); } } }

    private System.Windows.Media.Imaging.BitmapImage? _favicon;
    public System.Windows.Media.Imaging.BitmapImage? Favicon
    {
        get => _favicon;
        private set { if (_favicon != value) { _favicon = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFavicon)); } }
    }
    public bool HasFavicon => Favicon != null;

    private static string Fmt(DateTime dt) => dt.Year <= 1 ? "—" : dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>打开详情页：加载链接数据并记录一次访问。</summary>
    public Task LoadAsync(string linkId) => LoadCoreAsync(linkId, recordVisit: true);

    /// <summary>编辑器保存后静默刷新（不重复记录访问）。</summary>
    public Task ReloadIfOpenAsync() => _linkId == null ? Task.CompletedTask : LoadCoreAsync(_linkId, recordVisit: false);

    private async Task LoadCoreAsync(string linkId, bool recordVisit)
    {
        var gen = ++_generation;
        _linkId = linkId;
        try
        {
            // 打开详情 = 一次查看。**必须先记账、再读取展示数据**，否则页面显示的是"上一次"的
            // 「最后查看 / 查看次数」，用户要退出详情页（Back 会重新读取）才看到 +1。
            // 另外：这里必须 await，不能丢到 Task.Run —— 后台线程和 UI 线程并发使用同一个
            // EF DbContext 是非线程安全的（UI 线程同时可能因事件防抖在刷新列表）。
            if (recordVisit)
            {
                try { await _client.LinkVisitRecordAsync(linkId); }
                catch { /* 链接可能已不存在：交给下面的读取判定 */ }
                if (gen != _generation) return;
            }

            var link = (await _client.LinkAllAsync()).FirstOrDefault(l => l.LinkId == linkId);
            if (link == null || gen != _generation) { Close(); return; }

            Title = link.Title;
            Url = link.Url;
            Description = link.Description ?? "";
            PathText = _host.GetFolderPathDisplay(link.ListId);
            UpdatedAtText = Fmt(link.UpdatedAt);
            CreatedAtText = Fmt(link.CreatedAt);
            LastVisitedText = link.LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未";
            VisitCountText = $"{link.VisitCount} 次";
            IdText = link.LinkId;
            Favicon = string.IsNullOrEmpty(link.FaviconUrl) ? null : FaviconService.LoadFromCache(link.FaviconUrl);
        }
        catch (Exception ex)
        {
            Logger.Error("链接详情页加载失败", ex);
        }
    }

    private void OpenWebsite() => _ = OpenWebsiteAsync();

    /// <summary>打开网站 = 又一次查看：先记账、再重新读取，页面上的统计立刻反映这一次。</summary>
    private async Task OpenWebsiteAsync()
    {
        if (string.IsNullOrEmpty(Url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true });
        }
        catch { /* 无法打开时保持静默 */ }

        if (_linkId == null) return;
        try
        {
            await _client.LinkVisitRecordAsync(_linkId);
            await ReloadIfOpenAsync(); // 不重复计数，只把「含本次」的最新统计取回来
        }
        catch (Exception ex)
        {
            Logger.Error("记录访问失败", ex);
        }
    }

    private void CopyUrl()
    {
        try { if (!string.IsNullOrEmpty(Url)) System.Windows.Clipboard.SetText(Url); } catch { }
    }

    private void Edit()
    {
        if (_linkId == null) return;
        _host.OpenEditorForEdit(_linkId); // 整页编辑器覆盖在详情页之上；保存后经 ReloadIfOpenAsync 回写
    }

    private void CopyId()
    {
        try { if (!string.IsNullOrEmpty(IdText)) System.Windows.Clipboard.SetText(IdText); } catch { }
    }

    /// <summary>删除链接（移入回收站）：与右侧栏删除行为一致，删除后关闭详情页并刷新列表。</summary>
    private async Task DeleteAsync()
    {
        if (_linkId == null) return;
        try
        {
            await _client.LinkTrashAsync(_linkId);
            Close();
            await _host.RefreshAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("链接详情页删除失败", ex);
        }
    }

    /// <summary>
    /// 「返回」列表：关闭详情页并在原目录原地刷新（保留原选中项）。
    /// 详情页打开时已在内核记入一次「查看」——链接与上级文件夹的
    /// 「最后查看 / 查看次数」都已沿父链刷新，这里让它们立刻反映到列表与右侧栏；
    /// 「最后更新」不受查看影响，仍严格由内容变动驱动。
    /// </summary>
    private async Task BackAsync()
    {
        var keepId = _linkId;
        Close();
        await _host.RefreshAsync(preserveSelectionId: keepId);
    }

    public void Close()
    {
        _linkId = null;
        _generation++;
        _host.CloseDetailPage();
    }
}
