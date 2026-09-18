using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>
/// 链接编辑器视图模型（新建 / 编辑共用，整页覆盖层形态，与链接详情页对齐）。
/// 新建：固定在浏览模块当前目录创建（不再选择所属目录）；
/// 编辑：仅更新 URL/标题/描述，不移动所属目录（LinkUpdateAsync 不传 listId 即保持原目录）。
/// </summary>
public class LinkEditorViewModel : INotifyPropertyChanged
{
    private readonly BrowserViewModel _host;
    private readonly string? _editLinkId;
    private readonly string? _createListId;
    private readonly EngineClient _client;

    private LinkEditorViewModel(EngineClient client, BrowserViewModel host, string? editLinkId, string? createListId)
    {
        _client = client;
        _host = host;
        _editLinkId = editLinkId;
        _createListId = createListId;
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !IsLoading && !IsFetching && !HasError);
        CancelCommand = new RelayCommand(() => _host.CloseEditorPage());
        FetchMetadataCommand = new RelayCommand(() => _ = FetchMetadataAsync(),
            () => !IsLoading && !IsFetching && !string.IsNullOrWhiteSpace(Url));
        ClearFaviconCommand = new RelayCommand(ClearFavicon, () => CanClearFavicon);   // 与属性口径一致（含 IsFetching）
        if (IsEditMode) _ = LoadForEditAsync();
    }

    /// <summary>新建模式：在浏览模块当前目录创建（null = 根级）。</summary>
    public LinkEditorViewModel(EngineClient client, BrowserViewModel host, string? initialListId)
        : this(client, host, null, initialListId) { }

    /// <summary>编辑模式工厂：预填链接数据。</summary>
    public static LinkEditorViewModel ForEdit(EngineClient client, BrowserViewModel host, string linkId)
        => new(client, host, linkId, null);

    public bool IsEditMode => _editLinkId != null;
    public string TitleText => IsEditMode ? "编辑链接" : "新建链接";
    public string TitleIcon => IsEditMode ? "pencil-outline" : "link-plus";

    private string _url = string.Empty;
    public string Url
    {
        get => _url;
        set { if (_url != value) { _url = value; OnPropertyChanged(); ClearError(); } }
    }

    private string _linkTitle = string.Empty;
    public string LinkTitle
    {
        get => _linkTitle;
        set { if (_linkTitle != value) { _linkTitle = value; OnPropertyChanged(); ClearError(); } }
    }

    private string _description = string.Empty;
    public string Description
    {
        get => _description;
        set { if (_description != value) { _description = value; OnPropertyChanged(); } }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { if (_isLoading != value) { _isLoading = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        set { if (_error != value) { _error = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool HasError => !string.IsNullOrEmpty(Error);
    private void ClearError() { if (HasError) Error = null; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand FetchMetadataCommand { get; }
    public ICommand ClearFaviconCommand { get; }

    // —— 图标/元数据 ——
    /// <summary>自动解析得到的 favicon 地址（保存时随链接写入内核缓存）。</summary>
    private string? _pendingFaviconUrl;
    /// <summary>编辑模式标记「清除图标」：保存时写入空 favicon。</summary>
    private bool _clearFavicon;

    private System.Windows.Media.Imaging.BitmapImage? _favicon;
    /// <summary>头部卡片展示的图标：编辑模式为已有 favicon，解析后为预览图，清除后回落默认图标。</summary>
    public System.Windows.Media.Imaging.BitmapImage? Favicon
    {
        get => _favicon;
        private set { if (_favicon != value) { _favicon = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFavicon)); } }
    }
    public bool HasFavicon => Favicon != null;

    private bool _isFetching;
    /// <summary>正在自动解析网站元数据（URL 卡片按钮转圈，保存按钮同步禁用）。</summary>
    public bool IsFetching
    {
        get => _isFetching;
        set { if (_isFetching != value) { _isFetching = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanClearFavicon)); CommandManager.InvalidateRequerySuggested(); } }
    }

    /// <summary>是否可清除图标：仅编辑模式且当前有图标（原链接带图标或已解析出图标）。</summary>
    public bool CanClearFavicon => IsEditMode && !IsLoading && !IsFetching;

    /// <summary>自动解析：抓取网站标题/描述/图标，空缺字段自动填充，不覆盖用户已输入内容。</summary>
    private async Task FetchMetadataAsync()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        try
        {
            IsFetching = true;
            var meta = await _client.LinkMetadataFetchAsync(Url.Trim());
            if (meta == null) { Error = "未能解析该网站（请检查 URL 是否可访问）"; return; }

            if (!string.IsNullOrWhiteSpace(meta.Title) && string.IsNullOrWhiteSpace(LinkTitle))
                LinkTitle = meta.Title.Trim();
            if (!string.IsNullOrWhiteSpace(meta.Description) && string.IsNullOrWhiteSpace(Description))
                Description = meta.Description.Trim();

            if (!string.IsNullOrWhiteSpace(meta.FaviconUrl))
            {
                _pendingFaviconUrl = meta.FaviconUrl;
                _clearFavicon = false;
                // 拉取图标到磁盘缓存并在头部卡片实时预览；失败时回落 /favicon.ico 与聚合源
                var favUrl = meta.FaviconUrl;
                Favicon = await Task.Run(async () =>
                {
                    try
                    {
                        if (!await Services.FaviconStore.EnsureCachedAsync(favUrl))
                            Services.Logger.Error($"favicon 下载失败（含降级尝试）: {favUrl}", null);
                        return Services.FaviconService.LoadFromCache(favUrl);
                    }
                    catch { return Services.FaviconService.LoadFromCache(favUrl); }
                });
            }
        }
        catch (Exception ex)
        {
            Error = $"自动解析失败: {ex.Message}";
        }
        finally
        {
            IsFetching = false;
        }
    }

    /// <summary>清除图标（仅编辑模式）：保存时写入空 favicon 覆盖原图标，头部立即回落默认图标。</summary>
    private void ClearFavicon()
    {
        _clearFavicon = true;
        _pendingFaviconUrl = null;
        Favicon = null;
    }

    /// <summary>编辑模式预填。加载期间置 IsLoading（禁用保存/解析）；预填只填空字段——
    /// 慢加载完成不得覆盖用户已输入的内容（消除 fire-and-forget 与用户输入的竞态）。
    /// 取数走 LinkGetAsync 单点查询，不再全量拉取后 FirstOrDefault。</summary>
    private async Task LoadForEditAsync()
    {
        IsLoading = true;
        try
        {
            var link = await _client.LinkGetAsync(_editLinkId!);
            if (link == null) { _host.CloseEditorPage(); return; }
            // 仅当字段仍为空才填充：用户此刻的输入优先，预填只补缺
            if (string.IsNullOrEmpty(Url)) Url = link.Url;
            if (string.IsNullOrWhiteSpace(LinkTitle)) LinkTitle = link.Title;
            if (string.IsNullOrEmpty(Description)) Description = link.Description ?? "";
            // 已有 favicon 时头部直接展示真实图标（与详情页一致）
            if (!string.IsNullOrEmpty(link.FaviconUrl))
            {
                _pendingFaviconUrl = link.FaviconUrl;
                var favUrl = link.FaviconUrl;
                Favicon = await Task.Run(() =>
                {
                    try { Services.FaviconStore.EnsureCachedAsync(favUrl).GetAwaiter().GetResult(); } catch { }
                    return Services.FaviconService.LoadFromCache(favUrl);
                });
            }
        }
        catch (Exception ex)
        {
            Error = $"加载链接数据失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SaveAsync()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Url)) { Error = "URL 不能为空"; return; }
        if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        { Error = "请输入有效的 URL（http/https）"; return; }
        if (string.IsNullOrWhiteSpace(LinkTitle)) { Error = "标题不能为空"; return; }

        try
        {
            IsLoading = true;
            if (IsEditMode)
            {
                // 不传 listId → 保持原所属目录不变；faviconUrl：清除标志写空串，有解析结果写新值，否则不动
                var favicon = _clearFavicon ? "" : _pendingFaviconUrl;
                await _client.LinkUpdateAsync(_editLinkId!, url: Url.Trim(), title: LinkTitle.Trim(),
                    description: Description.Trim(), faviconUrl: favicon);
            }
            else
            {
                // 原文保存（与编辑分支一致）：绝不二次解码——%23/%2F/%3F 等合法编码在
                // 校验时已验证为合法 http(s) URL，UnescapeDataString 会破坏其语义
                await _client.LinkCreateAsync(
                    url: Url.Trim(),
                    title: LinkTitle.Trim(),
                    description: string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
                    listId: _createListId,
                    isImportant: false,
                    autoFetchMetadata: false,
                    faviconUrl: _pendingFaviconUrl);
            }

            _host.CloseEditorPage();
            // 列表刷新交给后端 links.changed 事件统一驱动（MainViewModel 300ms 防抖 → RefreshPreservingSelectionAsync）。
            // 这里不再显式 RefreshAsync：内核写操作必然推事件，显式刷新会和事件刷新叠成"外面刷新两次"。
            await _host.DetailPage.ReloadIfOpenAsync(); // 详情页若在编辑器下层，同步刷新
        }
        catch (Exception ex)
        {
            Error = $"保存失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
