using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace LinkPocket.ViewModels;

/// <summary>
/// 回收站主栏的行视图模型（与浏览页 <see cref="BrowserRowViewModel"/> 同构）：
/// 一行 = 一个被删单元（folder 条目）或一条被删书签（link 条目）。
/// 列 = 名称 / 类型 / 原位置 / 删除时间（回收站语义保留；行皮肤与选中投影与浏览页同一套）。
/// 选中是 <see cref="TrashViewModel"/> 的只读投影，行对象随重建销毁、绝不持久状态。
/// </summary>
public class TrashRowViewModel : INotifyPropertyChanged
{
    public TrashRowViewModel(string id, bool isFolder, string name)
    {
        Id = id;
        IsFolder = isFolder;
        Name = name;
    }

    /// <summary>单元 = trash_folder_id；链接 = 原链接 ID。</summary>
    public string Id { get; }

    public bool IsFolder { get; }

    public string Name { get; }

    public string? Url { get; init; }

    /// <summary>描述快照（链接 = 原描述；单元 = 被删文件夹的描述，v5 保真列）。</summary>
    public string? Description { get; init; }

    /// <summary>单元子树链接总数（链接行恒 0）。</summary>
    public int LinkCount { get; init; }

    /// <summary>删除时的原位置路径快照。</summary>
    public string? OriginPath { get; init; }

    public DateTime DeletedAt { get; init; }

    /// <summary>「类型」列。</summary>
    public string TypeText => IsFolder ? "文件夹" : "链接";

    /// <summary>「原位置」列（空快照回落「全部书签」——旧数据兜底显示，不是兼容层）。</summary>
    public string OriginText => string.IsNullOrWhiteSpace(OriginPath) ? "全部书签" : OriginPath!;

    /// <summary>「删除时间」列。</summary>
    public string DeletedText => DeletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>仅链接行有值：favicon 地址（磁盘缓存取图标的数据源）。</summary>
    public string? FaviconUrl { get; init; }

    /// <summary>仅链接行有值：前端解码后的 favicon（可能为 null，UI 显示占位图标）。</summary>
    public BitmapImage? Favicon { get; set; }

    /// <summary>行菜单命令的绑定宿主（ContextMenu 不在可视树，无法 RelativeSource 向上找）。</summary>
    public TrashViewModel? Host { get; set; }

    /// <summary>选中 = 宿主选中集合的纯投影。</summary>
    public bool IsSelected => Host != null && Host.IsSelectedId(Id);

    public void InvalidateIsSelected() => OnPropertyChanged(nameof(IsSelected));

    /// <summary>
    /// 两个行序列是否**渲染等价**（ID 序列 + 全部展示字段逐项一致，含顺序；favicon 也纳入）。
    /// 用途：刷新后判断"要不要换掉 Rows"——逐条 Clear/Add 会触发 N 次 CollectionChanged 并让视图整表重建，
    /// 内容没变时纯属白烧（低性能设备切页偶发明显卡顿）。
    /// 口径：**宁可重建不可漏更新**——字段有任何差异即视为需要重建。
    /// </summary>
    public static bool SameSequence(IReadOnlyList<TrashRowViewModel>? a, IReadOnlyList<TrashRowViewModel>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Id != y.Id || x.IsFolder != y.IsFolder || x.Name != y.Name || x.Url != y.Url
                || x.Description != y.Description || x.LinkCount != y.LinkCount
                || x.OriginPath != y.OriginPath || x.DeletedAt != y.DeletedAt
                || x.FaviconUrl != y.FaviconUrl || !ReferenceEquals(x.Favicon, y.Favicon)) return false;
        }
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
