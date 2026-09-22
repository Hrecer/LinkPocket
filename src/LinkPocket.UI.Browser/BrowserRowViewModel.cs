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

    public string Name { get; }

    /// <summary>仅书签行有值。</summary>
    public string? Url { get; init; }

    /// <summary>仅文件夹行有值：该文件夹下所有链接总数（递归，内核计算）。</summary>
    public int LinkCount { get; init; }

    public DateTime ModifiedAt { get; init; }

    /// <summary>创建时间（文件夹由内核维护；链接为其自身创建时间）。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>最后查看时间（内核维护：文件夹为子孙链接被查看时沿父链刷新；链接为自身被查看时间）。</summary>
    public DateTime? LastViewedAt { get; init; }

    /// <summary>查看次数（内核维护：文件夹为子树上溯增量；链接为自身访问次数）。</summary>
    public int ViewCount { get; init; }

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
    public void SetFavicon(BitmapImage? favicon)
    {
        if (Favicon != favicon)
        {
            Favicon = favicon;
            OnPropertyChanged();
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
