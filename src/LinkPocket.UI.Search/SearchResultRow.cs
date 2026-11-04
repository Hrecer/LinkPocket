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

    /// <summary>文件夹行：与链接同处一条结果集（文件夹块恒在链接块之前）。</summary>
    public override bool IsFolder => _isFolder;

    /// <summary>跨类型不撞车的身份键（见 <see cref="LinkItem.Key"/>）。</summary>
    public override string Key => _isFolder ? "f:" + LinkId : "l:" + LinkId;

    /// <summary>把 DTO 写入本行；只在本行第一次填充时调用（重复填充由调用方拒绝）。</summary>
    public void Fill(LinkDto dto, string query)
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
    }
}
