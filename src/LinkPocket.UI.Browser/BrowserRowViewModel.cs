using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using LinkPocket.Contracts;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels;

/// <summary>
/// 资源管理器视图的行视图模型（P4）：一行 = 一个子文件夹或一个书签。
/// IsSelected 由绑定驱动选中态（ItemContainerStyle / DataTrigger），禁止代码建控件改颜色。
/// </summary>
public class BrowserRowViewModel : INotifyPropertyChanged
{
    public BrowserRowViewModel(string id, bool isFolder, string name)
    {
        Id = id;
        IsFolder = isFolder;
        Name = name;
    }

    /// <summary>文件夹 ID 或链接 ID。</summary>
    public string Id { get; }

    public bool IsFolder { get; }

    private string _name = string.Empty;
    /// <summary>可变（带通知）：**乐观改名**用它立即反映新名字（引擎已落库、事件刷新稍后对齐）——
    /// 万行目录下"回车 → 等 300ms 防抖 + 整表重建才看到新名字"是实测的第二次等待。</summary>
    public string Name
    {
        get => _name;
        private set
        {
            if (string.Equals(_name, value, StringComparison.Ordinal)) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    /// <summary>乐观改名（仅界面投影；数据以引擎为准，事件刷新终会对齐）。</summary>
    public void UpdateName(string name) => Name = name;

    // ── 展示字段：**可原地更新**（差分刷新用）────────────────────────────────
    // 口径：行序列（Id + 顺序）不变时，刷新走 `ApplyFrom` **原地写回**而不是换新对象 ——
    // 换新对象要整表 ReplaceAll（一次 Reset ⇒ 视口内行容器连同右键菜单/布局行为全部重建，
    // 实测大目录一次 64ms 布局）。所以这些字段必须可写且**逐字段发通知**（只通知真变了的）。
    // 写入口只有两个：对象初始化器（构造）与 `ApplyFrom`（刷新）—— `internal set` 不对外开放。

    private string? _url;
    /// <summary>仅书签行有值。</summary>
    public string? Url
    {
        get => _url;
        internal set { if (string.Equals(_url, value, StringComparison.Ordinal)) return; _url = value; OnPropertyChanged(); }
    }

    private int _linkCount;
    /// <summary>仅文件夹行有值：该文件夹下所有链接总数（递归，内核计算）。</summary>
    public int LinkCount
    {
        get => _linkCount;
        internal set { if (_linkCount == value) return; _linkCount = value; OnPropertyChanged(); }
    }

    // ⚠️ 下列原始字段的 setter 必须**连派生的展示投影一起通知**（如 ModifiedAt → ModifiedText）：
    // 列表列绑定的是 `<c>{loc:FitValue ModifiedText}</c>` 这一族派生属性，不是原始字段；
    // 差分刷新走 ApplyFrom 原地写回时不换对象、不发 Reset，只靠这里的逐字段通知驱动那一格重画 ——
    // 只通知原始字段名的话绑定名对不上，单元格永远停在旧值（整表替换那一路因为换新对象才"看起来正常"）。

    private DateTime _modifiedAt;
    public DateTime ModifiedAt
    {
        get => _modifiedAt;
        internal set
        {
            if (_modifiedAt == value) return;
            _modifiedAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModifiedText));
        }
    }

