using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using LinkPocket;
using LinkPocket.Contracts;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using LinkPocket.Views;

namespace UiBench;

/// <summary>
/// Phase 0：搜索加载模型原型量测。只在本进程里建独立窗口/集合，不接产品 SearchViewModel。
/// 量测目标：
///   1) 旧模型首屏的 extent 跳变（对照证据）；
///   2) count + 视口预取的引擎耗时；
///   3) 100k 占位集合一次 Reset 的 UI 成本、extent、容器数；
///   4) 真实行/占位行高度差；
///   5) 每 10k 页原位填充的 UI 停摆；
///   6) 十万条滚动/末尾可达与内存峰值。
/// </summary>
internal static class SearchLoadProtoSuite
{
    private const int PrefetchSize = 64;
    private const int PageSize = 10_000;
    private const string Query = "页面";

    public static void Run(
        AppHost host,
        Window owner,
        Action<string> log,
        Func<Action, Func<bool>, int, (long Wall, long Stall)> measure,
        Action<string> reportGaps,
        Action<string, string, long, long, int?, int?, string?> record)
    {
        log("");
        log("⑪ searchproto：Phase 0 原型量测（独立窗口，不接产品搜索 VM）");

        CurrentSearchRedProbe(owner, measure, log, record);
        Pump(300);

        var client = host.Client;
        // 从干净的查询缓存开始：基线探针已经跑过宽命中，必须清掉才能量到冷路径。
        client.InvalidateQueryCache();

        var cold = MeasureFirstData(client, log, record);
        if (cold is null)
        {
            log("   跳过原型：count/prefetch 未成功");
            return;
        }
        var (total, prefetch, firstDataMs) = cold.Value;
        log($"   冷 count+prefetch：wall={firstDataMs}ms total={total} prefetch={prefetch.Count}");
        record("searchproto", "冷 count+prefetch", firstDataMs, -1, total, null,
            $"prefetch={prefetch.Count}");

        // 热重复（同一查询仍在 10s TTL 内）。
        var hot = MeasureFirstData(client, log, record);
        if (hot is not null)
        {
            log($"   热 count+prefetch：wall={hot.Value.FirstDataMs}ms total={hot.Value.Total} prefetch={hot.Value.Prefetch.Count}");
            record("searchproto", "热 count+prefetch", hot.Value.FirstDataMs, -1, hot.Value.Total, null,
                $"prefetch={hot.Value.Prefetch.Count}");
        }

        // —— 独立原型窗口：真实 SearchView 行模板（复制同一套视觉），不接 SearchViewModel ——
        var proto = new ProtoWindow(log, measure, record);
        proto.Run(host, owner, total, prefetch);
        proto.RunLazy(host, owner, total, prefetch);
        proto.Close();

        log("   ⑪ 原型量测结束（数据见上方各行与 searchproto 指标）");
    }

    // ───────────────── 旧模型基线证据 ─────────────────

