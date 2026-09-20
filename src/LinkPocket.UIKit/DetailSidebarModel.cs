using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace LinkPocket.ViewModels;

/// <summary>
/// 详情栏信息卡里的一行（数据驱动，单一数据源）：
/// 图标 + 标签 + 值，可选强调（Primary+SemiBold）/ 等宽（Consolas+截断）/ 复制按钮。
/// 行内容由使用方（浏览器页 / 搜索页 / 回收站）按选中对象构建，控件只负责渲染。
/// </summary>
public class DetailSidebarRow : INotifyPropertyChanged
{
    public string IconKind { get; init; } = "";
    public string Label { get; init; } = "";

    private string _value = "";
    /// <summary>行值：异步补拉时原位更新（INPC 通知，无需重建整行；值未变不发多余通知）。</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            OnPropertyChanged();
        }
    }

    /// <summary>强调值（如「11 个链接」→ Primary + SemiBold）。</summary>
    public bool IsAccent { get; init; }
    /// <summary>等宽值（如 ID → Consolas 11 + 省略截断）。</summary>
    public bool IsMono { get; init; }

    /// <summary>非空时该行末尾显示复制按钮。</summary>
    public ICommand? CopyCommand { get; init; }
    public string CopyToolTip { get; init; } = "复制";
    public bool HasCopy => CopyCommand != null;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 右侧详情栏的通用数据模型（与 Views/DetailSidebar 控件一一对应，解耦于任何页面）。
/// 四种状态：空占位 / 单选链接 / 单选文件夹 / 多选。
/// **动作面**（能力位 + 命令槽）来自 <see cref="ActionSurfaceModel"/>：各页只声明"有哪些动作"，
/// 界面由共享控件按能力位渲染，绝不按页复制界面或逻辑。
/// 页面特有逻辑（异步补拉、宿主命令）由子类或使用方注入。
/// </summary>
public class DetailSidebarModel : ActionSurfaceModel
{
    // —— 状态 ——
    public bool HasSelection { get; protected set; }
    public bool IsPlaceholder => !HasSelection;
    public bool IsMulti { get; protected set; }
    public bool IsSingle => HasSelection && !IsMulti;
    public bool IsFolder { get; protected set; }
    public bool IsLink => IsSingle && !IsFolder;

    /// <summary>
    /// 只读模式（如回收站详情栏）：语义标记——动作面**不做整卡隐藏**，而是由本页按能力位收窄
    /// （回收站只开 打开/详情 + 永久删除）。见 <see cref="ConfigureSidebarActionLabels"/>。
    /// </summary>
    public bool IsReadOnly { get; protected set; }

    /// <summary>
    /// **被删快照**语义（回收站详情栏打开）：条目身份图标灰化（`Text.Secondary` + `Opacity 0.55`），
    /// 与主栏行、左栏树的 `IsTrashed` 同一套呈现——回收站里的东西都是被删的，右栏不该是唯一一处"彩色的"。
    /// </summary>
    /// <remarks>
    /// 为什么是独立能力位而不是复用 <see cref="IsReadOnly"/>：只读页不止回收站（搜索页 / 智能列表 /
    /// 查重明细都是只读），但它们的条目**没有被删**，图标不该灰。判据必须落在"这条数据的语义"上。
    /// </remarks>
    public bool UseTrashedIconTone { get; protected set; }

    // —— 单选公共 ——
    public string DisplayName { get; protected set; } = "";
    public string IdText { get; protected set; } = "";
    public BitmapImage? Favicon { get; protected set; }
    public bool HasFavicon => Favicon != null;

    // —— 单选书签 ——
    public string UrlText { get; protected set; } = "";
    public string DescriptionText { get; protected set; } = "";
    /// <summary>描述卡是否显示（链接与单元通用；空描述不显示）。</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(DescriptionText);

    /// <summary>
    /// 侧栏动作面缺省配置（各页在选中态变化时调用；定制页可在其后覆写个别位）：
    /// 文件夹 = 「打开」（进入目录）+「重命名」；链接 = 「详情」+「打开网站」+「编辑」。
    /// 铅笔槽的**取色随语义走**（与标签同一处切换，绝不各写一份）：重命名 = `AccentBtn` 深紫（与侧栏
    /// 独立那枚「重命名」同色）/ 编辑 = 图标钮缺省 `Primary`。
    /// </summary>
    protected void ConfigureSidebarActionLabels(bool isFolder)
    {
        OpenLabel = isFolder ? "打开" : "详情";
        OpenToolTip = isFolder ? "打开目录" : "查看详情";
        EditLabel = isFolder ? "重命名" : "编辑";
        EditActionAccent = isFolder;
        ShowOpenWebsiteButton = !isFolder && ShowOpenWebsite;
        OpenColumnSpan = isFolder || !ShowOpenWebsiteButton ? 2 : 1;
    }

    /// <summary>复制 URL（子类赋值）。</summary>
    public ICommand? CopyUrlCommand { get; set; }

    // —— 信息卡：数据驱动行集合（替换整个集合 + Rows 通知，行内值可单独原位更新） ——
    public IReadOnlyList<DetailSidebarRow> Rows { get; private set; } = Array.Empty<DetailSidebarRow>();
    protected void SetRows(IReadOnlyList<DetailSidebarRow> rows)
    {
        Rows = rows;
        OnPropertyChanged(nameof(Rows));
    }
    protected DetailSidebarRow? FindRow(string label)
    {
        foreach (var r in Rows)
            if (r.Label == label) return r;
        return null;
    }

    // —— 多选 ——
    public int SelectedTotal { get; protected set; }
    public int SelectedFolders { get; protected set; }
    public int SelectedLinks { get; protected set; }

    /// <summary>清空选中（回到空占位态；动作面复位为缺省）。</summary>
    public virtual void Clear()
    {
        HasSelection = false;
        IsMulti = false;
        IsFolder = false;
        IsReadOnly = false;
        UseTrashedIconTone = false;
        ResetActionSurface();
        DisplayName = "";
        IdText = "";
        UrlText = "";
        DescriptionText = "";
        Favicon = null;
        SelectedTotal = SelectedFolders = SelectedLinks = 0;
        SetRows(Array.Empty<DetailSidebarRow>());
        RaiseAll();
    }

    protected void RaiseAll()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsPlaceholder));
        OnPropertyChanged(nameof(IsMulti));
        OnPropertyChanged(nameof(IsSingle));
        OnPropertyChanged(nameof(IsFolder));
        OnPropertyChanged(nameof(IsLink));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(UseTrashedIconTone));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IdText));
        OnPropertyChanged(nameof(Favicon));
        OnPropertyChanged(nameof(HasFavicon));
        OnPropertyChanged(nameof(UrlText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(SelectedTotal));
        OnPropertyChanged(nameof(SelectedFolders));
        OnPropertyChanged(nameof(SelectedLinks));
        RaiseActionChanged();
    }
}
