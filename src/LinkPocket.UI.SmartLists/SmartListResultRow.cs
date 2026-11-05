using System;
using System.Windows;
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
    /// <summary>位置列文本解析器（结果 VM 的按目录 + 语言版本缓存）：行只存委托，不存文本——
    /// 换语言时由视图对可视行发一次"全部属性失效"，getter 重新取词。</summary>
    private Func<string?, string>? _pathResolver;

    public bool IsPlaceholder { get; private set; } = true;

    public static SmartListResultRow Placeholder(string loadingText) => new()
    {
        Title = loadingText,
        Url = "\u00A0",
    };

    public void SetPathResolver(Func<string?, string> resolver) => _pathResolver = resolver;

    /// <summary>占位行的非身份单元格一律"空文本"（身份 = 标题 + URL），不参与日期/次数的渲染。</summary>
    public string PathText => IsPlaceholder ? string.Empty : _pathResolver?.Invoke(ListId) ?? string.Empty;
    public string UpdatedText => IsPlaceholder ? string.Empty : UpdatedValue.Resolve();
    public string LastVisitedText => IsPlaceholder ? string.Empty : LastVisitedValue.Resolve();
    public string VisitCountText => IsPlaceholder ? string.Empty : VisitCountValue.Resolve();
    public string CreatedText => IsPlaceholder ? string.Empty : CreatedValue.Resolve();
    /// <summary>图标在占位期隐藏（保留几何）；渲染口径与其它非身份单元格一致。</summary>
    public Visibility FaviconVisibility => IsPlaceholder ? Visibility.Hidden : Visibility.Visible;

    /// <summary>换语言：可视行一次性让所有属性绑定重读（文案仍在渲染边界取词，不落库）。</summary>
    public void NotifyLanguageChanged() => OnPropertyChanged(string.Empty);

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
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(LastVisitedText));
        OnPropertyChanged(nameof(VisitCountText));
        OnPropertyChanged(nameof(CreatedText));
        OnPropertyChanged(nameof(FaviconVisibility));
    }
}
