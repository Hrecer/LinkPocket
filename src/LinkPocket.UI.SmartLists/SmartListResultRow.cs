using LinkPocket.Contracts;
using LinkPocket.Models;

namespace LinkPocket.Views;

/// <summary>
/// 智能列表的**可原位填充行**：首帧以占位状态进入结果集（总数/滚动条从第一帧就是真实值），
/// 后台页到达后直接改本行字段并触发属性通知——不替换集合、不重建行、不移动滚动位置。
/// 占位行不显示日期/次数/位置，也不能被选中（<see cref="IsPlaceholder"/>）。
/// </summary>
internal sealed class SmartListResultRow : LinkItem
{
    public bool IsPlaceholder { get; private set; } = true;

    public static SmartListResultRow Placeholder(string loadingText) => new()
    {
        Title = loadingText,
        Url = "\u00A0",
    };

    /// <summary>把 DTO 写入本行（首填或重排后覆盖）；只由结果 VM 的填充器调用。</summary>
    public void Fill(LinkDto dto)
    {
        IsPlaceholder = false;
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

        OnPropertyChanged(nameof(IsPlaceholder));
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
        OnPropertyChanged(nameof(UpdatedValue));
        OnPropertyChanged(nameof(LastVisitedValue));
        OnPropertyChanged(nameof(VisitCountValue));
        OnPropertyChanged(nameof(CreatedValue));
    }
}
