using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

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

    /// <summary>「最后更新」列：未设置时间（默认值）时显示占位符，避免出现 0001-01-01。</summary>
    public string ModifiedText => ModifiedAt.Year <= 1
        ? "—"
        : ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>「最后查看」列。</summary>
    public string LastViewedText => LastViewedAt.HasValue
        ? LastViewedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "从未";

    /// <summary>「查看次数」列。</summary>
    public string ViewCountText => $"{ViewCount} 次";

    /// <summary>「创建时间」列。</summary>
    public string CreatedText => CreatedAt.Year <= 1
        ? "—"
        : CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

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

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            Host?.NotifySelectionChanged();
        }
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
}
