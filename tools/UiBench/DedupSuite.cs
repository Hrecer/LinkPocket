using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinkPocket;
using LinkPocket.Contracts;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using LinkPocket.Views;

namespace UiBench;

/// <summary>
/// ⑫ dedup：查重全链路（扫描 / 内容未变重扫 / 展开大组 / 删除后局部更新 / 组消失回表）。
/// 量法与真实操作一致：动作 → 界面上真的发生（面板可见、行集落地、布局排空），
/// 同时记录 UI 线程最长停摆、实例化行容器数与分配量。模板虚拟化下容器数应与数据量无关。
/// </summary>
internal static class DedupSuite
{
    public static void Run(
        AppHost host,
        MainViewModel vm,
        MainWindow window,
        Action<string> log,
        Func<Action, Func<bool>, int, (long Wall, long Stall)> measure,
        Action<string> reportGaps,
        Action<string, string, long, long, int?, int?, string?> record,
        Action<string, bool, string> gate)
    {
        _gate = gate;
        log("");
        log("⑫ dedup：查重（扫描 / 重扫 / 展开 / 删除 / 回表）");

        vm.SelectNavCommand.Execute(NavIds.Tools);
        PumpUntil(() => vm.CurrentNavId == NavIds.Tools, 10000);
        Pump(700);

        var page = GetField(window, "ToolsView") as ToolsPage;
        if (page is null) { log("   跳过：取不到 ToolsView"); return; }
        var toolsVm = GetProperty(page, "VmTools") as ToolsViewModel;
        var pane = GetField(page, "PaneTable") as SortableDataTable;
        var detail = GetField(page, "DetailTable") as SortableDataTable;
        var detailPanel = GetField(page, "DetailPanel") as FrameworkElement;
        var mainPanel = GetField(page, "MainPanel") as FrameworkElement;
        if (toolsVm is null || pane is null || detail is null || detailPanel is null || mainPanel is null)
        {
            log("   跳过：工具页内部元素取不到（页面结构变化？）");
            return;
        }

        var runDedup = typeof(ToolsPage).GetMethod("RunDedupAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var enterDetail = typeof(ToolsPage).GetMethod("EnterDetailAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var applyDeletion = typeof(ToolsPage).GetMethod("ApplyDeletionResult",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (runDedup is null || enterDetail is null || applyDeletion is null)
        {
            log("   跳过：工具页私有入口签名变化（RunDedupAsync / EnterDetail / ApplyDeletionResult）");
            return;
        }

        // —— 引擎面：查重只回重复组 vs 旧的拉全库 ——
        host.Client.InvalidateQueryCache();
        var scanAlloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var engineScan = MeasureTask(() => host.Client.DedupScanAsync());
        var scanAlloc1 = GC.GetTotalAllocatedBytes(precise: true);
        log($"   引擎 dedup.scan（冷）：wall={engineScan.Ms}ms 组={engineScan.Value.Count}"
            + $" 链接={engineScan.Value.Sum(g => g.Links.Count)} 分配={(scanAlloc1 - scanAlloc0) / 1048576.0:F1}MB");
        record("dedup", "引擎 dedup.scan 冷", engineScan.Ms, -1,
            engineScan.Value.Sum(g => g.Links.Count), null,
            $"groups={engineScan.Value.Count}; alloc_mb={(scanAlloc1 - scanAlloc0) / 1048576.0:F1}");

        var engineWarm = MeasureTask(() => host.Client.DedupScanAsync());
        log($"   引擎 dedup.scan（热缓存）：wall={engineWarm.Ms}ms");
        record("dedup", "引擎 dedup.scan 热", engineWarm.Ms, -1,
            engineWarm.Value.Sum(g => g.Links.Count), null, $"groups={engineWarm.Value.Count}");

        host.Client.InvalidateQueryCache();
        var leanEngine = MeasureTask(() => host.Client.DedupScanAsync(includeLinks: false));
        log($"   引擎 dedup.scan 摘要（冷）：wall={leanEngine.Ms}ms 组={leanEngine.Value.Count}"
            + $" 链接={leanEngine.Value.Sum(g => g.Count)}");
        record("dedup", "引擎 dedup.scan 摘要冷", leanEngine.Ms, -1,
            leanEngine.Value.Sum(g => g.Count), null, $"groups={leanEngine.Value.Count}");

        host.Client.InvalidateQueryCache();
        var allAlloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var allLinks = MeasureTask(() => host.Client.LinkAllAsync());
        var allAlloc1 = GC.GetTotalAllocatedBytes(precise: true);
        log($"   对照 links.list(per_page=0)（冷）：wall={allLinks.Ms}ms 全库={allLinks.Value.Count}"
            + $" 分配={(allAlloc1 - allAlloc0) / 1048576.0:F1}MB");
        record("dedup", "对照全库 links.list 冷", allLinks.Ms, -1, allLinks.Value.Count, null,
            $"alloc_mb={(allAlloc1 - allAlloc0) / 1048576.0:F1}");

        // —— UI 首次扫描（缓存已失效 = 冷） ——
        var first = RunUiScan("UI 首次扫描（冷）", invalidate: true,
            measure, page, pane, toolsVm, runDedup, host, window, log, record);
        if (first.Groups == 0)
        {
            log("   本库没有重复项：只完成空结果路径。");
            record("dedup", "UI 空结果", first.Wall, first.Stall, 0, ContainerCount(pane), "groups=0");
            return;
        }

        VerifyMainSort(pane, log);
        Pump(80);
        window.UpdateLayout();
        VerifyRowText(pane, log, "主表");

        // —— 内容未变重扫：不允许重设 ItemsSource（同引用） ——
        var sourceBefore = pane.ItemsSource;
        var second = RunUiScan("UI 重扫（内容未变）", invalidate: false,
            measure, page, pane, toolsVm, runDedup, host, window, log, record);
        var sameSource = ReferenceEquals(sourceBefore, pane.ItemsSource);
        log($"   重扫：内容未变={second.SameContent} ItemsSource 同引用={sameSource}");
        record("dedup", "UI 重扫内容未变", second.Wall, second.Stall, second.Groups, ContainerCount(pane),
            $"same_content={second.SameContent}; same_source={sameSource}");
        _gate("重扫内容未变不重建行集", second.SameContent && sameSource,
            $"same_content={second.SameContent}; same_source={sameSource}");

        // —— 真实行点击：主表行 → 明细；明细行 → 选中（模板行的事件路由） ——
        var firstMainRow = FindFirstTagged(pane, "DataRow");
        if (firstMainRow?.DataContext is DedupGroupRow firstGroup)
        {
            RaiseRowClick(firstMainRow);
            Pump(80);
            window.UpdateLayout();
            _gate("点主表行进入明细",
                detailPanel.Visibility == Visibility.Visible
                && string.Equals(toolsVm.CurrentGroupUrl, firstGroup.Url, StringComparison.Ordinal),
                $"visible={detailPanel.Visibility}; url_match={toolsVm.CurrentGroupUrl == firstGroup.Url}");
            typeof(ToolsPage).GetMethod("GoBackToList", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(page, null);
            Pump(80);
            window.UpdateLayout();
        }

        var groups = GetProperty(toolsVm, "Groups") as List<DedupGroupRow>;
        if (groups is null || groups.Count == 0) { log("   跳过后续：Groups 为空"); return; }

        // —— 展开最大的组（摘要先出、组内链接按需加载） ——
        var largest = groups.OrderByDescending(g => g.Count).First();
        var detailAlloc0 = GC.GetTotalAllocatedBytes(precise: true);
        Task? openTask = null;
        var open = measure(
            () => openTask = enterDetail.Invoke(page, new object[] { largest, false }) as Task,
            () => detailPanel.Visibility == Visibility.Visible
                  && openTask is { IsCompleted: true }
                  && toolsVm.CurrentGroupLinks is not null
                  && Settle(window, detail),
            30000);
        var detailAlloc1 = GC.GetTotalAllocatedBytes(precise: true);
        var detailRows = GetField(page, "_detailLinks") as System.Collections.ICollection;
        log($"   展开最大组：wall={open.Wall}ms 停摆={open.Stall}ms 组内={largest.Count}"
            + $" 行容器={ContainerCount(detail)} 分配={(detailAlloc1 - detailAlloc0) / 1048576.0:F1}MB");
        VerifyRowText(detail, log, "明细");
        VerifyCheckboxClick(detail, toolsVm, log);
        VerifyDetailRowClick(detail, toolsVm, log);
        record("dedup", "展开最大组", open.Wall, open.Stall, largest.Count, ContainerCount(detail),
            $"detail_rows={detailRows?.Count ?? -1}; alloc_mb={(detailAlloc1 - detailAlloc0) / 1048576.0:F1}");

        var deletedIds = new List<string>();

        // —— 删除 1 条（组仍保留，>1） ——
        if (largest.Count >= 3)
        {
            var expectedRest = largest.Count - 1;
            var del = DeleteOne(window, page, toolsVm, detailPanel, largest, measure, log,
                record, reportGaps, applyDeletion, expectGroupGone: false, deletedIds);
            if (del.Done)
                _gate("删除后组仍保留且已删除项消失",
                    del.RestCount == expectedRest && !del.DeletedStillVisible,
                    $"rest={del.RestCount}; expect={expectedRest}; deleted_visible={del.DeletedStillVisible}");
        }
        else
        {
            log("   跳过「删除 1 条后组仍保留」：没有 ≥3 条的组");
        }

        // —— 删除到只剩 1 条：组消失、回主表、不重扫 ——
        groups = GetProperty(toolsVm, "Groups") as List<DedupGroupRow> ?? groups;
        var pair = groups.FirstOrDefault(g => g.Count == 2);
        if (pair is not null)
        {
            var del = DeleteOne(window, page, toolsVm, detailPanel, pair, measure, log,
                record, reportGaps, applyDeletion, expectGroupGone: true, deletedIds);
            if (del.Done)
            {
                var stillThere = (GetProperty(toolsVm, "Groups") as List<DedupGroupRow>)?
                    .Any(g => string.Equals(g.Url, pair.Url, StringComparison.Ordinal)) == true;
                _gate("删除到只剩 1 条后组消失", !stillThere, $"still_in_groups={stillThere}");
            }
        }
        else
        {
            log("   跳过「删除到只剩 1 条」：当前没有 2 条的组");
        }

        // 回主表状态（若仍停在明细，先走真实的返回入口）
        if (detailPanel.Visibility == Visibility.Visible)
        {
            var goBack = typeof(ToolsPage).GetMethod("GoBackToList",
                BindingFlags.Instance | BindingFlags.NonPublic);
            goBack?.Invoke(page, null);
            Pump(100);
        }
        var back = measure(() => { }, () => mainPanel.Visibility == Visibility.Visible && Settle(window, pane), 30000);
        var remaining = GetProperty(toolsVm, "Groups") as List<DedupGroupRow>;
        log($"   回主表：wall={back.Wall}ms 剩余组={remaining?.Count ?? -1}"
            + $" 剩余重复链接={remaining?.Sum(g => g.Count) ?? -1} 行容器={ContainerCount(pane)}");
        record("dedup", "删除后回主表", back.Wall, back.Stall, remaining?.Sum(g => g.Count), ContainerCount(pane),
            $"groups={remaining?.Count ?? -1}");

        // —— 恢复：把本次删掉的链接还回去（回收站恢复），保证同一库可重复跑 ——
        RestoreDeleted(host, deletedIds, log);
    }

    /// <summary>模板行绑定自检：首行必须有内容且不出现缺键哨兵（⟨key⟩）。</summary>
    private static void VerifyRowText(DependencyObject table, Action<string> log, string label)
    {
        var row = FindFirstTagged(table, "DataRow");
        if (row is null) { log($"   跳过{label}文本验证：没有已实现的行"); return; }
        var texts = new List<string>();
        CollectTexts(row, texts);
        var missing = texts.Any(t => t.Contains('⟨'));
        log($"   {label}首行文本：{string.Join(" | ", texts.Take(6))}");
        _gate($"{label}首行文本无缺键", !missing && texts.Any(t => !string.IsNullOrWhiteSpace(t)),
            $"missing_key={missing}; texts={texts.Count}");
    }

    /// <summary>明细行点击 → 选中投影（唯一事实源 = DetailSelection）。</summary>
    private static void VerifyDetailRowClick(DependencyObject detail, ToolsViewModel toolsVm, Action<string> log)
    {
        var row = FindFirstTagged(detail, "DataRow");
        if (row?.DataContext is not DedupLinkRow link) return;
        try
        {
            RaiseRowClick(row);
            Pump(40);
            _gate("点明细行选中", toolsVm.DetailSelection.Contains(link.LinkId) && link.IsSelected,
                $"selected={toolsVm.DetailSelection.HasAny}; row={link.IsSelected}");
        }
        catch (Exception ex)
        {
            log($"   跳过明细行点击：{ex.Message}");
        }
    }

    private static void RaiseRowClick(FrameworkElement row)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        };
        row.RaiseEvent(args);
    }

    /// <summary>勾选按钮走真实模板事件：勾上不触发行选中，再点一次取消。</summary>
    private static void VerifyCheckboxClick(DependencyObject detail, ToolsViewModel toolsVm, Action<string> log)
    {
        var row = FindFirstTagged(detail, "DataRow");
        if (row is null) return;
        var button = FindDescendant<Button>(row);
        if (button?.DataContext is not DedupLinkRow link) { log("   跳过勾选点击：行内没有勾选按钮"); return; }

        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump(40);
        _gate("勾选按钮生效且不选中行",
            toolsVm.CheckedIds.Contains(link.LinkId) && !toolsVm.DetailSelection.HasAny && link.IsChecked,
            $"checked={toolsVm.CheckedIds.Count}; selected={toolsVm.DetailSelection.HasAny}; row={link.IsChecked}");

        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump(40);
        _gate("再次点击取消勾选", toolsVm.CheckedIds.Count == 0 && !link.IsChecked,
            $"checked={toolsVm.CheckedIds.Count}; row={link.IsChecked}");
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } deep) return deep;
        }
        return null;
    }

    private static FrameworkElement? FindFirstTagged(DependencyObject root, string tag)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Tag: string t } fe && t == tag) return fe;
            if (FindFirstTagged(child, tag) is { } hit) return hit;
        }
        return null;
    }

    private static void CollectTexts(DependencyObject root, List<string> texts)
    {
        if (root is TextBlock tb && !string.IsNullOrEmpty(tb.Text)) texts.Add(tb.Text);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            CollectTexts(VisualTreeHelper.GetChild(root, i), texts);
    }

    /// <summary>模板模式不内部排序：点表头必须由页面重排 ItemsSource（默认 URL 升序 + 组数切换）。</summary>
    private static void VerifyMainSort(SortableDataTable pane, Action<string> log)
    {
        var initial = (pane.ItemsSource as System.Collections.IEnumerable)?.Cast<DedupGroupRow>().ToList();
        if (initial is null || initial.Count < 2) return;
        var urls = initial.Select(g => g.Url).ToList();
        _gate("主表默认 URL 升序", urls.SequenceEqual(urls.OrderBy(u => u)),
            $"rows={urls.Count}");

        var countHeader = FindHeader(pane, "count");
        if (countHeader is null) { log("   跳过排序验证：找不到「组数」表头"); return; }

        countHeader.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump(60);
        var ascending = (pane.ItemsSource as System.Collections.IEnumerable)?.Cast<DedupGroupRow>()
            .Select(g => g.Count).ToList() ?? new List<int>();
        _gate("主表按组数升序", ascending.SequenceEqual(ascending.OrderBy(c => c)), $"rows={ascending.Count}");

        countHeader.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump(60);
        var descending = (pane.ItemsSource as System.Collections.IEnumerable)?.Cast<DedupGroupRow>()
            .Select(g => g.Count).ToList() ?? new List<int>();
        _gate("主表按组数降序", descending.SequenceEqual(descending.OrderByDescending(c => c)),
            $"rows={descending.Count}");
    }

    private static SortableHeaderButton? FindHeader(DependencyObject root, string field)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is SortableHeaderButton header && header.Field == field) return header;
            if (FindHeader(child, field) is { } hit) return hit;
        }
        return null;
    }

    private sealed class ScanResult
    {
        public long Wall;
        public long Stall;
        public int Groups;
        public bool SameContent;
    }

    private static ScanResult RunUiScan(
        string label, bool invalidate,
        Func<Action, Func<bool>, int, (long Wall, long Stall)> measure,
        ToolsPage page, SortableDataTable pane, ToolsViewModel toolsVm, MethodInfo runDedup,
        AppHost host, MainWindow window,
        Action<string> log, Action<string, string, long, long, int?, int?, string?> record)
    {
        if (invalidate) host.Client.InvalidateQueryCache();
        var groupsBefore = (GetProperty(toolsVm, "Groups") as List<DedupGroupRow>)?.ToList();
        var sourceBefore = pane.ItemsSource;
        Task? task = null;
        var alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var m = measure(
            () => task = runDedup.Invoke(page, new object[] { false }) as Task,
            () => task is { IsCompleted: true } && Settle(window, pane),
            180000);
        var alloc1 = GC.GetTotalAllocatedBytes(precise: true);
        task?.GetAwaiter().GetResult();
        var groups = GetProperty(toolsVm, "Groups") as List<DedupGroupRow> ?? new List<DedupGroupRow>();
        var sameContent = groupsBefore is not null && DedupGroupRow.SameSequence(groupsBefore, groups);
        log($"   {label}：wall={m.Wall}ms 停摆={m.Stall}ms 组={groups.Count}"
            + $" 重复链接={groups.Sum(g => g.Count)} 行容器={ContainerCount(pane)}"
            + $" 分配={(alloc1 - alloc0) / 1048576.0:F1}MB");
        record("dedup", label, m.Wall, m.Stall, groups.Sum(g => g.Count), ContainerCount(pane),
            $"groups={groups.Count}; alloc_mb={(alloc1 - alloc0) / 1048576.0:F1};"
            + $" same_content={sameContent}; same_source={ReferenceEquals(sourceBefore, pane.ItemsSource)}");
        return new ScanResult { Wall = m.Wall, Stall = m.Stall, Groups = groups.Count, SameContent = sameContent };
    }

    private static (bool Done, int RestCount, bool DeletedStillVisible) DeleteOne(
        MainWindow window, ToolsPage page, ToolsViewModel toolsVm, FrameworkElement detailPanel,
        DedupGroupRow row,
        Func<Action, Func<bool>, int, (long Wall, long Stall)> measure,
        Action<string> log, Action<string, string, long, long, int?, int?, string?> record,
        Action<string> reportGaps, MethodInfo applyDeletion, bool expectGroupGone, List<string> deletedIds)
    {
        // 当前明细必须是目标组；否则先进入它并等组内链接按需加载完成
        if (detailPanel.Visibility != Visibility.Visible
            || !string.Equals(toolsVm.CurrentGroupUrl, row.Url, StringComparison.Ordinal))
        {
            var enter = typeof(ToolsPage).GetMethod("EnterDetailAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var enterTask = enter?.Invoke(page, new object[] { row, false }) as Task;
            PumpUntil(() => enterTask is { IsCompleted: true } && toolsVm.CurrentGroupLinks is not null, 30000);
        }
        else
        {
            PumpUntil(() => toolsVm.CurrentGroupLinks is not null, 30000);
        }

        var victim = row.Links.FirstOrDefault();
        if (victim is null) { log("   跳过：组内链接未加载"); return (false, -1, false); }
        if (!toolsVm.ToggleChecked(victim.LinkId)) { log("   跳过：勾选守卫拒绝"); return (false, -1, false); }
        Task<List<LinkDto>?>? task = null;
        List<LinkDto>? rest = null;
        var applied = false;
        var alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var m = measure(
            () => task = toolsVm.DeleteCheckedAsync(),
            () =>
            {
                if (task is not { IsCompleted: true }) return false;
                if (!applied)
                {
                    applied = true;
                    rest = task.GetAwaiter().GetResult();
                    deletedIds.Add(victim.LinkId);
                    applyDeletion.Invoke(page, new object?[] { rest });
                }
                return Settle(window, (FrameworkElement)GetField(page, "MainPanel")!);
            }, 180000);
        var alloc1 = GC.GetTotalAllocatedBytes(precise: true);
        reportGaps(expectGroupGone ? "删除末条" : "删除一条");

        var groups = GetProperty(toolsVm, "Groups") as List<DedupGroupRow> ?? new List<DedupGroupRow>();
        var deletedStillVisible = groups.SelectMany(g => g.Links).Any(l => l.LinkId == victim.LinkId);
        // 重进当前组（若仍在）验证返回的 CurrentGroupLinks 是局部更新后的集合
        if (rest is not null)
        {
            var updated = groups.FirstOrDefault(g => g.Links.Any(l => l.LinkId == victim.LinkId));
            _ = updated;
            var still = toolsVm.CurrentGroupLinks?.Any(l => l.LinkId == victim.LinkId) == true;
            deletedStillVisible |= still;
        }

        log($"   {(expectGroupGone ? "删除到只剩 1 条" : "删除 1 条（组保留）")}：wall={m.Wall}ms 停摆={m.Stall}ms"
            + $" 剩余={rest?.Count ?? 0} 组数={groups.Count} 行容器={ContainerCount(GetField(page, "DetailTable") as SortableDataTable)}"
            + $" 分配={(alloc1 - alloc0) / 1048576.0:F1}MB 已删项可见={deletedStillVisible}");
        record("dedup", expectGroupGone ? "删除到只剩 1 条" : "删除 1 条（组保留）",
            m.Wall, m.Stall, rest?.Count, ContainerCount(GetField(page, "DetailTable") as SortableDataTable),
            $"groups={groups.Count}; deleted_visible={deletedStillVisible}; alloc_mb={(alloc1 - alloc0) / 1048576.0:F1}");
        return (true, rest?.Count ?? 0, deletedStillVisible);
    }

    private static void RestoreDeleted(AppHost host, List<string> ids, Action<string> log)
    {
        if (ids.Count == 0) return;
        try
        {
            var result = host.Client.TrashRestoreBatchAsync(ids).GetAwaiter().GetResult();
            log($"   收尾：恢复本次删除的 {result.Data?.RestoredLinks ?? 0} 条（库可重复跑）");
        }
        catch (Exception ex)
        {
            log($"   收尾恢复失败（不影响读数）：{ex.Message}");
        }
    }

    // —— 小工具 ——

    private static (long Ms, T Value) MeasureTask<T>(Func<Task<T>> action)
    {
        var sw = Stopwatch.StartNew();
        var task = action();
        PumpUntil(() => task.IsCompleted, 300000);
        sw.Stop();
        return (sw.ElapsedMilliseconds, task.GetAwaiter().GetResult());
    }

    private static bool Settle(MainWindow window, FrameworkElement? scope)
    {
        window.UpdateLayout();
        scope?.UpdateLayout();
        return true;
    }

    private static int ContainerCount(SortableDataTable? table)
    {
        if (table is null) return -1;
        var count = 0;
        void Walk(DependencyObject d)
        {
            if (d is FrameworkElement { Tag: "DataRow" }) count++;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(table);
        return count;
    }

    private static object? GetField(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(target);

    private static object? GetProperty(object target, string name)
        => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(target);

    private static Action<string, bool, string> _gate = (_, _, _) => { };

    private static void Pump(int ms)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(ms), System.Windows.Threading.DispatcherPriority.Render,
            (_, _) => frame.Continue = false, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void PumpUntil(Func<bool> ready, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!ready() && sw.ElapsedMilliseconds < timeoutMs) Pump(5);
    }
}
