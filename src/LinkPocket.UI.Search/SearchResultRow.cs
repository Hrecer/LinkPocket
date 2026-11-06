using System;
using System.Windows;
using LinkPocket.Contracts;
using LinkPocket.Models;

namespace LinkPocket.Views;

/// <summary>
/// 搜索结果的**可原位填充行**：先以占位状态进入结果集（总数/滚动条从第一帧就是真实值），
/// 每页数据到达后直接改本行字段并触发属性通知——不替换集合、不重建行、不改变滚动位置。
/// </summary>
internal sealed class SearchResultRow : LinkItem
{
    /// <summary>尚未填入真实数据；点击/选中/详情等命令必须拒绝。</summary>
    public bool IsPlaceholder { get; private set; } = true;

    private bool _isFolder;

    /// <summary>位置列文本解析器（结果 VM 的按目录 + 语言版本缓存）：行只存委托、不存文本——
    /// 换语言时由视图对可视行发一次"全部属性失效"，getter 重新取词。</summary>
    private Func<string?, string>? _pathResolver;

    public void SetPathResolver(Func<string?, string> resolver) => _pathResolver = resolver;

    /// <summary>占位行的非身份单元格一律空文本（身份 = 标题 + URL），不参与路径/日期/次数渲染。</summary>
    public string PathText => IsPlaceholder ? string.Empty : _pathResolver?.Invoke(ListId) ?? string.Empty;
    public string UpdatedText => IsPlaceholder ? string.Empty : UpdatedValue.Resolve();
    public string LastVisitedText => IsPlaceholder ? string.Empty : LastVisitedValue.Resolve();
    public string VisitCountText => IsPlaceholder ? string.Empty : VisitCountValue.Resolve();
    public string CreatedText => IsPlaceholder ? string.Empty : CreatedValue.Resolve();

    /// <summary>图标在占位期隐藏（保留几何）；文件夹行不显示 favicon，链接行不显示文件夹字形。</summary>
    public Visibility LinkIconVisibility
        => IsPlaceholder ? Visibility.Hidden : _isFolder ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FolderIconVisibility
        => IsPlaceholder ? Visibility.Hidden : _isFolder ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>换语言：可视行一次性让所有属性绑定重读（文案仍在渲染边界取词，不落库）。</summary>
    public void NotifyLanguageChanged() => OnPropertyChanged(string.Empty);

    /// <summary>文件夹行：与链接同处一条结果集（文件夹块恒在链接块之前）。</summary>
    public override bool IsFolder => _isFolder;

    /// <summary>跨类型不撞车的身份键（见 <see cref="LinkItem.Key"/>）。</summary>
    public override string Key => _isFolder ? "f:" + LinkId : "l:" + LinkId;

    /// <summary>把 DTO 写入本行；只在本行第一次填充时调用。</summary>
    public void Fill(LinkDto dto, string query) => Apply(dto, query);

    /// <summary>活结果集更新：把最新 DTO 原位写回已有行（保持行对象/选中/详情引用不变）。</summary>
    public void Refill(LinkDto dto, string query) => Apply(dto, query);

    private void Apply(LinkDto dto, string query)
    {
        IsPlaceholder = false;
        _isFolder = string.Equals(dto.Kind, "folder", StringComparison.OrdinalIgnoreCase);
        LinkId = dto.LinkId;
        Url = dto.Url ?? string.Empty;
        Title = dto.Title ?? string.Empty;
        Description = dto.Description ?? string.Empty;
        FaviconUrl = dto.FaviconUrl ?? string.Empty;
        ListId = dto.ListId;
        LastVisitedAt = dto.LastVisitedAt;
        VisitCount = dto.VisitCount;
        IsImportant = dto.IsImportant;
        CreatedAt = dto.CreatedAt;
        UpdatedAt = dto.UpdatedAt;
        HighlightQuery = query;

        OnPropertyChanged(nameof(IsPlaceholder));
        OnPropertyChanged(nameof(IsFolder));
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(LinkId));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(FaviconUrl));
        OnPropertyChanged(nameof(ListId));
        OnPropertyChanged(nameof(LastVisitedAt));
        OnPropertyChanged(nameof(VisitCount));
        OnPropertyChanged(nameof(IsImportant));
        OnPropertyChanged(nameof(CreatedAt));
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(HighlightQuery));
        OnPropertyChanged(nameof(UpdatedValue));
        OnPropertyChanged(nameof(LastVisitedValue));
        OnPropertyChanged(nameof(VisitCountValue));
        OnPropertyChanged(nameof(CreatedValue));
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(LastVisitedText));
        OnPropertyChanged(nameof(VisitCountText));
        OnPropertyChanged(nameof(CreatedText));
        OnPropertyChanged(nameof(LinkIconVisibility));
        OnPropertyChanged(nameof(FolderIconVisibility));
    }
}