    private DateTime _createdAt;
    /// <summary>创建时间（文件夹由内核维护；链接为其自身创建时间）。</summary>
    public DateTime CreatedAt
    {
        get => _createdAt;
        internal set
        {
            if (_createdAt == value) return;
            _createdAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CreatedText));
        }
    }

    private DateTime? _lastViewedAt;
    /// <summary>最后查看时间（内核维护：文件夹为子孙链接被查看时沿父链刷新；链接为自身被查看时间）。</summary>
    public DateTime? LastViewedAt
    {
        get => _lastViewedAt;
        internal set
        {
            if (_lastViewedAt == value) return;
            _lastViewedAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastViewedText));
            OnPropertyChanged(nameof(LastViewedAdaptive));
            OnPropertyChanged(nameof(LastViewedCopy));
        }
    }

    private int _viewCount;
    /// <summary>查看次数（内核维护：文件夹为子树上溯增量；链接为自身访问次数）。</summary>
    public int ViewCount
    {
        get => _viewCount;
        internal set
        {
            if (_viewCount == value) return;
            _viewCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ViewCountText));
        }
    }

    /// <summary>
    /// **原地更新**：把另一份（同 Id、同位置的）行的展示字段写回本行 —— 只写真的变了的那些，
    /// 每个变了的值各发一次属性通知（绑它的那一格自己重画，不动集合、不动容器）。
    /// </summary>
    /// <remarks>
    /// 与"整表替换"的分工：本方法**只**用于"行序列（Id + 顺序）完全一致"的刷新；
    /// 行的增减 / 重排 / 换目录仍走 <c>Rows.ReplaceAll</c>（那时容器的对应关系已经变了，必须重建）。
    /// </remarks>
    internal void ApplyFrom(BrowserRowViewModel fresh)
    {
        Name = fresh.Name;
        Url = fresh.Url;
        LinkCount = fresh.LinkCount;
        ModifiedAt = fresh.ModifiedAt;
        CreatedAt = fresh.CreatedAt;
        LastViewedAt = fresh.LastViewedAt;
        ViewCount = fresh.ViewCount;
        IsCut = fresh.IsCut;
        SetFavicon(fresh.Favicon);   // 值相同不发通知（见 SetFavicon）
    }

    /// <summary>
    /// 两个行序列是否**同一批行**（条数、逐项 Id 与类型都一致 = 只有展示字段可能变）——
    /// 差分刷新的判据：真时走 <see cref="ApplyFrom"/> 原地更新，假时走整表替换。
    /// </summary>
    public static bool SameIdentity(IReadOnlyList<BrowserRowViewModel>? a, IReadOnlyList<BrowserRowViewModel>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id || a[i].IsFolder != b[i].IsFolder) return false;
        return true;
    }

    /// <summary>
    /// 「最后更新」列：未设置时间（默认值）时显示占位符 <c>—</c>，避免出现 0001-01-01。
    /// <b>两个长度形态</b>（<see cref="UiClock.Text"/>）：列宽是冻结几何，英文日期比中文宽约 25%，
    /// 放不下时换短式（去年份）而不是截断——截断的日期是错的日期。
    /// </summary>
    public LocText ModifiedText => ModifiedAt.Year <= 1 ? Dash : UiClock.Text(ModifiedAt);

    /// <summary>「最后查看」列（时间戳；无值时由 <see cref="LastViewedCopy"/> 画「从未」）。</summary>
    public LocText LastViewedText => LastViewedAt.HasValue ? UiClock.Text(LastViewedAt.Value) : LocText.Empty;

    /// <summary>
    /// 「最后查看」列的自适应形态：<b>有值时是时间戳、无值时回落到「从未」</b>。
    /// </summary>
    /// <remarks>
    /// 存在的理由：无值的行要让自适应机制去画「从未」这句**文案**（它可能也需要短式），
    /// 而那个哨兵句在 <see cref="LastViewedCopy"/> 里、按语言取词。
    /// 原先 XAML 把时间戳与「从未」拆成两套显示机制（自适应 + 静态），
    /// 于是无值的那些行不受自适应管——两种语言下几何可能不一致。
    /// </remarks>
    public LocText LastViewedAdaptive => LastViewedAt.HasValue ? UiClock.Text(LastViewedAt.Value) : LastViewedCopyText;

    /// <summary>「从未」哨兵的 <see cref="LocText"/> 形态（无值的「最后查看」列用；短式由表里有没有 <c>#short</c> 决定）。</summary>
    private static LocText LastViewedCopyText => LocText.Key("clock.never", "clock.never" + Loc.ShortSuffix);

    /// <summary>「最后查看」列的文案（无值时画「从未」）。</summary>
    public LocValue LastViewedCopy => LastViewedAt.HasValue ? LocValue.Empty : Loc.K("clock.never");

    /// <summary>
    /// 「查看次数」列（<b>结构性单元格</b>：走自适应通道画，与日期列同一套）。
    /// </summary>
    /// <remarks>
    /// 类型必须是 <see cref="LocText"/>：它绑定到 <c>{loc:FitValue}</c>，而该通道的解析器只接受
    /// <see cref="LocText"/>（<see cref="LocValue"/> 会让投影拿不到文案 ⇒ 整格一个字都不画）。
    /// </remarks>
    public LocText ViewCountText => LocText.Of(Loc.K("count.viewsN", ViewCount));

    /// <summary>「创建时间」列（两个长度形态，同 <see cref="ModifiedText"/>）。</summary>
    public LocText CreatedText => CreatedAt.Year <= 1 ? Dash : UiClock.Text(CreatedAt);

    /// <summary>无时间时的占位符。中文与英文共用（<c>—</c> 不是文字，是符号）。</summary>
    private static LocText Dash => LocText.Of(LocValue.Literal("—"));

    /// <summary>仅书签行有值：前端解码后的 favicon（可能为 null，UI 显示占位图标；磁盘缓存补拉后可更新）。</summary>
    public BitmapImage? Favicon { get; set; }

    /// <summary>磁盘缓存补拉完成后更新 favicon 图像（带属性通知）。</summary>
    /// <remarks>
    /// ⚠️ 通知名必须**显式给 <c>Favicon</c>**：本方法的 <c>OnPropertyChanged()</c> 走
    /// <see cref="CallerMemberNameAttribute"/>，不传参就发成方法名 <c>SetFavicon</c>，
    /// 而模板绑的是 <c>{Binding Favicon}</c> —— 名对不上，短式图标补拉回来永远画不上去。
    /// </remarks>
    public void SetFavicon(BitmapImage? favicon)
    {
        if (Favicon != favicon)
        {
            Favicon = favicon;
            OnPropertyChanged(nameof(Favicon));
        }
    }

    /// <summary>所属 VM：行内右键菜单经此绑定命令（ContextMenu 不在可视树，无法 RelativeSource 向上找）。</summary>
    public BrowserViewModel? Host { get; set; }

    /// <summary>
    /// 行选中态 = 宿主选中集合（<see cref="BrowserViewModel.IsSelectedId"/>）的纯投影。
    /// 不再把选中持久在行对象上——行对象会随 Rows 重建销毁，持久于行根本无法跨刷新存活；
    /// 选中唯一事实来源在 VM，行只是从集合读值。集合变化时由宿主调用 <see cref="InvalidateIsSelected"/>
    /// 通知绑定刷新，绝不在行自身写选中。
    /// </summary>
    public bool IsSelected => Host != null && Host.IsSelectedId(Id);

    /// <summary>选中集合变化后由宿主调用：仅重发本行 IsSelected 的绑定通知（值由集合投影）。</summary>
    public void InvalidateIsSelected() => OnPropertyChanged(nameof(IsSelected));

    /// <summary>
    /// 本行是否显示就地改名编辑框 = 宿主重命名会话状态的纯投影（与 <see cref="IsSelected"/> 同构）：
    /// 行对象随 Rows 重建销毁，所以"编辑中"绝不持久在行上，只从 VM 读值。
    /// </summary>
    public bool IsRenaming => Host != null && Host.IsRenamingId(Id, BrowserPane.Main);

    /// <summary>重命名会话变化后由宿主调用：仅重发本行 IsRenaming 的绑定通知（值由会话投影）。</summary>
    public void InvalidateIsRenaming() => OnPropertyChanged(nameof(IsRenaming));

    /// <summary>
    /// 本行是否为**拖拽悬停落点** = 宿主落点状态的纯投影（与 <see cref="IsSelected"/> 同构）：
    /// 拖拽过程中指针下的可落点行高亮；落点唯一事实来源在 VM（覆盖式更新），行对象随重建销毁、绝不持久本状态。
    /// </summary>
    public bool IsDropTarget => Host != null && Host.IsDropTargetRow(Id);

    /// <summary>落点状态变化后由宿主调用：仅重发本行 IsDropTarget 的绑定通知（值由落点投影）。</summary>
    public void InvalidateIsDropTarget() => OnPropertyChanged(nameof(IsDropTarget));

    /// <summary>
    /// 两个行序列是否**渲染等价**（ID 序列 + 全部展示字段逐项一致，含顺序；favicon 与剪切态也纳入）。
    /// 用途：刷新后判断"要不要换掉 Rows"——逐条 Clear/Add 会触发 N 次 CollectionChanged 并让视图整表重建，
    /// 内容没变时纯属白烧（低性能设备上切页偶发明显卡顿）。
    /// 口径：**宁可重建不可漏更新**——字段有任何差异即视为需要重建。
    /// </summary>
    public static bool SameSequence(IReadOnlyList<BrowserRowViewModel>? a, IReadOnlyList<BrowserRowViewModel>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Id != y.Id || x.IsFolder != y.IsFolder || x.Name != y.Name || x.Url != y.Url
                || x.LinkCount != y.LinkCount || x.ModifiedAt != y.ModifiedAt
                || x.CreatedAt != y.CreatedAt || x.LastViewedAt != y.LastViewedAt
                || x.ViewCount != y.ViewCount || x.IsCut != y.IsCut
                || !ReferenceEquals(x.Favicon, y.Favicon)) return false;
        }
        return true;
    }

    private bool _isCut;
    /// <summary>剪切态视觉（Ctrl+X）：行整体半透明，由绑定驱动。</summary>
    public bool IsCut
    {
        get => _isCut;
        set { if (_isCut != value) { _isCut = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>面包屑节点：可点击跳转。</summary>
public class BrowserCrumbViewModel
{
    public BrowserCrumbViewModel(string? folderId, string name)
    {
        FolderId = folderId;
        Name = name;
    }

    /// <summary>null 表示根目录（全部书签）。</summary>
    public string? FolderId { get; }
    public string Name { get; }

    /// <summary>是否为当前目录（面包屑最后一级，高亮显示）。</summary>
    public bool IsLast { get; init; }

    /// <summary>
    /// 是否为虚根段（<c>@root</c> / <c>@trash</c>）：根名是**随语言换的界面文案**（全部书签 ⇄ Bookmarks），
    /// 模板据此把它的宽度**冻在中文基线**（页面给 <c>RootSegmentWidth</c>）并接自适应通道；
    /// 其余段是用户文件夹名（用户数据）：宽度保持内容自适应、永不缩字号。
    /// </summary>
    public bool IsRoot => Name is BookmarkPath.RootToken or BookmarkPath.TrashToken;
}
