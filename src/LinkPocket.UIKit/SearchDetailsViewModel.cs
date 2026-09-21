using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.I18n;
using LinkPocket.UIKit;

namespace LinkPocket.ViewModels;

/// <summary>
/// 搜索页右侧详情栏数据模型：继承通用 <see cref="DetailSidebarModel"/>，
/// 把搜索结果选中的 <see cref="LinkItem"/> 映射为详情栏数据。
/// 搜索结果只有链接（无文件夹/多选），信息卡行固定为：位置 / 最后更新 / 最后查看 / 查看次数 / 创建时间 / ID。
/// 页面动作（详情 / 打开 / 跳转 / 删除）由使用方页面注入命令；favicon 异步补拉后原位刷新。
/// 三个页面共用本模型：搜索页（完整多选）、智能列表结果页、查重明细（后两者单选中）。
/// </summary>
public class SearchDetailsViewModel : DetailSidebarModel
{
    /// <summary>选中代次：异步补拉返回时校验，避免旧结果覆盖新选中。</summary>
    private int _generation;

    /// <summary>只读结果页的动作面是否已收窄（见 <see cref="HideEditAndDeleteActions"/>）——动作面复位后仍保持。</summary>
    private bool _readOnlyActions;

    /// <summary>本页右栏是否声明两行排布（见 <see cref="UseStackedActions"/>）——动作面复位后仍保持。</summary>
    private bool _stackedActions;

    /// <summary>
    /// **只读结果页**（智能列表结果页 / 去重明细对比页）的动作面收窄：**不显示「编辑」（铅笔）与「删除」（垃圾桶）**。
    /// 理由：这两页是只读的查看/对比面——智能列表不提供改写入口；去重明细的删除入口在头部
    /// 「删除重复项」（按勾选、且"至少保留一条"），右栏不承担该语义（也就不会出现"按钮在、命令是 null"的死按钮）。
    /// 幂等：`Clear()` / `UpdateFrom()` 走动作面复位之后仍保持隐藏。
    /// </summary>
    public void HideEditAndDeleteActions()
    {
        _readOnlyActions = true;
        ApplyReadOnlyActions();
    }

    private void ApplyReadOnlyActions()
    {
        if (!_readOnlyActions) return;
        ShowEditAction = false;
        ShowDeleteAction = false;
        RaiseActionChanged();
    }

    /// <summary>
    /// 右栏排布定制 = **两行**（药丸一行 / 图标钮一行靠右）：286 宽放不下"两枚药丸 + 三枚 32 图标钮"
    /// （搜索页单选态的「跳转 / 编辑 / 删除」正是三枚），同排会把药丸压到裁字（WARNINGS 51）。
    /// 与浏览页侧栏 / 回收站右栏同一套 `StackedActions`（**共用同一对按钮模板**，不是第二套界面）；
    /// 图标钮只有一枚的结果页（智能列表 / 查重明细）不开，保持一行。
    /// 幂等：`Clear()` / `UpdateFrom()` 走动作面复位之后仍保持。
    /// </summary>
    public void UseStackedActions()
    {
        _stackedActions = true;
        ApplyPageLayout();
    }

    private void ApplyPageLayout()
    {
        if (_stackedActions) StackedActions = true;
    }

    /// <summary>清空选中回到空占位（动作面复位后重新应用本页的动作面定制）。</summary>
    public override void Clear()
    {
        base.Clear();
        ApplyReadOnlyActions();
        ApplyPageLayout();
    }

    public ICommand CopyIdCommand => _copyIdCommand ??= new RelayCommand(
        () => { try { if (!string.IsNullOrEmpty(IdText)) System.Windows.Clipboard.SetText(IdText); } catch { } },
        () => IsLink && !string.IsNullOrEmpty(IdText));   // 与 CopyUrlCommand 对齐（单选链接且非空）
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
    public void UpdateFrom(LinkItem? item, LocValue pathText)
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
        ConfigureSidebarActionLabels(isFolder: false);   // 搜索结果恒为链接（共享动作面缺省配置）
        // 「跳转」= 进目录 + 选中该行（经使用方注入的 JumpCommand）；单一目标动作 → 只在单选态开。
        ShowJumpAction = true;
        DisplayNameData = string.IsNullOrEmpty(item.Title) ? item.Url : item.Title;
        IdText = item.LinkId;
        UrlText = item.Url;
        DescriptionText = item.Description ?? "";
        Favicon = FaviconService.LoadFromCache(item.FaviconUrl);

        SetRows(new List<DetailSidebarRow>
        {
            new() { IconKind = "folder-outline", LabelKey = "ui.noun.location", ValueCopy = pathText },
            new() { IconKind = "refresh", LabelKey = "ui.noun.updatedAt", ValueData = FormatTime(item.UpdatedAt) },
            new() { IconKind = "history", LabelKey = "ui.noun.lastVisited", ValueData = item.LastVisitedAt is null ? "" : FormatTime(item.LastVisitedAt.Value), ValueCopy = item.LastVisitedAt is null ? Loc.K("clock.never") : LocValue.Empty },
            new() { IconKind = "trending-up", LabelKey = "ui.noun.visitCount", ValueCopy = Loc.K("count.viewsN", item.VisitCount) },
            new() { IconKind = "plus-circle-outline", LabelKey = "ui.noun.createdAt", ValueData = FormatTime(item.CreatedAt) },
            new() { IconKind = "fingerprint", LabelKey = "ui.noun.id", ValueData = item.LinkId, IsMono = true, CopyCommand = CopyIdCommand, CopyToolTip = Loc.K("common.copyId") },
        });

        ApplyPageLayout();   // 排布位在 RaiseAll 之前落地（RaiseAll 里的动作面通知才带得上它）
        RaiseAll();
        ApplyReadOnlyActions();   // 动作面复位后再应用本页定制（只读结果页不摆编辑/删除按钮）

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
                    // INPC 必须回到 UI 线程（后台线程触发属性通知在特定绑定路径下会抛异常）
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
        => utc.Year <= 1 ? "—" : UiClock.Format(utc.ToLocalTime());

    /// <summary>
    /// 多选投影（搜索页完整多选模型）：显示项数（搜索页恒为链接）；动作面收窄为「删除所选」。
    /// 与回收站多选同一条口径（同一 DetailSidebar 控件、同一能力位系统）。
    /// </summary>
    public void ShowMulti(IReadOnlyList<LinkItem> items)
    {
        _generation++;   // 使在途 favicon 补拉失效
        HasSelection = true;
        IsMulti = true;
        IsFolder = false;
        IsReadOnly = false;
        ShowOpenAction = false;
        ShowOpenWebsite = false;
        ShowOpenWebsiteButton = false;
        ShowEditAction = false;
        ShowDeleteAction = true;
        // 多选**不提供跳转**（跳转只对单个目标有意义；顶部药丸的 CanExecute 也是"恰一项"）
        ShowJumpAction = false;
        DisplayNameCopy = Loc.K("count.selected", items.Count);
        IdText = string.Empty;
        UrlText = string.Empty;
        DescriptionText = string.Empty;
        Favicon = null;
        SelectedTotal = items.Count;
        SelectedFolders = 0;
        SelectedLinks = items.Count;
        SetRows(Array.Empty<DetailSidebarRow>());
        ApplyPageLayout();
        RaiseAll();
    }
}