    /// <summary>当前搜索实现：首屏 500 与全量落地时的 extent 对照，量化“范围短暂偏小”。</summary>
    private static void CurrentSearchRedProbe(
        Window owner,
        Func<Action, Func<bool>, int, (long Wall, long Stall)> measure,
        Action<string> log,
        Action<string, string, long, long, int?, int?, string?> record)
    {
        log("");
        log("   ⑪-a 旧模型首屏 extent 对照（Phase 0 基线）");
        if (owner.DataContext is not MainViewModel mvm)
        {
            log("   跳过：主窗口 DataContext 不是 MainViewModel");
            return;
        }

        var view = FindVisualChild<SearchView>(owner);
        if (view is null)
        {
            log("   跳过：取不到 SearchView");
            return;
        }
        if (GetField(view, "SearchBox") is TextBox searchBox)
        {
            // 与 SuiteSearch 同一量法：测量进程内不测 TSF/输入法。
            searchBox.Focusable = false;
            searchBox.IsTabStop = false;
        }

        mvm.SelectNavCommand.Execute(NavIds.Search);
        PumpUntil(() => mvm.CurrentNavId == NavIds.Search, 10000);
        Pump(700);
        owner.UpdateLayout();

        if (view.DataContext is not SearchViewModel svm
            || GetField(view, "ResultsTable") is not SortableDataTable table)
        {
            log("   跳过：取不到 SearchViewModel / ResultsTable");
            return;
        }

        svm.SearchTitle = true;
        svm.SearchUrl = true;
        svm.SearchDesc = true;
        svm.SearchPath = true;
        svm.Query = Query;
        Pump(600);

        var first = measure(
            () => svm.SearchCommand.Execute(null),
            () => !svm.IsSearching && svm.Results.Count > 0,
            180_000);
        Pump(80);
        owner.UpdateLayout();
        var scroller = FindVisualChild<ScrollViewer>(table);
        var extentFirst = scroller?.ExtentHeight ?? -1;
        var rowsFirst = svm.Results.Count;
        var totalFirst = svm.TotalHitCount;
        var containersFirst = CountTagged(table, "DataRow");
        var currentRowHeight = ProtoWindow.MeasureAnyRowHeight(table);
        log($"   当前首屏：wall={first.Wall}ms 停摆={first.Stall}ms Results={rowsFirst} Total={totalFirst}"
            + $" Loading={svm.IsLoadingAll} Extent={extentFirst:F0} 行高={currentRowHeight:F1} 容器={containersFirst}");
        record("searchproto", "当前首屏", first.Wall, first.Stall, rowsFirst, containersFirst,
            $"total={totalFirst}; extent={extentFirst:F0}; loading={svm.IsLoadingAll}");

        // 命令语义对照：等 count 落地后（结果仍只有首屏 500 行），全选只覆盖已到达的 500 条。
        Pump(300);
        var totalAfterCount = svm.TotalHitCount;
        svm.SelectAllCommand.Execute(null);
        var selectedFirst = svm.SelectionCount;
        svm.ClearSelectionCommand.Execute(null);
        log($"   当前首屏 Ctrl+A：selected={selectedFirst}（已加载 {svm.Results.Count} / 总数 {totalAfterCount}"
            + $" Loading={svm.IsLoadingAll}）");
        record("searchproto", "当前首屏 Ctrl+A", -1, -1, svm.Results.Count, null,
            $"selected={selectedFirst}; total={totalAfterCount}; loading={svm.IsLoadingAll}");

        var full = measure(() => { }, () => !svm.IsLoadingAll, 300_000);
        Pump(80);
        owner.UpdateLayout();
        scroller = FindVisualChild<ScrollViewer>(table);
        var extentFull = scroller?.ExtentHeight ?? -1;
        var rowsFull = svm.Results.Count;
        var containersFull = CountTagged(table, "DataRow");
        var jumpPct = extentFirst > 0 ? (extentFull - extentFirst) / extentFirst * 100 : -1;
        var currentRowHeightFull = ProtoWindow.MeasureAnyRowHeight(table);
        log($"   当前全量：wall={full.Wall}ms 停摆={full.Stall}ms Results={rowsFull}/{svm.TotalHitCount}"
            + $" Extent={extentFull:F0} 行高={currentRowHeightFull:F1} 容器={containersFull} extent跳变={jumpPct:F1}%");
        record("searchproto", "当前全量", full.Wall, full.Stall, rowsFull, containersFull,
            $"extent_before={extentFirst:F0}; extent_after={extentFull:F0}; jump_pct={jumpPct:F1}; loading={svm.IsLoadingAll}");

        svm.CancelCommand.Execute(null);
        Pump(300);
        mvm.SelectNavCommand.Execute(NavIds.Browser);
        Pump(300);
    }

    // ───────────────── 引擎第一段数据 ─────────────────

