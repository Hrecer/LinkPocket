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
/// 行内容由使用方（浏览器页 / 搜索页）按选中对象构建，控件只负责渲染。
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
/// 页面特有逻辑（异步补拉、宿主命令）由子类或使用方注入：
/// - Rows 按选中对象构建（文件夹与链接的信息行各不相同）；
/// - Open/Rename/Delete 三个页面动作命令由使用方赋值；
/// - CopyUrl/CopyId 交给子类实现（需要读取 UrlText/IdText）。
/// </summary>
public class DetailSidebarModel : INotifyPropertyChanged
{
    // —— 状态 ——
    public bool HasSelection { get; protected set; }
    public bool IsPlaceholder => !HasSelection;
    public bool IsMulti { get; protected set; }
    public bool IsSingle => HasSelection && !IsMulti;
    public bool IsFolder { get; protected set; }
    public bool IsLink => IsSingle && !IsFolder;

    /// <summary>
    /// 只读模式（如回收站详情栏）：单选「快捷操作」卡整体隐藏 —— 只展示信息，不提供
    /// 打开 / 编辑 / 删除等任何动作入口（动作仍可由宿主页面自行提供）。
    /// </summary>
    public bool IsReadOnly { get; protected set; }

    // —— 单选公共 ——
    public string DisplayName { get; protected set; } = "";
    public string IdText { get; protected set; } = "";
    public BitmapImage? Favicon { get; protected set; }
    public bool HasFavicon => Favicon != null;

    // —— 单选书签 ——
    public string UrlText { get; protected set; } = "";
    public string DescriptionText { get; protected set; } = "";
    public bool HasDescription => IsLink && !string.IsNullOrWhiteSpace(DescriptionText);

    /// <summary>
    /// 操作卡里铅笔按钮的文案：链接是「编辑」（打开整页编辑器），
    /// 文件夹是「重命名」（文件夹本身只有名称）。
    /// </summary>
    public string EditLabel => IsFolder ? "重命名" : "编辑";

    /// <summary>操作卡主按钮文案：文件夹是「打开」（进入目录），链接是「详情」（查看详情页）。</summary>
    public string OpenLabel => IsFolder ? "打开" : "详情";
    /// <summary>操作卡主按钮提示（短表述）。</summary>
    public string OpenToolTip => IsFolder ? "打开目录" : "查看详情";

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

    // —— 命令：页面动作由使用方注入；复制类由子类实现 ——
    public ICommand? OpenCommand { get; set; }
    /// <summary>打开网站（仅单选链接；用系统默认浏览器打开并记录一次访问）。由使用方注入。</summary>
    public ICommand? OpenWebsiteCommand { get; set; }
    public ICommand? RenameCommand { get; set; }
    public ICommand? DeleteCommand { get; set; }
    public ICommand? CopyUrlCommand { get; set; }

    // —— 多选 ——
    public int SelectedTotal { get; protected set; }
    public int SelectedFolders { get; protected set; }
    public int SelectedLinks { get; protected set; }

    /// <summary>清空选中（回到空占位态）。</summary>
    public virtual void Clear()
    {
        HasSelection = false;
        IsMulti = false;
        IsFolder = false;
        IsReadOnly = false;
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
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IdText));
        OnPropertyChanged(nameof(Favicon));
        OnPropertyChanged(nameof(HasFavicon));
        OnPropertyChanged(nameof(UrlText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(EditLabel));
        OnPropertyChanged(nameof(OpenLabel));
        OnPropertyChanged(nameof(OpenToolTip));
        OnPropertyChanged(nameof(SelectedTotal));
        OnPropertyChanged(nameof(SelectedFolders));
        OnPropertyChanged(nameof(SelectedLinks));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
