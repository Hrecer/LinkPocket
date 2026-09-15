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

    /// <summary>仅文件夹行有值：直接子书签数。</summary>
    public int LinkCount { get; init; }

    public bool IsImportant { get; init; }

    public DateTime ModifiedAt { get; init; }

    public string ModifiedText => ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

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

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
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