    private static (int Total, List<LinkDto> Prefetch, long FirstDataMs)? MeasureFirstData(
        EngineClient client, Action<string> log,
        Action<string, string, long, long, int?, int?, string?> record)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var countTask = Task.Run(() => client.SearchCountAsync(Query,
                searchTitle: true, searchUrl: true, searchDescription: true, searchPath: true));
            var prefetchTask = Task.Run(() => client.SearchLinksAsync(Query,
                searchTitle: true, searchUrl: true, searchDescription: true, searchPath: true,
                sortBy: "title", sortOrder: "asc", page: 1, perPage: PrefetchSize));
            PumpUntil(() => countTask.IsCompleted && prefetchTask.IsCompleted, 300_000);
            sw.Stop();
            var total = countTask.GetAwaiter().GetResult();
            var prefetch = prefetchTask.GetAwaiter().GetResult();
            return (total, prefetch, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            log($"   ✗ count/prefetch 失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ───────────────── 原型窗口 ─────────────────

    private sealed class ProtoWindow
    {
        private readonly Action<string> _log;
        private readonly Func<Action, Func<bool>, int, (long Wall, long Stall)> _measure;
        private readonly Action<string, string, long, long, int?, int?, string?> _record;
        private Window? _window;
        private SortableDataTable? _table;
        private BulkObservableCollection<LinkItem>? _items;
        private double _firstExtent = -1;
        private int _filled;
        private int _uniqueIds;
        private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
        private double _realRowHeight = -1;
        private double _placeholderRowHeight = -1;

        public ProtoWindow(Action<string> log,
            Func<Action, Func<bool>, int, (long Wall, long Stall)> measure,
            Action<string, string, long, long, int?, int?, string?> record)
        {
            _log = log;
            _measure = measure;
            _record = record;
        }

        public void Run(AppHost host, Window owner, int total, List<LinkDto> prefetch)
        {
            if (total <= 0)
            {
                _log("   跳过原型：总命中为 0");
                return;
            }

            var beforeMem = Memory();
            _log($"   内存(原型前)={beforeMem / 1024 / 1024}MB total={total}");

            // 占位行用 NBSP 占住两行文本高度；先量它和真实行的高度差。
            var sw = Stopwatch.StartNew();
            var placeholders = new List<LinkItem>(total);
            for (var i = 0; i < total; i++)
                placeholders.Add(new ProtoRow { Title = "\u00A0", Url = "\u00A0" });
            sw.Stop();
            var placeholderCreateMs = sw.ElapsedMilliseconds;
            var afterPlaceholders = Memory();
            _log($"   创建 {total} 占位行：{placeholderCreateMs}ms 内存={afterPlaceholders / 1024 / 1024}MB");

            // 先把前 prefetch 行填成真实数据，再一次性挂进表格：模拟“总数已知、首屏真实行已到”。
            var prefetchCount = Math.Min(prefetch.Count, placeholders.Count);
            for (var i = 0; i < prefetchCount; i++)
            {
                ((ProtoRow)placeholders[i]).Fill(prefetch[i], Query);
                if (_seenIds.Add(prefetch[i].LinkId)) _uniqueIds++;
            }

            owner.Hide();
            Pump(200);

            var table = new SortableDataTable
            {
                SelectionEnabled = false,
                RowCacheLength = 0,
                Margin = new Thickness(12, 12, 12, 12),
            };
            table.Columns = BuildColumns();
            table.SortField = "title";
            table.SortAscending = true;
            table.ItemTemplate = (DataTemplate)XamlReader.Parse(RowTemplateXaml);

            var window = new Window
            {
                Width = 1400,
                Height = 860,
                Left = 60,
                Top = 60,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Title = "SearchLoadProto",
                Content = table,
                FontFamily = owner.FontFamily,
                FontSize = owner.FontSize,
                Language = owner.Language,
            };
            window.SetResourceReference(Window.BackgroundProperty, "App.Surface.Base");
            _window = window;
            _table = table;
            _items = new BulkObservableCollection<LinkItem>();
            table.ItemsSource = _items;
            window.Show();
            Pump(400);
            window.UpdateLayout();

            // 初始空表先量一次，确保 Reset 的测量窗只包含 100k 占位数据。
            var initial = _measure(
                () => _items.ReplaceAll(placeholders),
                () => { window.UpdateLayout(); return CountTagged(table, "DataRow") > 0; },
                120_000);
            Pump(80);
            window.UpdateLayout();
            var scroller = FindVisualChild<ScrollViewer>(table);
            _firstExtent = scroller?.ExtentHeight ?? -1;
            var containers = CountTagged(table, "DataRow");
            _filled = prefetchCount;
            _realRowHeight = MeasureRowHeight(table, placeholders[0]);
            _log($"   占位集合首帧：wall={initial.Wall}ms 停摆={initial.Stall}ms rows={_items.Count}"
                + $" 容器={containers} Extent={_firstExtent:F0} Viewport={scroller?.ViewportHeight:F0}");
            _record("searchproto", "100k 占位集合首帧", initial.Wall, initial.Stall, _items.Count, containers,
                $"extent={_firstExtent:F0}; prefetch={prefetchCount}; placeholder_create_ms={placeholderCreateMs}");

            // 占位行高度：滚到第一个未填充的占位行，量同模板下的高度。
            var probeIndex = Math.Min(prefetchCount + 20, _items.Count - 1);
            table.ScrollItemIntoView(_items[probeIndex]);
            Pump(120);
            window.UpdateLayout();
            _placeholderRowHeight = MeasureRowHeight(table, _items[probeIndex]);
            _log($"   行高：真实={_realRowHeight:F1} 占位={_placeholderRowHeight:F1}"
                + $" 差={_placeholderRowHeight - _realRowHeight:F1}");
            _record("searchproto", "真实/占位行高", -1, -1, null, null,
                $"real={_realRowHeight:F1}; placeholder={_placeholderRowHeight:F1}; diff={_placeholderRowHeight - _realRowHeight:F1}");

            // 占位全量滚到底/回来：验证容器数与末尾可达，不触发任何查询。
            FlingAndCheck(table, window, _items, _items.Count - 1, "占位十万条滚到底");
            table.ScrollToVerticalOffset(0);
            Pump(100);

            // 快速甩动**与后台取页并发**：模拟滚动到未填充区时引擎仍在取下一页。
            FlingWhileFetch(host.Client, table, window, scroller);

            // 一页一页取 + 原位填充：fetch（后台）与 fill（UI）分开计时。
            var page = 1;
            long fetchTotal = 0;
            while (_filled < _items.Count)
            {
                List<LinkDto> rows;
                long fetchMs;
                if (page == 1)
                {
                    var timed = TimedPage(host.Client, page);
                    if (timed is null) break;
                    (fetchMs, rows) = timed.Value;
                }
                else
                {
                    var timed = TimedPage(host.Client, page);
                    if (timed is null) break;
                    (fetchMs, rows) = timed.Value;
                }
                fetchTotal += fetchMs;
                if (rows.Count == 0) break;

                var offset = (page - 1) * PageSize;
                var start = Math.Max(0, _filled - offset);
                if (start >= rows.Count) { page++; continue; }

                var fill = _measure(
                    () => FillRange(offset, rows, start),
                    () => { window.UpdateLayout(); return true; },
                    120_000);
                _filled = Math.Min(_items.Count, offset + rows.Count);
                window.UpdateLayout();
                scroller = FindVisualChild<ScrollViewer>(table);
                var extent = scroller?.ExtentHeight ?? -1;
                var pageContainers = CountTagged(table, "DataRow");
                var drift = _firstExtent > 0 ? (extent - _firstExtent) / _firstExtent * 100 : -1;
                _log($"   第 {page} 页：fetch={fetchMs}ms 填充 wall={fill.Wall} 停摆={fill.Stall} "
                    + $"rows={_filled}/{_items.Count} Extent={extent:F0} 漂移={drift:F1}% 容器={pageContainers}");
                _record("searchproto", $"第 {page} 页 fetch", fetchMs, -1, null, null, $"rows={rows.Count}");
                _record("searchproto", $"第 {page} 页原位填充", fill.Wall, fill.Stall,
                    _filled, pageContainers,
                    $"extent={extent:F0}; drift_pct={drift:F1}; fetch_ms={fetchMs}");

                if (rows.Count < PageSize) break;
                page++;
            }

            window.UpdateLayout();
            scroller = FindVisualChild<ScrollViewer>(table);
            var finalExtent = scroller?.ExtentHeight ?? -1;
            var finalDrift = _firstExtent > 0 ? (finalExtent - _firstExtent) / _firstExtent * 100 : -1;
            _log($"   填充结束：rows={_items.Count}/{total} filled={_filled} 唯一ID={_uniqueIds}"
                + $" Extent={finalExtent:F0} 起始={_firstExtent:F0} 漂移={finalDrift:F1}% fetch合计={fetchTotal}ms");
            _record("searchproto", "10 页取数合计", fetchTotal, -1, _items.Count, null,
                $"filled={_filled}; unique={_uniqueIds}; extent={finalExtent:F0}; drift_pct={finalDrift:F1}");

            FlingAndCheck(table, window, _items, _items.Count - 1, "全量十万条滚到底");
            var afterMem = Memory();
            _log($"   内存(原型后)={afterMem / 1024 / 1024}MB 增量={(afterMem - beforeMem) / 1024 / 1024}MB");
            _record("searchproto", "100k 原型内存", -1, -1, _items.Count, null,
                $"before_mb={beforeMem / 1024 / 1024}; after_mb={afterMem / 1024 / 1024}; delta_mb={(afterMem - beforeMem) / 1024 / 1024}");

            window.Close();
            owner.Show();
            Pump(300);
        }

        /// <summary>
        /// 备选架构：**懒创建占位行**的 IList。Count 一开始就是总数，索引器只在 WPF/填充真的访问时
        /// 创建该行；若 WPF 会枚举全表，CreatedCount 会立刻变成 100k（此路线即失效）。
        /// </summary>
        public void RunLazy(AppHost host, Window owner, int total, List<LinkDto> prefetch)
        {
            if (total <= 0) return;
            var beforeMem = Memory();
            var lazy = new LazyRowList(total);
            for (var i = 0; i < Math.Min(prefetch.Count, total); i++)
                lazy.GetOrCreate(i).Fill(prefetch[i], Query);

            owner.Hide();
            Pump(200);
            var table = new SortableDataTable
            {
                SelectionEnabled = false,
                RowCacheLength = 0,
                Margin = new Thickness(12, 12, 12, 12),
            };
            table.Columns = BuildColumns();
            table.SortField = "title";
            table.SortAscending = true;
            table.ItemTemplate = (DataTemplate)XamlReader.Parse(RowTemplateXaml);
            var window = new Window
            {
                Width = 1400,
                Height = 860,
                Left = 60,
                Top = 60,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Title = "SearchLoadLazyProto",
                Content = table,
                FontFamily = owner.FontFamily,
                FontSize = owner.FontSize,
                Language = owner.Language,
            };
            window.SetResourceReference(Window.BackgroundProperty, "App.Surface.Base");
            window.Show();
            Pump(300);
            window.UpdateLayout();

            var initial = _measure(
                () => table.ItemsSource = lazy,
                () => { window.UpdateLayout(); return CountTagged(table, "DataRow") > 0; },
                120_000);
            Pump(80);
            window.UpdateLayout();
            var scroller = FindVisualChild<ScrollViewer>(table);
            var extent = scroller?.ExtentHeight ?? -1;
            var containers = CountTagged(table, "DataRow");
            var realHeight = MeasureRowHeight(table, lazy[0]);
            _log($"   lazy 首帧：wall={initial.Wall}ms 停摆={initial.Stall}ms rows={lazy.Count}"
                + $" 已创建={lazy.CreatedCount} 枚举调用={lazy.EnumeratorCalls}"
                + $" Extent={extent:F0} 行高={realHeight:F1} 容器={containers}");
            _record("searchproto", "lazy 100k 首帧", initial.Wall, initial.Stall, lazy.Count, containers,
                $"created={lazy.CreatedCount}; enumerator_calls={lazy.EnumeratorCalls}; extent={extent:F0}");

            // 滚到底再回来：只应创建视口附近的少量行。
            var maxLayout = 0L;
            foreach (var ratio in new[] { 0.25, 0.75, 0.10, 0.90, 1.0, 0.0 })
            {
                var sw = Stopwatch.StartNew();
                table.ScrollToVerticalOffset(scroller is null ? 0 : scroller.ExtentHeight * ratio);
                window.UpdateLayout();
                sw.Stop();
                if (sw.ElapsedMilliseconds > maxLayout) maxLayout = sw.ElapsedMilliseconds;
                Pump(16);
            }
            _log($"   lazy 滚动：max_layout={maxLayout}ms 已创建={lazy.CreatedCount} 容器={CountTagged(table, "DataRow")}");
            _record("searchproto", "lazy 滚动", -1, maxLayout, lazy.Count, CountTagged(table, "DataRow"),
                $"created={lazy.CreatedCount}");

            long fetchTotal = 0;
            var maxFill = 0L;
            var page = 1;
            var filled = Math.Min(prefetch.Count, total);
            while (filled < total)
            {
                var timed = TimedPage(host.Client, page);
                if (timed is null) break;
                var (fetchMs, rows) = timed.Value;
                fetchTotal += fetchMs;
                if (rows.Count == 0) break;
                var offset = (page - 1) * PageSize;
                var start = Math.Max(0, filled - offset);
                if (start >= rows.Count) { page++; continue; }
                var fill = _measure(
                    () => FillLazyRange(lazy, offset, rows, start, total),
                    () => { window.UpdateLayout(); return true; },
                    120_000);
                if (fill.Stall > maxFill) maxFill = fill.Stall;
                filled = Math.Min(total, offset + rows.Count);
                if (rows.Count < PageSize) break;
                page++;
            }
            window.UpdateLayout();
            scroller = FindVisualChild<ScrollViewer>(table);
            var finalExtent = scroller?.ExtentHeight ?? -1;
            var last = lazy[total - 1];
            table.ScrollToVerticalOffset(finalExtent);
            Pump(120);
            window.UpdateLayout();
            var lastVisible = false;
            void Walk(DependencyObject d)
            {
                if (d is FrameworkElement { Tag: "DataRow" } fe && ReferenceEquals(fe.DataContext, last))
                    lastVisible = true;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
            }
            Walk(table);
            var afterMem = Memory();
            _log($"   lazy 填充：rows={filled}/{total} fetch={fetchTotal}ms max_fill={maxFill}ms"
                + $" 已创建={lazy.CreatedCount} Extent={finalExtent:F0} 末行可见={lastVisible}"
                + $" 内存={afterMem / 1024 / 1024}MB 增量={(afterMem - beforeMem) / 1024 / 1024}MB");
            _record("searchproto", "lazy 填充", fetchTotal, maxFill, filled, null,
                $"created={lazy.CreatedCount}; extent={finalExtent:F0}; last_visible={lastVisible};"
                + $" memory_mb={afterMem / 1024 / 1024}");

            window.Close();
            owner.Show();
            Pump(300);
        }

        /// <summary>懒列表填充：按索引取行（未创建则创建），有绑定则属性通知原位更新。</summary>
        private static void FillLazyRange(LazyRowList lazy, int offset, List<LinkDto> rows, int start, int total)
        {
            for (var i = start; i < rows.Count; i++)
            {
                var index = offset + i;
                if (index >= total) break;
                lazy.GetOrCreate(index).Fill(rows[i], Query);
            }
        }

        public void Close()
        {
            try { _window?.Close(); } catch { /* measurement only */ }
            _window = null;
        }

        /// <summary>只读、按需物化的行列表（WPF 虚拟化数据源试验）。</summary>
        private sealed class LazyRowList : IList, IReadOnlyList<LinkItem>
        {
            private readonly ProtoRow?[] _rows;
            public LazyRowList(int count) => _rows = new ProtoRow?[count];
            public int Count => _rows.Length;
            public int CreatedCount { get; private set; }
            public int EnumeratorCalls { get; private set; }

            public ProtoRow GetOrCreate(int index)
            {
                var row = _rows[index];
                if (row is null)
                {
                    row = new ProtoRow { Title = "\u00A0", Url = "\u00A0" };
                    _rows[index] = row;
                    CreatedCount++;
                }
                return row;
            }

            public LinkItem this[int index] => GetOrCreate(index);
            object? IList.this[int index]
            {
                get => GetOrCreate(index);
                set => throw new NotSupportedException();
            }
            public bool IsReadOnly => true;
            public bool IsFixedSize => true;
            public bool IsSynchronized => false;
            public object SyncRoot => this;
            public int Add(object? value) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public bool Contains(object? value) => value is LinkItem item && IndexOf(item) >= 0;
            public int IndexOf(object? value)
            {
                if (value is not LinkItem item) return -1;
                for (var i = 0; i < _rows.Length; i++)
                    if (ReferenceEquals(_rows[i], item)) return i;
                return -1;
            }
            public void Insert(int index, object? value) => throw new NotSupportedException();
            public void Remove(object? value) => throw new NotSupportedException();
            public void RemoveAt(int index) => throw new NotSupportedException();
            public void CopyTo(Array array, int index)
            {
                for (var i = 0; i < _rows.Length; i++) array.SetValue(GetOrCreate(i), index + i);
            }
            public IEnumerator<LinkItem> GetEnumerator()
            {
                EnumeratorCalls++;
                for (var i = 0; i < _rows.Length; i++) yield return GetOrCreate(i);
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private (long Ms, List<LinkDto> Rows)? TimedPage(EngineClient client, int page)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var task = Task.Run(() => client.SearchLinksAsync(Query,
                    searchTitle: true, searchUrl: true, searchDescription: true, searchPath: true,
                    sortBy: "title", sortOrder: "asc", page: page, perPage: PageSize));
                PumpUntil(() => task.IsCompleted, 300_000);
                sw.Stop();
                return (sw.ElapsedMilliseconds, task.GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                _log($"   ✗ 取第 {page} 页失败：{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>在后台取页期间连续快速滚动：量 UI 停摆、extent 是否稳定、容器是否仍有界。
        /// 取的是第 2 页（后面正式填充会命中缓存，只影响 fetch 计时，不影响填充语义）。</summary>
        private void FlingWhileFetch(EngineClient client, SortableDataTable table, Window window,
            ScrollViewer? scroller)
        {
            if (scroller is null || _items is null) return;
            var task = Task.Run(() => client.SearchLinksAsync(Query,
                searchTitle: true, searchUrl: true, searchDescription: true, searchPath: true,
                sortBy: "title", sortOrder: "asc", page: 2, perPage: PageSize));
            var sw = Stopwatch.StartNew();
            var maxLayout = 0L;
            var iterations = 0;
            var rnd = new Random(12345);
            var extentBefore = scroller.ExtentHeight;
            while (!task.IsCompleted && sw.ElapsedMilliseconds < 30_000)
            {
                var sample = Stopwatch.StartNew();
                table.ScrollToVerticalOffset(scroller.ExtentHeight * rnd.NextDouble());
                window.UpdateLayout();
                sample.Stop();
                if (sample.ElapsedMilliseconds > maxLayout) maxLayout = sample.ElapsedMilliseconds;
                iterations++;
                Pump(8);
            }
            PumpUntil(() => task.IsCompleted, 300_000);
            sw.Stop();
            var extentAfter = FindVisualChild<ScrollViewer>(table)?.ExtentHeight ?? -1;
            var containers = CountTagged(table, "DataRow");
            _log($"   取页期间快速甩动：样本={iterations} 最长布局={maxLayout}ms fetch={sw.ElapsedMilliseconds}ms"
                + $" Extent={extentAfter:F0} 起始={extentBefore:F0} 容器={containers}");
            _record("searchproto", "取页期间快速甩动", sw.ElapsedMilliseconds, maxLayout,
                _items.Count, containers,
                $"samples={iterations}; extent_before={extentBefore:F0}; extent_after={extentAfter:F0}");
            table.ScrollToVerticalOffset(0);
            Pump(80);
        }

        private void FillRange(int offset, List<LinkDto> rows, int start)
        {
            for (var i = start; i < rows.Count; i++)
            {
                var index = offset + i;
                if (index >= _items!.Count) break;
                var dto = rows[i];
                ((ProtoRow)_items[index]).Fill(dto, Query);
                if (_seenIds.Add(dto.LinkId)) _uniqueIds++;
            }
            _filled = Math.Max(_filled, Math.Min(_items!.Count, offset + rows.Count));
        }

        private void FlingAndCheck(SortableDataTable table, Window window,
            IReadOnlyList<LinkItem> items, int lastIndex, string label)
        {
            var scroller = FindVisualChild<ScrollViewer>(table);
            if (scroller is null || items.Count == 0) return;
            var maxLayout = 0L;
            var samples = new[] { 0.25, 0.75, 0.10, 0.90, 0.50, 1.0, 0.0 };
            foreach (var ratio in samples)
            {
                var sw = Stopwatch.StartNew();
                table.ScrollToVerticalOffset(scroller.ExtentHeight * ratio);
                window.UpdateLayout();
                sw.Stop();
                if (sw.ElapsedMilliseconds > maxLayout) maxLayout = sw.ElapsedMilliseconds;
                Pump(16);
            }
            table.ScrollItemIntoView(items[lastIndex]);
            Pump(120);
            window.UpdateLayout();
            var visible = false;
            var containers = 0;
            void Walk(DependencyObject d)
            {
                if (d is FrameworkElement { Tag: "DataRow" } fe)
                {
                    containers++;
                    if (ReferenceEquals(fe.DataContext, items[lastIndex])) visible = true;
                }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
            }
            Walk(table);
            var extent = FindVisualChild<ScrollViewer>(table)?.ExtentHeight ?? -1;
            _log($"   {label}：max_layout={maxLayout}ms 容器={containers} 末行可见={visible} Extent={extent:F0}");
        }

        internal static double MeasureAnyRowHeight(DependencyObject root)
    {
        double height = -1;
        void Walk(DependencyObject d)
        {
            if (height > 0) return;
            if (d is FrameworkElement { Tag: "DataRow" } fe) height = fe.ActualHeight;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return height;
    }

    private static double MeasureRowHeight(DependencyObject root, object dataContext)
        {
            double height = -1;
            void Walk(DependencyObject d)
            {
                if (height > 0) return;
                if (d is FrameworkElement { Tag: "DataRow" } fe && ReferenceEquals(fe.DataContext, dataContext))
                    height = fe.ActualHeight;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
            }
            Walk(root);
            return height;
        }

        private static IReadOnlyList<DataTableColumn> BuildColumns() => new[]
        {
            new DataTableColumn { Field = "title", LabelKey = "ui.noun.name", Width = -2 },
            new DataTableColumn { Field = "path", LabelKey = "ui.noun.location", Width = -3 },
            new DataTableColumn { Field = "updated_at", LabelKey = "ui.noun.updatedAt", Width = 114 },
            new DataTableColumn { Field = "last_visited_at", LabelKey = "ui.noun.lastVisited", Width = 114 },
            new DataTableColumn { Field = "visit_count", LabelKey = "ui.noun.visitCount", Width = 72 },
            new DataTableColumn { Field = "created_at", LabelKey = "ui.noun.createdAt", Width = 114 },
        };

        private const string RowTemplateXaml = """
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:views="clr-namespace:LinkPocket.Views;assembly=LinkPocket.UIKit"
              xmlns:local="clr-namespace:LinkPocket.Views;assembly=LinkPocket.UI.Search">
  <Border Tag="DataRow" HorizontalAlignment="Stretch">
    <Border.Style>
      <Style TargetType="Border" BasedOn="{StaticResource LpRowCard}">
        <Setter Property="Margin" Value="8,1"/>
        <Setter Property="Padding" Value="16,9"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="SnapsToDevicePixels" Value="True"/>
        <Style.Triggers>
          <DataTrigger Binding="{Binding IsSelected}" Value="True">
            <Setter Property="Background" Value="{DynamicResource App.Surface.Selected}"/>
          </DataTrigger>
        </Style.Triggers>
      </Style>
    </Border.Style>
    <Grid VerticalAlignment="Center">
      <Grid.ColumnDefinitions>
        <ColumnDefinition views:ColumnWidthBinding.Width="{Binding ColumnWidths[0], RelativeSource={RelativeSource AncestorType=views:SortableDataTable}}"/>
        <ColumnDefinition views:ColumnWidthBinding.Width="{Binding ColumnWidths[1], RelativeSource={RelativeSource AncestorType=views:SortableDataTable}}"/>
        <ColumnDefinition views:ColumnWidthBinding.Width="{Binding ColumnWidths[2], RelativeSource={RelativeSource AncestorType=views:SortableDataTable}}"/>
        <ColumnDefinition views:ColumnWidthBinding.Width="{Binding ColumnWidths[3], RelativeSource={RelativeSource AncestorType=views:SortableDataTable}}"/>
        <ColumnDefinition views:ColumnWidthBinding.Width="{Binding ColumnWidths[4], RelativeSource={RelativeSource AncestorType=views:SortableDataTable}}"/>
        <ColumnDefinition views:ColumnWidthBinding.Width="{Binding ColumnWidths[5], RelativeSource={RelativeSource AncestorType=views:SortableDataTable}}"/>
      </Grid.ColumnDefinitions>
      <Grid Grid.Column="0" VerticalAlignment="Center">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="Auto"/>
          <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <local:SearchFaviconImage Grid.Column="0" Url="{Binding FaviconUrl}" Width="18" Height="18"
                                  Margin="0,0,10,0" VerticalAlignment="Center"/>
        <StackPanel Grid.Column="1" VerticalAlignment="Center">
          <local:HighlightTextBlock RowText="{Binding DisplayTitle}" HighlightQuery="{Binding HighlightQuery}"
                                    FontSize="14" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
          <local:HighlightTextBlock RowText="{Binding Url}" HighlightQuery="{Binding HighlightQuery}"
                                    NormalBrushKey="App.Text.Secondary" FontSize="11.5" Margin="0,3,0,0"
                                    TextTrimming="CharacterEllipsis"/>
        </StackPanel>
      </Grid>
      <TextBlock Grid.Column="1" FontSize="12.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"
                 Foreground="{DynamicResource App.Text.Secondary}" Text="{Binding ListId}"/>
      <local:LocalizedTextBlock Grid.Column="2" VerticalAlignment="Center" FontSize="12.5"
                                TextTrimming="CharacterEllipsis" TextValue="{Binding UpdatedValue}"/>
      <local:LocalizedTextBlock Grid.Column="3" VerticalAlignment="Center" FontSize="12.5"
                                TextTrimming="CharacterEllipsis" TextValue="{Binding LastVisitedValue}"/>
      <local:LocalizedTextBlock Grid.Column="4" VerticalAlignment="Center" FontSize="12.5"
                                TextTrimming="CharacterEllipsis" TextValue="{Binding VisitCountValue}"/>
      <local:LocalizedTextBlock Grid.Column="5" VerticalAlignment="Center" FontSize="12.5"
                                TextTrimming="CharacterEllipsis" TextValue="{Binding CreatedValue}"/>
    </Grid>
  </Border>
</DataTemplate>
""";
    }

    // ───────────────── 行模型 ─────────────────

    /// <summary>原型行：用真实 LinkItem + 原位填充；只在本量测进程内存在。</summary>
    private sealed class ProtoRow : LinkItem
    {
        public void Fill(LinkDto dto, string query)
        {
            LinkId = dto.LinkId;
            Url = dto.Url ?? string.Empty;
            Title = dto.Title ?? string.Empty;
            Description = dto.Description ?? string.Empty;
            FaviconUrl = string.Empty;   // 避免异步图标解码混入填充量测（图标尺寸固定）
            ListId = dto.ListId;
            LastVisitedAt = dto.LastVisitedAt;
            VisitCount = dto.VisitCount;
            IsImportant = dto.IsImportant;
            CreatedAt = dto.CreatedAt;
            UpdatedAt = dto.UpdatedAt;
            HighlightQuery = query;

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

    // ───────────────── 本套件独立量具 ─────────────────

    private static long Memory()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(true);
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Render,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void PumpUntil(Func<bool> ready, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!ready() && sw.ElapsedMilliseconds < timeoutMs) Pump(5);
    }

    private static int CountTagged(DependencyObject root, string tag)
    {
        var n = 0;
        void Walk(DependencyObject d)
        {
            if (d is FrameworkElement { Tag: string t } && t == tag) n++;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return n;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindVisualChild<T>(child) is { } deep) return deep;
        }
        return null;
    }

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?.GetValue(target);
}
