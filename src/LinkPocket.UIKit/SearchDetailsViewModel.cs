using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Models;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>
/// 搜索页右侧详情栏数据模型：继承通用 <see cref="DetailSidebarModel"/>，
/// 把搜索结果选中的 <see cref="LinkItem"/> 映射为详情栏数据。
/// 搜索结果只有链接（无文件夹/多选），信息卡行固定为：位置 / 最后更新 / 最后查看 / 查看次数 / 创建时间 / ID。
/// 页面动作（打开 / 编辑 / 删除）由 MainWindow 注入命令；favicon 异步补拉后原位刷新。
/// </summary>
public class SearchDetailsViewModel : DetailSidebarModel
{
    /// <summary>选中代次：异步补拉返回时校验，避免旧结果覆盖新选中。</summary>
    private int _generation;

    public ICommand CopyIdCommand => _copyIdCommand ??= new RelayCommand(
        () => { try { if (!string.IsNullOrEmpty(IdText)) System.Windows.Clipboard.SetText(IdText); } catch { } },
        () => IsLink && !string.IsNullOrEmpty(IdText));   // 3.3：与 CopyUrlCommand 对齐（单选链接且非空）
    private RelayCommand? _copyIdCommand;

    public SearchDetailsViewModel()
    {
        CopyUrlCommand = new RelayCommand(
            () => { try { if (!string.IsNullOrEmpty(UrlText)) System.Windows.Clipboard.SetText(UrlText); } catch { } },
            () => IsLink && !string.IsNullOrEmpty(UrlText));
    }

    /// <summary>
    /// 用选中的搜索结果行更新详情栏。pathText 由使用方解析（搜索页与表格「位置」列同一口径）。
    /// </summary>
    public void UpdateFrom(LinkItem? item, string pathText)
    {
        _generation++;
        var gen = _generation;
        if (item == null)
        {
            Clear();
            return;
        }

        HasSelection = true;
        IsMulti = false;
        IsFolder = false;
        DisplayName = string.IsNullOrEmpty(item.Title) ? item.Url : item.Title;
        IdText = item.LinkId;
        UrlText = item.Url;
        DescriptionText = item.Description ?? "";
        Favicon = FaviconService.LoadFromCache(item.FaviconUrl);

        SetRows(new List<DetailSidebarRow>
        {
            new() { IconKind = "folder-outline", Label = "位置", Value = pathText },
            new() { IconKind = "refresh", Label = "最后更新", Value = FormatTime(item.UpdatedAt) },
            new() { IconKind = "history", Label = "最后查看", Value = item.LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未" },
            new() { IconKind = "trending-up", Label = "查看次数", Value = $"{item.VisitCount} 次" },
            new() { IconKind = "plus-circle-outline", Label = "创建时间", Value = FormatTime(item.CreatedAt) },
            new() { IconKind = "fingerprint", Label = "ID", Value = item.LinkId, IsMono = true, CopyCommand = CopyIdCommand, CopyToolTip = "复制 ID" },
        });

        RaiseAll();

        // favicon 未命中缓存时异步补拉，成功且选中未变时原位刷新
        if (Favicon == null && !string.IsNullOrWhiteSpace(item.FaviconUrl))
        {
            var faviconUrl = item.FaviconUrl;
            _ = Task.Run(async () =>
            {
                try
                {
                    await FaviconService.PrefetchAndCacheAsync(faviconUrl);
                    var cached = FaviconService.LoadFromCache(faviconUrl);
                    if (cached == null || gen != _generation) return;
                    // 2.5：INPC 必须回到 UI 线程（后台线程触发属性通知在特定绑定路径下会抛异常）
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (gen != _generation) return;
                        Favicon = cached;
                        OnPropertyChanged(nameof(Favicon));
                        OnPropertyChanged(nameof(HasFavicon));
                    });
                }
                catch { }
            });
        }
    }

    private static string FormatTime(DateTime utc)
        => utc.Year <= 1 ? "—" : utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
