using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Services;
using LinkPocket.I18n;

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
        // 一键解析（补缺）+ 三枚单字段抓取（只覆盖该字段）：共用同一次网络抓取，差别只在"写哪几个字段"
        FetchMetadataCommand = new RelayCommand(() => _ = FetchFieldAsync(FetchField.All), CanFetch);
        FetchTitleCommand = new RelayCommand(() => _ = FetchFieldAsync(FetchField.Title), CanFetch);
        FetchIconCommand = new RelayCommand(() => _ = FetchFieldAsync(FetchField.Icon), CanFetch);
        FetchDescriptionCommand = new RelayCommand(() => _ = FetchFieldAsync(FetchField.Description), CanFetch);
        ClearFaviconCommand = new RelayCommand(ClearFavicon, () => CanClearFavicon);   // 与属性口径一致（含 IsFetching）
        if (IsEditMode) _ = LoadForEditAsync();
    }

    /// <summary>抓取类命令的共用可用性：加载/解析中禁用，URL 非空才发请求。</summary>
    private bool CanFetch() => !IsLoading && !IsFetching && !string.IsNullOrWhiteSpace(Url);

    /// <summary>新建模式：在浏览模块当前目录创建（null = 根级）。</summary>
    public LinkEditorViewModel(EngineClient client, BrowserViewModel host, string? initialListId)
        : this(client, host, null, initialListId) { }

    /// <summary>编辑模式工厂：预填链接数据。</summary>
    public static LinkEditorViewModel ForEdit(EngineClient client, BrowserViewModel host, string linkId)
        => new(client, host, linkId, null);

    public bool IsEditMode => _editLinkId != null;
    public LocValue TitleText => IsEditMode ? Loc.K("editor.title.edit") : Loc.K("editor.title.create");
    public string TitleIcon => IsEditMode ? "pencil-outline" : "link-plus";

    private string _url = string.Empty;
    public string Url
    {
        get => _url;
        set
        {
            if (_url != value)
            {
                var prevEmpty = string.IsNullOrWhiteSpace(_url);
                var nextEmpty = string.IsNullOrWhiteSpace(value);
                _url = value;
                OnPropertyChanged();
                ClearError();
                // URL 变更 = 目标站点变化：旧解析的 favicon 与「清除」标志一并作废（防张冠李戴写错图标）
                _pendingFaviconUrl = null;
                _clearFavicon = false;
                // CanExecute 只依赖「空/非空」布尔；无错误时 ClearError 不触发通知 →
                // 空↔非空翻转必须显式重评估命令（否则按钮可用态不刷新）
                if (prevEmpty != nextEmpty) CommandRefresh.Request();
            }
        }
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
        set { if (_description != value) { _description = value; OnPropertyChanged(); ClearError(); } }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnPropertyChanged();
                // CanClearFavicon 含 !IsLoading：预填时 favicon 在加载中置位（那一刻重评估为 false），
                // 加载收尾若不重评估，清除按钮的可见性会**永久停在 false**（实测：取消再进编辑后按钮消失）。
                OnPropertyChanged(nameof(CanClearFavicon));
                CommandRefresh.Request();
            }
        }
    }

    private LocValue? _error;

    /// <summary>校验/加载失败的错误行（键 + 参数；引擎原文只进日志，不上屏）。</summary>
    public LocValue? Error
    {
        get => _error;
        set { if (!Equals(_error, value)) { _error = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); CommandRefresh.Request(); } }
    }
    public bool HasError => Error is { IsEmpty: false };
    private void ClearError() { if (HasError) Error = null; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand FetchMetadataCommand { get; }
    public ICommand FetchTitleCommand { get; }
    public ICommand FetchIconCommand { get; }
    public ICommand FetchDescriptionCommand { get; }
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
        private set
        {
            if (_favicon != value)
            {
                _favicon = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFavicon));
                OnPropertyChanged(nameof(CanClearFavicon));   // 图标有无驱动清除按钮可用性
                CommandRefresh.Request();
            }
        }
    }
    public bool HasFavicon => Favicon != null;

    private bool _isFetching;
    /// <summary>正在自动解析网站元数据（URL 卡片按钮转圈，保存按钮同步禁用）。</summary>
    public bool IsFetching
    {
        get => _isFetching;
        set { if (_isFetching != value) { _isFetching = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanClearFavicon)); CommandRefresh.Request(); } }
    }

    /// <summary>抓取中的字段（"all" / "title" / "icon" / "description"；null = 空闲）——四枚按钮的转圈态据此投影。</summary>
    private string? _fetchingField;

    /// <summary>正在整页一键解析（URL 卡片按钮转圈）。</summary>
    public bool IsFetchingAll => _fetchingField == "all";
    /// <summary>正在单独抓取标题（标题按钮转圈）。</summary>
    public bool IsFetchingTitle => _fetchingField == "title";
    /// <summary>正在单独抓取图标（图标徽标按钮转圈）。</summary>
    public bool IsFetchingIcon => _fetchingField == "icon";
    /// <summary>正在单独抓取描述（描述按钮转圈）。</summary>
    public bool IsFetchingDescription => _fetchingField == "description";

    private void SetFetchingField(string? field)
    {
        _fetchingField = field;
        OnPropertyChanged(nameof(IsFetchingAll));
        OnPropertyChanged(nameof(IsFetchingTitle));
        OnPropertyChanged(nameof(IsFetchingIcon));
        OnPropertyChanged(nameof(IsFetchingDescription));
    }

    /// <summary>是否可清除图标：仅编辑模式、当前确实有图标（原链接带图标或已解析出图标）且不在加载/解析中
    /// （无图标时按钮不应出现——"清除不存在的图标"是无意义操作；清除后图标置空，按钮随之隐藏）。</summary>
    public bool CanClearFavicon => IsEditMode && HasFavicon && !IsLoading && !IsFetching;

    /// <summary>抓取作用域：All = 整页一键解析（只补缺、不覆盖用户输入）；其余 = 单字段抓取（用结果**覆盖**该字段）。</summary>
    private enum FetchField { All, Title, Icon, Description }

    /// <summary>抓取共用实现（一键补缺 / 单字段覆盖都走这一条）：
    /// 网络抓取与"URL 快照校验、取消即丢弃"同口径；差别只在写哪几个字段。
    /// 单字段的语义 = 用户的明确意图（"只更新这一个"）：抓到就**覆盖**（站点标题改了要刷新）；
    /// 没抓到 → 保持原值 + 错误行说明（绝不因为"没抓到"把用户输入清掉）。</summary>
    private async Task FetchFieldAsync(FetchField field)
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        try
        {
            IsFetching = true;
            SetFetchingField(field switch
            {
                FetchField.Title => "title",
                FetchField.Icon => "icon",
                FetchField.Description => "description",
                _ => "all",
            });
            Error = null;   // 新一次抓取清掉上一次的错误行
            var url = Url.Trim();   // URL 快照——解析期间 URL 被用户修改则丢弃旧结果（防 A 的结果写进 B）
            var meta = await _client.LinkMetadataFetchAsync(url);
            if (!_host.IsEditorPageOpen) return;   // 解析耗时期间用户已取消：丢弃结果，不写已废弃的 VM（并发覆盖）
            if (!string.Equals(url, Url.Trim(), StringComparison.Ordinal)) return;   // URL 已改 → 旧解析结果作废
            if (meta == null) { Error = Loc.K("editor.resolveFailed"); return; }

            switch (field)
            {
                case FetchField.All:
                    if (!string.IsNullOrWhiteSpace(meta.Title) && string.IsNullOrWhiteSpace(LinkTitle))
                        LinkTitle = meta.Title.Trim();
                    if (!string.IsNullOrWhiteSpace(meta.Description) && string.IsNullOrWhiteSpace(Description))
                        Description = meta.Description.Trim();
                    if (!string.IsNullOrWhiteSpace(meta.FaviconUrl))
                        await ApplyFaviconAsync(meta.FaviconUrl);

                    // 一键解析只补缺、不覆盖用户输入，但**补完仍空着的就不能装作成功**：
                    // 只有图标解析出来、标题描述空着且不吭声，用户会读成"解析坏了"。
                    // 判据 = 补完后的实际状态（用户自己填过的字段不算失败）；只有描述为空不报——
                    // 页面没写 description 是常态，不该每次都弹一条错误。
                    var noTitle = string.IsNullOrWhiteSpace(LinkTitle);
                    var noDescription = string.IsNullOrWhiteSpace(Description);
                    if (noTitle && noDescription)
                        Error = meta.BlockedByVerification
                            ? Loc.K("editor.verificationRequired")
                            : Loc.K("editor.notFound.titleDescription");
                    else if (noTitle)
                        Error = Loc.K("editor.notFound.title");
                    break;

                case FetchField.Title:
                    if (!string.IsNullOrWhiteSpace(meta.Title)) LinkTitle = meta.Title.Trim();
                    else Error = Loc.K("editor.notFound.title");
                    break;

                case FetchField.Description:
                    if (!string.IsNullOrWhiteSpace(meta.Description)) Description = meta.Description.Trim();
                    else Error = Loc.K("editor.notFound.description");
                    break;

                case FetchField.Icon:
                    if (!string.IsNullOrWhiteSpace(meta.FaviconUrl)) await ApplyFaviconAsync(meta.FaviconUrl);
                    else Error = Loc.K("editor.notFound.icon");
                    break;
            }
        }
        catch (Exception ex)
        {
            // 界面文案承诺"详情见日志"，这里就必须真的把详情落下去：
            // 曾经是个光秃 catch（异常到手即弃），于是日志里空无一物、无从排查（meoai 那次就是这么耗掉的）。
            LpLog.Error($"metadata auto-parse failed: {Url}", ex);
            Error = Loc.K("editor.err.autoParseFailed");
        }
        finally
        {
            IsFetching = false;
            SetFetchingField(null);
        }
    }

    /// <summary>把解析出的图标地址设为待写入值、复位「清除」标志，并拉取到磁盘缓存在头部卡片实时预览
    /// （失败时回落 /favicon.ico 与聚合源）。</summary>
    private async Task ApplyFaviconAsync(string faviconUrl)
    {
        _pendingFaviconUrl = faviconUrl;
        _clearFavicon = false;
        var favUrl = faviconUrl;
        Favicon = await Task.Run(async () =>
        {
            try
            {
                if (!await LinkPocket.Contracts.FaviconCache.EnsureCachedAsync(favUrl))
                    LpLog.Error($"favicon download failed (including fallback): {favUrl}", null);
                return Services.FaviconService.LoadFromCache(favUrl);
            }
            catch { return Services.FaviconService.LoadFromCache(favUrl); }
        });
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
            if (!_host.IsEditorPageOpen) return;   // 加载期间用户已取消：丢弃结果，不再写已废弃的 VM
            // 仅当字段仍为空才填充：用户此刻的输入优先，预填只补缺
            if (string.IsNullOrEmpty(Url)) Url = link.Url;
            if (string.IsNullOrWhiteSpace(LinkTitle)) LinkTitle = link.Title;
            if (string.IsNullOrEmpty(Description)) Description = link.Description ?? "";
            // 已有 favicon 时头部直接展示真实图标（与详情页一致）
            if (!string.IsNullOrEmpty(link.FaviconUrl))
            {
                _pendingFaviconUrl = link.FaviconUrl;
                var favUrl = link.FaviconUrl;
                // async lambda + await，不在 Task.Run 里同步 GetAwaiter().GetResult()（与 FetchMetadataAsync 同构）
                Favicon = await Task.Run(async () =>
                {
                    try { await LinkPocket.Contracts.FaviconCache.EnsureCachedAsync(favUrl); } catch { /* 图标抓取失败：界面有地球占位符，不影响编辑 */ }
                    return Services.FaviconService.LoadFromCache(favUrl);
                });
            }
        }
        catch (Exception)
        {
            Error = Loc.K("editor.err.loadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SaveAsync()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Url)) { Error = Loc.K("editor.urlRequired"); return; }
        if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        { Error = Loc.K("editor.urlInvalid"); return; }
        if (string.IsNullOrWhiteSpace(LinkTitle)) { Error = Loc.K("editor.titleRequired"); return; }

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

            IsLoading = false;   // 保存完成即收加载态（页面随后关闭，属性通知变窄窗口）
            _host.CloseEditorPage();
            // 立即真刷新（不等 300ms 事件防抖）：新建/编辑后的行随查询结果落地。
            // 不靠事件链"稍后对齐"——用户点保存之后应当立刻在列表里看到结果。
            await _host.RefreshPreservingSelectionAsync();
            await _host.DetailPage.ReloadIfOpenAsync(); // 详情页若在编辑器下层，同步刷新
        }
        catch (Exception)
        {
            Error = Loc.K("editor.err.saveFailed");
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
