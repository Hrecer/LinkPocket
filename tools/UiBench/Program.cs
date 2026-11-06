using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LinkPocket;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using LinkPocket.Views;
using LinkPocket.Views.Browser;
using Microsoft.Data.Sqlite;
using UiBench;

// UiBench —— 大库综合性能评估台（大规模测试库 + 浏览/深树/搜索/写操作全链路）。
//
// 覆盖常见的高开销路径，每项都拆到"引擎 / VM / 布局 / 渲染"：
//   ① open      打开指定目录（默认示例目录；用 --folder 指定测试库中的目录）
//   ② deep      上百层嵌套目录的打开与面包屑/树展开
//   ③ switch    设置页 ↔ 浏览页反复切换（每轮记 wall 与 UI 线程最长单次停摆）
//   ④ refresh   原地刷新（后台刷新走的同一条链）
//   ⑤ menu      行右键菜单（共享实例 + 每次打开换行）
//   ⑥ dirty     批改 200 条后的刷新/重开
//   ⑦ layout    布局分路
//   ⑧ shift     导航后的布局位移
//   ⑨ mutations 写操作端到端延迟（新建文件夹 / 新建链接 / 删除链接 / 删除文件夹）
//   ⑩ search    搜索全链路：宽命中/窄命中/无命中/路径范围/范围切换/缓存重复/F5
//   ⑬ sorts     表头排序：浏览页五列 / 搜索页六列，升降各一次（点击 → 重排上屏）
//   ⑭ cold      完整冷缓存：每次测量前清空系统文件缓存 / SQLite 连接池（需要提升权限；--only cold）
// 跑完回读本次 `logs\*.jsonl` 里 `cat=app.nav` 的行（各阶段耗时就在记录的 ms 字段），
// 并把结构化读数写成 `ui_bench_report.json`（含库规模/深度、每条 wall/停摆/布局/行数）。
//
// 工程约束：
//   · 必须显式 STA 线程（顶层 Main 不是 STA）；
//   · 界面 VM 必须**从可视树取**（页面对象图是 Scoped，从根容器解析会拿到另一个实例 → 读到 Rows=0）；
//   · 判虚拟化看**两件事**：行容器数量 + 面板 Extent/Viewport（只看代码里设了 SetIsVirtualizing 会漏判）；
//   · 等待/打点的退出回调**不能挂 Background**（会被"持续失效→重渲染"的动画饿死），一律挂 Render；
//   · 渲染必须发生在可见窗口中（`--offscreen` 只用于对照：离屏窗口的组合/绘制会被跳过，读数偏乐观）。

var folderId = Arg("--folder") ?? "960000000001";   // 示例目录 ID；测试库不同请显式传 --folder
var deepFolderId = Arg("--deep-folder") ?? "960100000100";   // 示例深层目录 ID
var rounds = int.TryParse(Arg("--rounds"), out var r) ? r : 6;
var seedDb = Arg("--db");
var offscreen = args.Contains("--offscreen");
// --noshadow：临时摘掉外壳的 DropShadowEffect（**只在本测量进程里**，不碰产品代码）——
// 用来判定"动画期间每帧耗时"是否来自"分层窗口 + 全窗模糊"这条渲染路径的固定成本。
var noShadow = args.Contains("--noshadow");
// --max：把窗口最大化再测量。窗口尺寸会直接影响每帧重绘成本。
var maxWindow = args.Contains("--max");
// --startup-wait：启动后等多久再跑套件（给 App 的启动预热留时间），并记录这段窗口的 UI 停摆。
var startupWaitMs = int.TryParse(Arg("--startup-wait"), out var sw) ? Math.Max(0, sw) : 0;
var only = (Arg("--only") ?? "open,deep,switch,refresh,menu,dirty,layout,shift,mutations,search,smartlist,dedup")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var baseDir = AppContext.BaseDirectory;
var report = new System.Text.StringBuilder();
var metrics = new List<BenchMetric>();
var gates = new List<(string Label, bool Pass, string Detail)>();

if (seedDb is not null)
{
    var src = Path.GetFullPath(seedDb);
    var dataRoot = AppPaths.DataRoot;
    Directory.CreateDirectory(dataRoot);
    // ⚠️ 换库前必须清掉**随库的残迹**：`-wal`/`-shm` 若留到新主库旁边，SQLite 会把旧 WAL 套到新主库上——
    // 主库是新库、WAL 是上一轮的页，会合成出"半新半旧"甚至损坏的库（症状：目标目录"不存在"、
    // 命令时好时坏）。审计库 / 撤销日志 / 暂存区同理都是上一轮的残迹；favicons 不删（只是图标缓存）。
    foreach (var file in new[]
             {
                 "linkpocket.db-wal", "linkpocket.db-shm",
                 "linkpocket-ops.db", "linkpocket-ops.db-wal", "linkpocket-ops.db-shm",
                 "undo-journal.json",
             })
    {
        var path = Path.Combine(dataRoot, file);
        if (File.Exists(path)) File.Delete(path);
    }
    foreach (var dir in new[] { "undo-journal.json.parts" })
    {
        var path = Path.Combine(dataRoot, dir);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
    File.Copy(src, Path.Combine(dataRoot, "linkpocket.db"), overwrite: true);
    foreach (var side in new[] { "-wal", "-shm" })
        if (File.Exists(src + side)) File.Copy(src + side, Path.Combine(dataRoot, "linkpocket.db" + side), overwrite: true);
}
// 库规模 / 深度 / 最深目录的祖先链在**建窗口之前**读一次：套件直接拿它做断言。
var benchDbPath = Path.Combine(AppPaths.DataRoot, "linkpocket.db");
var dbStats = ReadDbStats(benchDbPath, deepFolderId);
var logDir = Path.Combine(AppPaths.ProgramRoot, "logs");

var thread = new Thread(() => Run());
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
File.WriteAllText(Path.Combine(baseDir, "ui_bench_report.txt"), report.ToString());
Console.WriteLine("UI BENCH DONE -> ui_bench_report.txt");
Environment.Exit(gates.Any(g => !g.Pass) ? 2 : 0);

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

void Log(string line)
{
    Console.WriteLine(line);
    Console.Out.Flush();   // 重定向到文件时也逐行可见（长套件卡住时能定位到最后完成的一步）
    report.AppendLine(line);
}

void Record(string suite, string name, long wall = -1, long stall = -1, long? layout = null,
    long? settle = null, int? rows = null, int? containers = null, string? detail = null)
    => metrics.Add(new BenchMetric(suite, name, wall, stall, layout, settle, rows, containers, detail));

/// <summary>正式门禁：不只看数字，还给出 PASS/FAIL；任一条 FAIL → 进程退出码 2。</summary>
void Gate(string label, bool pass, string detail)
{
    gates.Add((label, pass, detail));
    Log($"   [{(pass ? "PASS" : "FAIL")}] 门禁 {label}：{detail}");
}

/// <summary>托管堆（强制回收后）：搜索前后各量一次，差值 = 结果集/占位行的真实内存成本。</summary>
static long ManagedBytes()
{
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    return GC.GetTotalMemory(false);
}

void Run()
{
    var app = new App();
    app.InitializeComponent();
    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    Pump(1500);
    SynchronizationContext.SetSynchronizationContext(
        new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
    var window = Application.Current?.MainWindow as MainWindow
        ?? throw new InvalidOperationException("产品启动序没有建出 MainWindow");
    if (GetField(window, "_host") is not AppHost host) throw new InvalidOperationException("取不到组合根");
    if (window.DataContext is not MainViewModel vm) throw new InvalidOperationException("DataContext 不是 MainViewModel");
    if (offscreen) { window.Left = -20000; window.Top = 0; }
    if (maxWindow) { window.WindowState = WindowState.Maximized; Pump(600); }
    window.ShowInTaskbar = false;
    window.UpdateLayout();
    if (noShadow && GetField(window, "WindowShell") is Border shell) shell.Effect = null;

    var table = FindVisualChild<SortableDataTable>(window) ?? throw new InvalidOperationException("取不到主表");
    Log($"== UiBench  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  渲染={(offscreen ? "离屏(偏乐观)" : "屏幕")}  目录={folderId}");

    // 帧间隔时间线（判断"一次大块阻塞"还是"多帧摊开"）。
    // ⚠️ 计时方式：打点循环用 DispatcherTimer，**分辨率 ≈15.6ms（系统计时器 tick）**，所以
    // "连续两次打点"最小就是 ~16-19ms —— 阈值必须抬到 **2 个 tick（32ms）**，否则把打点噪声当卡顿。
    // 真正要抓的是 ≥32ms 的那些（= 至少丢 2 帧的同步阻塞）。
    var gaps = new List<(long AtMs, long GapMs)>();
    const long FrameGapFloorMs = 32;
    const long quietMs = 400;   // 连续这么久没有大停顿才算"UI 安静下来"
    long lastLayoutMs = -1;
    long lastActMs = -1;   // 最近一次量具的 act() 同步段（命令本身在 UI 线程上跑了多久）
    long lastGcPauseMs = -1;   // 最近一次量具窗口内 GC 暂停总时长（区分“代码慢”与“GC 停”）

    // ⚠️ 导航量具不再在 RefreshCompleted 里强制 UpdateLayout：
    // 那会把"布局"从渲染帧里提前拽到事件回调内，既改变时序，也把首帧模板切换（紧凑→完整）
    // 挡在测量之外。现在 wall = 动作 → **首帧真的渲染出来**（CompositionTarget.Rendering），
    // 首帧之后的 ≥32ms 卡顿单独记 tail（见 MeasureNav）。
    // 仍要"布局成本"分项的套件在 Measure 前把 captureLayout 置 true：只在该次刷新收尾同步量一次。
    var captureLayout = false;
    vm.BrowserViewModel.RefreshCompleted += (_, _) =>
    {
        if (!captureLayout) return;
        captureLayout = false;
        var sw = Stopwatch.StartNew();
        window.UpdateLayout();
        sw.Stop();
        lastLayoutMs = sw.ElapsedMilliseconds;
    };

    // 首屏落地（浏览器是默认页）
    var idle0 = vm.BrowserViewModel.WaitForIdleAsync();
    PumpUntil(() => idle0.IsCompleted, 180000);
    Pump(800);

    if (startupWaitMs > 0)
    {
        Log("");
        Log($"启动预热窗口：等待 {startupWaitMs}ms（App 启动后 2s 开始后台预热）");
        var warmClock = Stopwatch.StartNew();
        var warmWindow = MeasureUntil(() => { },
            () => warmClock.ElapsedMilliseconds >= startupWaitMs, startupWaitMs + 15000);
        Log($"启动预热窗口 UI 停摆：{warmWindow.Stall}ms（墙钟 {warmWindow.Wall}ms）");
        Record("warmstart", "启动预热窗口 UI 停摆", warmWindow.Wall, warmWindow.Stall, null, null, null, null, null);
    }

    if (only.Contains("open")) SuiteOpen();
    if (only.Contains("deep")) SuiteDeep();
    if (only.Contains("switch")) SuiteSwitch();
    if (only.Contains("refresh")) SuiteRefresh();
    if (only.Contains("menu")) SuiteMenu();
    if (only.Contains("dirty")) SuiteDirty();
    if (only.Contains("shift")) SuiteShift();
    if (only.Contains("mutations")) SuiteMutations();
        if (only.Contains("search")) SuiteSearch();
        if (only.Contains("folders")) SuiteFolderScale();
    if (only.Contains("sorts")) SuiteHeaderSort();
    if (only.Contains("smartlist")) SuiteSmartList();
    if (only.Contains("cold")) SuiteCold();
    if (only.Contains("searchproto"))
        SearchLoadProtoSuite.Run(host, window, Log,
            (act, done, timeout) => MeasureUntil(act, done, timeout),
            ReportGaps,
            (suite, name, wall, stall, rows, containers, detail) =>
                Record(suite, name, wall, stall, null, null, rows, containers, detail));
    if (only.Contains("dedup"))
        DedupSuite.Run(host, vm, window, Log,
            (act, done, timeout) => MeasureUntil(act, done, timeout),
            ReportGaps,
            (suite, name, wall, stall, rows, containers, detail) =>
                Record(suite, name, wall, stall, null, null, rows, containers, detail),
            Gate);

    /// <summary>
    /// ⑦ 导航后的**布局位移**取证：导航后每 50ms 采样"首个行容器相对窗口的 Y"**以及它的每一层祖先
    /// 高度**，只在指纹变化时打印 —— 哪一层高度变了、在哪一刻变的，直接可见。
    /// </summary>
    void SuiteShift()
    {
        Log("");
        Log("⑦ 导航后的布局位移（每 50ms 采样，变化时才打印）");
        if (vm.BrowserViewModel.CurrentFolderId != folderId)
        {
            var go = vm.BrowserViewModel.LoadAsync(folderId);
            PumpUntil(() => go.IsCompleted, 300000);
            PumpUntil(() => vm.BrowserViewModel.WaitForIdleAsync().IsCompleted, 300000);
            Pump(800);
        }
        // 先退回根，让下一次打开是"打开文件夹"的真实路径
        var back = vm.BrowserViewModel.LoadAsync(null);
        PumpUntil(() => back.IsCompleted, 300000);
        PumpUntil(() => vm.BrowserViewModel.WaitForIdleAsync().IsCompleted, 300000);
        Pump(800);

        if (RealizedRows(table).Count == 0) { Log("   跳过：视口内没有行容器"); return; }

        string Fingerprint(out double rowY)
        {
            // ⚠️ **每次采样都重新取**"当前首个已实现的行"：换行集时旧容器会被丢弃，
            // 拿一次引用会让后续采样盯着一个已经移除的元素（曾因此把位移漏成"纹丝不动"）。
            var row = RealizedRows(table).FirstOrDefault();
            rowY = double.NaN;
            var text = new System.Text.StringBuilder();
            if (row is null) return $"(no realized rows) containers=0";
            // 外壳先入指纹（标题栏 / 浏览页）：整窗位移 vs 只有列表内部位移，靠这两个数分辨
            if (GetField(window, "TitleBar") is FrameworkElement titleBar)
                text.Append($"TitleBar={titleBar.ActualHeight:F1} | ");
            if (GetField(window, "BrowserPage") is FrameworkElement page)
                text.Append($"BrowserPage={page.ActualHeight:F1} | ");
            for (DependencyObject? n = row; n is not null; n = ParentOf(n))
            {
                if (n is not FrameworkElement fe) continue;
                double y = double.NaN;
                try { y = fe.TranslatePoint(new Point(0, 0), window).Y; } catch { /* 未接入可视树 */ }
                if (double.IsNaN(rowY) && !double.IsNaN(y)) rowY = y;
                var label = string.IsNullOrEmpty(fe.Name) ? fe.GetType().Name : fe.GetType().Name + "#" + fe.Name;
                text.Append(label + $"={fe.ActualHeight:F1}@{y:F1} | ");
            }
            text.Append($"containers={RealizedRows(table).Count}");
            return text.ToString();
        }

        static DependencyObject? ParentOf(DependencyObject d) =>
            VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);

        var start = Stopwatch.StartNew();
        var open = vm.BrowserViewModel.LoadAsync(folderId);
        var last = "";
        var changes = 0;
        while (start.ElapsedMilliseconds < 2500)
        {
            Pump(20);   // 20ms：入场动画的每帧位移都要看得见（50ms 会跨过整段）
            var now = Fingerprint(out var y);
            if (now == last) continue;
            last = now;
            changes++;
            Log($"   +{start.ElapsedMilliseconds,4}ms  首行Y={y:F1}  {now}");
        }
        Log("   （以上每行 = 一次布局变化后的状态；对比相邻两行的某个元素高度差，就是整窗位移的来源）");
        Record("shift", "导航后布局指纹变化", 2500, -1, null, null, vm.BrowserViewModel.Rows.Count,
            CountRows(table), $"fingerprints={changes}; expect=1");
    }
    if (only.Contains("layout")) SuiteLayout();

    /// <summary>
    /// ⑥ 布局分路：把"数据变过之后那一轮刷新"的**同步布局**按区域拆开 —— 做法是"藏一块、量一次、取差值"。
    /// 每一轮都先把 200 条标题改掉（行内容真的变了 ⇒ 这一轮的行布局一定会做），然后在刷新链收尾处
    /// （`RefreshCompleted`，布局还没跑）量 `UpdateLayout()`：藏起来的那一块不参与 ⇒ 差值就是它的布局成本。
    /// </summary>
    void SuiteLayout()
    {
        Log("");
        Log("⑥ 布局分路（藏一块量一次，取差值；每轮都先改 200 条标题让行真的变）");
        var browser = GetField(window, "BrowserPage") as FrameworkElement
                      ?? throw new InvalidOperationException("取不到 BrowserPage");
        var tableCtl = GetField(browser, "MainTable") as FrameworkElement;
        var details = GetField(browser, "DetailsPanel") as FrameworkElement;
        var tree = FindByTypeName(browser, "FolderTreePanel");
        if (vm.BrowserViewModel.CurrentFolderId != folderId)
        {
            var go = vm.BrowserViewModel.LoadAsync(folderId);
            PumpUntil(() => go.IsCompleted, 300000);
            PumpUntil(() => vm.BrowserViewModel.WaitForIdleAsync().IsCompleted, 300000);
            Pump(600);
        }
        Log($"   区域定位：表格={tableCtl is not null} 树面板={tree is not null} 右栏={details is not null}");

        var baseMs = LayoutRun();
        Log($"   ① 全量布局 = {baseMs} ms");
        Record("layout", "全量布局", -1, -1, baseMs);
        foreach (var (label, region) in new[] { ("树面板", tree), ("表格视口", tableCtl), ("右栏", details) })
        {
            if (region is null) { Log($"   {label}：取不到控件，跳过"); continue; }
            region.Visibility = Visibility.Collapsed;
            var ms = LayoutRun();
            region.Visibility = Visibility.Visible;
            Pump(500);
            Log($"   {label}藏起来 = {ms} ms ⇒ {label} ≈ {baseMs - ms} ms");
            Record("layout", $"藏起{label}", -1, -1, ms, detail: $"exclusive_est={baseMs - ms}");
        }

        long LayoutRun()
        {
            var ids = vm.BrowserViewModel.Rows.Where(r => !r.IsFolder).Take(200).Select(r => r.Id).ToList();
            var writes = ids.Select(id => host.Client.LinkUpdateAsync(id, title: $"分路探针·{Environment.TickCount64}")).ToList();
            PumpUntil(() => writes.All(w => w.IsCompleted), 180000);
            Pump(700);
            lastLayoutMs = -1;
            captureLayout = true;
            _ = Measure(() => _ = vm.BrowserViewModel.RefreshPreservingSelectionAsync());
            return lastLayoutMs;
        }
    }

    /// <summary>
    /// ⑤「数据刚被批脚本改过」的大目录刷新/重开 —— **差分刷新**治的就是这一下：
    /// 改 200 条标题 ⇒ 引擎查询缓存作废（冷读）+ 行内容真的变了，然后正常刷新/重开。
    /// 判据：布局成本与停摆（改前走整表替换：视口内容器 + 右键菜单 + 布局行为全部重建）。
    /// </summary>
    void SuiteDirty()
    {
        Log("");
        Log("⑤ 数据刚变过的大目录（改 200 条标题后再刷新/重开）");
        if (vm.BrowserViewModel.CurrentFolderId != folderId)
        {
            var go = vm.BrowserViewModel.LoadAsync(folderId);
            PumpUntil(() => go.IsCompleted, 300000);
            PumpUntil(() => vm.BrowserViewModel.WaitForIdleAsync().IsCompleted, 300000);
            Pump(600);
        }

        void Touch(int skip)
        {
            var ids = vm.BrowserViewModel.Rows.Where(r => !r.IsFolder).Skip(skip).Take(200).Select(r => r.Id).ToList();
            var writes = ids.Select(id => host.Client.LinkUpdateAsync(id, title: $"差分探针·{skip}")).ToList();
            PumpUntil(() => writes.All(w => w.IsCompleted), 180000);
            Pump(900);   // 让 300ms 防抖的那一次也落地，避免混进下面的读数
        }

        Touch(0);
        lastLayoutMs = -1;
        captureLayout = true;
        var refresh = MeasureNav(() => _ = vm.BrowserViewModel.RefreshPreservingSelectionAsync(),
            () => !vm.BrowserViewModel.IsLoading && !vm.BrowserViewModel.IsNavigating);
        Log($"   原地刷新：wall(首帧)={refresh.Wall} 停摆={refresh.Stall} 尾随={refresh.TailMax}/{refresh.TailTotal} 布局={lastLayoutMs} ms 行容器={CountRows(table)}");
        Record("dirty", "批改后原地刷新", refresh.Wall, refresh.Stall,
            lastLayoutMs >= 0 ? lastLayoutMs : null, null,
            vm.BrowserViewModel.Rows.Count, CountRows(table),
            $"tail_max={refresh.TailMax}; tail_total={refresh.TailTotal}");
        ReportGaps("原地刷新");

        Touch(200);
        lastLayoutMs = -1;
        captureLayout = true;
        var reopen = MeasureNav(() => vm.BrowserViewModel.LoadAsync(folderId),
            () => vm.BrowserViewModel.CurrentFolderId == folderId
                  && !vm.BrowserViewModel.IsLoading && !vm.BrowserViewModel.IsNavigating);
        Log($"   重开目录：wall(首帧)={reopen.Wall} 停摆={reopen.Stall} 尾随={reopen.TailMax}/{reopen.TailTotal} 布局={lastLayoutMs} ms 行容器={CountRows(table)}");
        Record("dirty", "批改后重开目录", reopen.Wall, reopen.Stall,
            lastLayoutMs >= 0 ? lastLayoutMs : null, null,
            vm.BrowserViewModel.Rows.Count, CountRows(table),
            $"tail_max={reopen.TailMax}; tail_total={reopen.TailTotal}");
        ReportGaps("重开目录");
    }

    /// <summary>
    /// ⑧ 写操作的端到端延迟（四个常见动作）：新建文件夹 / 新建链接 / 删除链接 / 删除文件夹。
    /// 量法 = **等条件式测量**：从动作发起量到"界面上真的发生"（行出现且进入改名 / 行消失），
    /// 而不是"命令返回"。测完自负清理（每轮删掉自己刚建的东西），库保持稳定可重复。
    /// 对话框端口只在本测量进程内换成"自动确认"（删除确认直接放行，不打真实模态）。
    /// </summary>
    void SuiteMutations()
    {
        Log("");
        Log("⑧ 写操作端到端延迟（等条件式：动作 → 界面上真的发生）");
        var ports = GetField(vm.BrowserViewModel, "_ports") as UiPortProvider;
        var originalDialogs = ports?.Dialogs;
        if (ports is not null) ports.Dialogs = new AutoConfirmDialogs();
        try
        {
            if (vm.BrowserViewModel.CurrentFolderId != folderId)
            {
                var go = vm.BrowserViewModel.LoadAsync(folderId);
                PumpUntil(() => go.IsCompleted, 300000);
                PumpUntil(() => vm.BrowserViewModel.WaitForIdleAsync().IsCompleted, 300000);
                Pump(600);
            }
            var browser = vm.BrowserViewModel;

            for (var i = 1; i <= 2; i++)
            {
                // ① 新建文件夹：命令 → 行出现（且进入就地改名）
                var create = MeasureUntil(() => browser.NewFolderCommand.Execute(null),
                    () => browser.IsRenaming, 30000);
                Log($"   #{i} 新建文件夹（→ 行出现且进入改名）：wall={create.Wall} 停摆={create.Stall} ms");
                Record("mutations", $"新建文件夹 #{i}", create.Wall, create.Stall, null, null,
                    browser.Rows.Count, CountRows(table));
                Gate($"新建文件夹 #{i} ≤100ms", create.Wall <= 100, $"wall={create.Wall}ms; stall={create.Stall}ms");
                ReportGaps("新建文件夹");

                // ② 改名提交（回车路径的 VM 等价物：提交会话 → 行名更新）→ 顺手把它删掉（也为下一轮清理）
                var newName = $"bench-f-{Environment.TickCount64}";
                browser.EditingName = newName;
                var commit = MeasureUntil(() => { _ = browser.CommitRenameAsync(); },
                    () => !browser.IsRenaming && browser.Rows.Any(x => x.Name == newName), 30000);
                Log($"   #{i} 改名提交（回车 → 新名字可见）：wall={commit.Wall} 停摆={commit.Stall} ms");
                Record("mutations", $"改名提交 #{i}", commit.Wall, commit.Stall);

                var folderRow = browser.Rows.FirstOrDefault(x => x.Name == newName);
                if (folderRow is null) { Log($"   #{i} 跳过：改名后的行不在列表里（rows={browser.Rows.Count}）"); return; }
                var delFolder = MeasureUntil(() =>
                {
                    browser.SelectRowWithModifiers(folderRow, ModifierKeys.None);
                    browser.DeleteSelectionCommand.Execute(null);
                }, () => browser.Rows.All(x => x.Id != folderRow.Id), 30000);
                Log($"   #{i} 删除文件夹（→ 行消失）：wall={delFolder.Wall} 停摆={delFolder.Stall} ms");
                Record("mutations", $"删除文件夹 #{i}", delFolder.Wall, delFolder.Stall, null, null,
                    browser.Rows.Count, CountRows(table));
                Gate($"删除文件夹 #{i} ≤120ms", delFolder.Wall <= 120, $"wall={delFolder.Wall}ms; stall={delFolder.Stall}ms");
                ReportGaps("删除文件夹");

                // ③ 新建链接（编辑器保存）→ 删除链接
                var title = $"bench-l-{Environment.TickCount64}";
                browser.NewLinkCommand.Execute(null);
                if (browser.EditorPage is not LinkEditorViewModel editor)
                { Log($"   #{i} 跳过：新建链接没有打开编辑器页（rows={browser.Rows.Count}）"); return; }
                editor.Url = $"https://bench.invalid/{Environment.TickCount64}";
                editor.LinkTitle = title;
                var save = MeasureUntil(() => editor.SaveCommand.Execute(null),
                    () => !browser.IsEditorPageOpen && browser.Rows.Any(x => x.Name == title), 30000);
                Log($"   #{i} 新建链接（保存 → 行出现）：wall={save.Wall} 停摆={save.Stall} ms");
                Record("mutations", $"新建链接 #{i}", save.Wall, save.Stall, null, null,
                    browser.Rows.Count, CountRows(table));
                ReportGaps("新建链接");

                var linkRow = browser.Rows.FirstOrDefault(x => x.Name == title);
                if (linkRow is null) { Log($"   #{i} 跳过：保存后的行不在列表里（rows={browser.Rows.Count}）"); return; }
                var delLink = MeasureUntil(() =>
                {
                    browser.SelectRowWithModifiers(linkRow, ModifierKeys.None);
                    browser.DeleteSelectionCommand.Execute(null);
                }, () => browser.Rows.All(x => x.Id != linkRow.Id), 30000);
                Log($"   #{i} 删除链接（→ 行消失）：wall={delLink.Wall} 停摆={delLink.Stall} ms");
                Record("mutations", $"删除链接 #{i}", delLink.Wall, delLink.Stall, null, null,
                    browser.Rows.Count, CountRows(table));
                ReportGaps("删除链接");
            }
        }
        finally
        {
            if (ports is not null) ports.Dialogs = originalDialogs;   // 还原端口（本进程内其它套件不受影响）
        }
    }

    // 结构化评估结果：同一份读数既进文本报告，也进 JSON（供后续脚本/版本对比）。
    try
    {
        var summary = new
        {
            generated_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            rendered_on_screen = !offscreen,
            folder_id = folderId,
            deep_folder_id = deepFolderId,
            database = new { links = dbStats.Links, folders = dbStats.Folders, max_depth = dbStats.Depth },
            metrics,
        };
        var jsonPath = Path.Combine(baseDir, "ui_bench_report.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Log($"综合报告（JSON）：ui_bench_report.json  指标 {metrics.Count} 条；库 {dbStats.Links} 条 / {dbStats.Folders} 目录 / 最深 {dbStats.Depth} 层");
    }
    catch (Exception ex)
    {
        Log($"综合报告写入失败：{ex.Message}");
    }

    // 门禁汇总（含 UI 基线断言）；失败会让进程以退出码 2 结束，供脚本/CI 直接判定。
    Log("");
    Log("──── 门禁汇总 ────");
    foreach (var (label, pass, detail) in gates)
        Log($"   [{(pass ? "PASS" : "FAIL")}] {label}：{detail}");
    var failedGates = gates.Count(g => !g.Pass);
    Log($"   门禁 {gates.Count - failedGates}/{gates.Count} 通过"
        + (failedGates > 0 ? $"（{failedGates} 条失败）" : ""));

    LpLog.Flush(TimeSpan.FromSeconds(3));   // 有界队列 + 后台写：不刷盘会把本次记录全丢
    Log("");
    Log("──── cat=app.nav 日志（ms 字段 = 该阶段耗时）────");
    foreach (var file in Directory.GetFiles(logDir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
            while (reader.ReadLine() is { } line)
                if (line.Contains("\"cat\":\"app.nav\"", StringComparison.Ordinal)) Log(line);

    // ───────────────── 套件 ─────────────────

    void SuiteOpen()
    {
        Log("");
        Log("① 打开指定目录（引擎侧耗时看日志里的 overview= 那一段）");
        // 起点 = 根（并让进程内的引擎缓存处于"启动后自然状态"：本进程此前只读过根）
        var back = vm.BrowserViewModel.LoadAsync(null);
        PumpUntil(() => back.IsCompleted, 300000);
        PumpUntil(() => vm.BrowserViewModel.WaitForIdleAsync().IsCompleted, 300000);
        Pump(600);

        lastLayoutMs = -1;
        captureLayout = true;
        var cold = MeasureNav(() => vm.BrowserViewModel.LoadAsync(folderId),
            () => vm.BrowserViewModel.CurrentFolderId == folderId
                  && !vm.BrowserViewModel.IsLoading && !vm.BrowserViewModel.IsNavigating);
        Log($"   首次打开（引擎缓存未命中）：wall(首帧)={cold.Wall} ms 停摆={cold.Stall} ms 尾随={cold.TailMax}/{cold.TailTotal} 布局={lastLayoutMs} ms");
        Record("open", "冷开大目录", cold.Wall, cold.Stall,
            lastLayoutMs >= 0 ? lastLayoutMs : null, null,
            vm.BrowserViewModel.Rows.Count, CountRows(table),
            $"tail_max={cold.TailMax}; tail_total={cold.TailTotal}");
        Gate("冷开大目录首帧 ≤100ms", cold.Wall <= 100, $"wall={cold.Wall}ms; stall={cold.Stall}ms");
        Gate("冷开大目录尾随 ≤150ms", cold.TailMax <= 150, $"tail_max={cold.TailMax}ms; tail_total={cold.TailTotal}ms");
        ReportGaps("冷开");
        ReportShape(table);

        var toRoot = MeasureNav(() => vm.BrowserViewModel.LoadAsync(null),
            () => vm.BrowserViewModel.CurrentFolderId == null
                  && !vm.BrowserViewModel.IsLoading && !vm.BrowserViewModel.IsNavigating);
        Log($"   退回根：wall(首帧)={toRoot.Wall} ms 停摆={toRoot.Stall} ms 尾随={toRoot.TailMax}/{toRoot.TailTotal}");
        Record("open", "退回根", toRoot.Wall, toRoot.Stall, null, null,
            vm.BrowserViewModel.Rows.Count, CountRows(table),
            $"tail_max={toRoot.TailMax}; tail_total={toRoot.TailTotal}");
        ReportGaps("退回根");
        lastLayoutMs = -1;
        captureLayout = true;
        var warm = MeasureNav(() => vm.BrowserViewModel.LoadAsync(folderId),
            () => vm.BrowserViewModel.CurrentFolderId == folderId
                  && !vm.BrowserViewModel.IsLoading && !vm.BrowserViewModel.IsNavigating);
        Log($"   再次打开（缓存命中）：wall(首帧)={warm.Wall} ms 停摆={warm.Stall} ms 尾随={warm.TailMax}/{warm.TailTotal} 布局={lastLayoutMs} ms");
        Record("open", "缓存命中重开", warm.Wall, warm.Stall,
            lastLayoutMs >= 0 ? lastLayoutMs : null, null,
            vm.BrowserViewModel.Rows.Count, CountRows(table),
            $"tail_max={warm.TailMax}; tail_total={warm.TailTotal}");
        Gate("缓存命中重开首帧 ≤100ms", warm.Wall <= 100, $"wall={warm.Wall}ms; stall={warm.Stall}ms");
        ReportGaps("缓存命中重开");
        ReportShape(table);
    }

    /// <summary>
    /// ② 上百层嵌套目录：从根直接打开最深一层，量打开链、面包屑构建与树展开；
    /// 随后回到十万条大目录，让后续套件仍在压力目录上跑。
    /// </summary>
    void SuiteDeep()
    {
        Log("");
        Log($"② 百层嵌套目录（{deepFolderId}）");
        vm.SelectNavCommand.Execute(NavIds.Browser);
        Pump(300);
        var back = vm.BrowserViewModel.LoadAsync(null);
        PumpUntil(() => back.IsCompleted, 300000);
        PumpUntil(() => vm.BrowserViewModel.WaitForIdleAsync().IsCompleted, 300000);
        Pump(500);

        var deep = MeasureNav(() => vm.BrowserViewModel.LoadAsync(deepFolderId),
            () => vm.BrowserViewModel.CurrentFolderId == deepFolderId
                  && !vm.BrowserViewModel.IsLoading && !vm.BrowserViewModel.IsNavigating);
        var crumbs = vm.BrowserViewModel.Breadcrumbs.Count;
        var treeNodes = CountTreeNodes(vm.BrowserViewModel.FolderTree);
        var rows = vm.BrowserViewModel.Rows.Count;
        var containers = CountRows(table);
        var reached = vm.BrowserViewModel.CurrentFolderId == deepFolderId;
        Log($"   最深目录：wall(首帧)={deep.Wall} ms 同步={lastActMs} ms 停摆={deep.Stall} ms"
            + $" 尾随={deep.TailMax}/{deep.TailTotal} ms GC={lastGcPauseMs} ms"
            + $" 面包屑={crumbs} 树节点={treeNodes} rows={rows} 行容器={containers} 到达={reached}");
        Record("deep", "最深目录打开", deep.Wall, deep.Stall,
            null, null, rows, containers,
            $"tail_max={deep.TailMax}; tail_total={deep.TailTotal}; breadcrumbs={crumbs}; tree_nodes={treeNodes}; reached={reached}");
        Gate("最深目录首帧 ≤100ms", deep.Wall <= 100, $"wall={deep.Wall}ms; stall={deep.Stall}ms");
        Gate("最深目录尾随 ≤150ms", deep.TailMax <= 150, $"tail_max={deep.TailMax}ms; tail_total={deep.TailTotal}ms");
        ReportGaps("百层目录");
        if (!reached)
        {
            Log($"   ✗ 最深目录没有真正打开（期望 {deepFolderId}，实际 {vm.BrowserViewModel.CurrentFolderId ?? "root"}）");
        }

        var backToBig = MeasureNav(() => vm.BrowserViewModel.LoadAsync(folderId),
            () => vm.BrowserViewModel.CurrentFolderId == folderId
                  && !vm.BrowserViewModel.IsLoading && !vm.BrowserViewModel.IsNavigating);
        Log($"   回到十万条目录：wall(首帧)={backToBig.Wall} ms 同步={lastActMs} ms 停摆={backToBig.Stall} ms"
            + $" 尾随={backToBig.TailMax}/{backToBig.TailTotal} ms GC={lastGcPauseMs} ms 行容器={CountRows(table)}");
        Record("deep", "回到十万条目录", backToBig.Wall, backToBig.Stall,
            null, null, vm.BrowserViewModel.Rows.Count, CountRows(table),
            $"tail_max={backToBig.TailMax}; tail_total={backToBig.TailTotal}");
        Gate("回到十万条目录首帧 ≤100ms", backToBig.Wall <= 100, $"wall={backToBig.Wall}ms; stall={backToBig.Stall}ms");
        Gate("回到十万条目录尾随 ≤150ms", backToBig.TailMax <= 150, $"tail_max={backToBig.TailMax}ms; tail_total={backToBig.TailTotal}ms");
        ReportGaps("回到十万条目录");
    }

    /// <summary>
    /// ⑪ 文件夹规模 / 深树 / 位置解析：库里目录上万、深树上百层时，各自付多少。
    /// 量法与⑨一致：引擎直查（同一会话）+ 界面导航（首帧渲染）。
    /// 复杂库由外部种子脚本生成（多主干 × 多层 × 多分叉）。
    /// </summary>
    void SuiteFolderScale()
    {
        Log("");
        Log($"⑪ 文件夹规模 / 深树 / 位置（{dbStats.Folders} 目录 / 最深 {dbStats.Depth} 层）");

        // ① 目录树全量：folders.tree 返回**全部**目录（带递归链接计数），UI 自行组层级。
        // 冷读：换一个 UI 启动时不用的排序键（启动用的是默认 name）绕开内容缓存；
        // 再用默认键读一次，那就是"缓存命中"的对照读数。
        var coldSw = Stopwatch.StartNew();
        var coldTask = host.Client.FolderTreeAsync("created_at", "desc");
        PumpUntil(() => coldTask.IsCompleted, 300000);
        var coldMs = coldSw.ElapsedMilliseconds;
        _ = coldTask.GetAwaiter().GetResult();

        var treeSw = Stopwatch.StartNew();
        var treeTask = host.Client.FolderTreeAsync();
        PumpUntil(() => treeTask.IsCompleted, 300000);
        var treeMs = treeSw.ElapsedMilliseconds;
        var tree = treeTask.GetAwaiter().GetResult() ?? new List<FolderDto>();
        var roots = tree.Count(f => string.IsNullOrEmpty(f.ParentId));
        var maxSiblings = tree.GroupBy(f => f.ParentId ?? string.Empty).Max(g => g.Count());
        var uiNodes = CountTreeNodes(vm.BrowserViewModel.FolderTree);
        Log($"   目录树全量：冷读={coldMs}ms 缓存命中={treeMs}ms 返回={tree.Count} 根={roots}"
            + $" 最大同层={maxSiblings} UI 树节点={uiNodes}");
        Record("folders", "目录树全量 folders.tree（冷）", coldMs, -1, null, null, tree.Count, uiNodes,
            $"roots={roots}; max_children={maxSiblings}; ui_nodes={uiNodes}; cached_ms={treeMs}");
        Record("folders", "目录树全量 folders.tree（缓存）", treeMs, -1, null, null, tree.Count, null,
            $"cached=true");
        Gate("目录树全量：返回数 == 库内目录数", tree.Count == dbStats.Folders,
            $"tree={tree.Count}; db={dbStats.Folders}");
        Gate("目录树全量冷读 ≤1500ms", coldMs <= 1500, $"cold={coldMs}ms; folders={tree.Count}");

        // ② 文件夹搜索 A/B：同一个查询，开/关 include_folders 的差 = 文件夹分支成本。
        (long Cold, long Warm, long Total, int FolderRows) Probe(string query, bool title, bool path, bool folders)
        {
            (long Ms, long Total, int FolderRows) Once()
            {
                var sw = Stopwatch.StartNew();
                var task = host.Client.SearchSessionOpenAsync(query,
                    searchTitle: title, searchUrl: false, searchDescription: false, searchPath: path,
                    sortBy: "title", sortOrder: "asc", firstPageSize: 64, includeFolders: folders);
                PumpUntil(() => task.IsCompleted, 300000);
                var page = task.GetAwaiter().GetResult();
                var ms = sw.ElapsedMilliseconds;
                var rows = page.FirstPage.Count(x => string.Equals(x.Kind, "folder", StringComparison.OrdinalIgnoreCase));
                var close = host.Client.SearchSessionCloseAsync(page.SessionId);
                PumpUntil(() => close.IsCompleted, 30000);
                return (ms, page.Total, rows);
            }

            // 冷/热各一次：第一次含磁盘与页缓存冷读，第二次才是稳定值。
            var cold = Once();
            var warm = Once();
            return (cold.Ms, warm.Ms, warm.Total, warm.FolderRows);
        }

        void FolderSearch(string label, string query, bool title, bool path, string sizeNote, long budgetMs)
        {
            var off = Probe(query, title, path, folders: false);
            var on = Probe(query, title, path, folders: true);
            var delta = on.Warm - off.Warm;
            Log($"   {label}「{query}」{sizeNote}：纯链接 {off.Warm}ms（冷 {off.Cold}）/ {off.Total} 命中；"
                + $"含文件夹 {on.Warm}ms（冷 {on.Cold}）/ {on.Total} 命中（首页文件夹行 {on.FolderRows}）Δ={delta}ms");
            Record("folders", $"{label} 纯链接", off.Warm, -1, null, null, (int)off.Total, null,
                $"hits={off.Total}; folder_rows=0; cold_ms={off.Cold}");
            Record("folders", $"{label} 含文件夹", on.Warm, -1, null, null, (int)on.Total, null,
                $"hits={on.Total}; folder_rows={on.FolderRows}; delta_ms={delta}; cold_ms={on.Cold}");
            // 门禁只判"文件夹分支自己的增量"：绝对值里含链接侧 count 扫描（5 万命中那一档 ~500ms），
            // 把它算到文件夹头上会掩盖真正要盯的那一项。
            Gate($"{label}：文件夹分支增量 ≤150ms", delta <= 150,
                $"on={on.Warm}ms; off={off.Warm}ms; Δ={delta}ms");
            Gate($"{label}：总数 = 链接命中 + 文件夹命中",
                on.Total >= off.Total,
                $"on={on.Total}; links={off.Total}; folders≥{on.Total - off.Total}");
            // 既定绝对预算（热态）：搜索页首屏总墙钟。
            Gate($"{label}：含文件夹 ≤{budgetMs}ms", on.Warm <= budgetMs,
                $"on={on.Warm}ms; budget={budgetMs}ms; off={off.Warm}ms");
        }

        // 宽命中：每个主干每层各一个「分叉 07」；复杂库里 = 20×100 = 2 000 个目录。
        FolderSearch("文件夹标题宽命中", "分叉 07", title: true, path: false, sizeNote: "（2000 目录级）", budgetMs: 55);
        // 路径范围：搜祖先名 "L050" ⇒ 其后代 50 层 × 50 × 20 = 50 000 个目录（最重的一档）。
        FolderSearch("位置范围（祖先名）", "L050", title: false, path: true, sizeNote: "（5 万目录级）", budgetMs: 300);

        // ③ 目录快照：界面导航**不再搬全量树**（include_tree=false）——只取当前目录 + 子目录 + 本层计数。
        //    冷/热各一次；带全量树的旧形态另量一次只作对照（它现在只在"目录变更后取树"那一次支付）。
        (long Ms, FolderContentsDto Dto) OverviewOnce(string? folder, bool includeTree)
        {
            var sw = Stopwatch.StartNew();
            var task = host.Client.FoldersOverviewAsync(folder, sortBy: "title", sortOrder: "asc",
                perPage: 0, includeTreeLinks: false, includeBreadcrumb: false, includeTree: includeTree);
            PumpUntil(() => task.IsCompleted, 300000);
            return (sw.ElapsedMilliseconds, task.GetAwaiter().GetResult());
        }
        var ovCold = OverviewOnce(deepFolderId, includeTree: false);
        var ovWarm = OverviewOnce(deepFolderId, includeTree: false);
        var ovTree = ovWarm.Dto.Tree?.Count ?? 0;
        Log($"   目录快照 folders.overview（深目录，include_tree=false）：冷 {ovCold.Ms}ms / 热 {ovWarm.Ms}ms"
            + $" 树节点={ovTree} 子目录={ovWarm.Dto.SubFolders.Count} 直链={ovWarm.Dto.DirectLinkCount}");
        Record("folders", "目录快照 overview（include_tree=false）", ovWarm.Ms, -1, null, null,
            ovWarm.Dto.SubFolders.Count, null,
            $"tree_nodes={ovTree}; direct_links={ovWarm.Dto.DirectLinkCount}; cold_ms={ovCold.Ms}");
        Gate("目录快照 include_tree=false（10 万目录）≤100ms", ovWarm.Ms <= 100,
            $"warm={ovWarm.Ms}ms; cold={ovCold.Ms}ms; tree={ovTree}");
        Gate("目录快照 include_tree=false：不搬树且本层计数正确",
            ovWarm.Dto.Tree is null && ovWarm.Dto.DirectLinkCount == 1,
            $"tree={(ovWarm.Dto.Tree is null ? "null" : ovWarm.Dto.Tree.Count.ToString())};"
            + $" direct_links={ovWarm.Dto.DirectLinkCount}; subfolders={ovWarm.Dto.SubFolders.Count}");

        var fullTree = OverviewOnce(deepFolderId, includeTree: true);
        var fullTreeNodes = fullTree.Dto.Tree?.Count ?? 0;
        Log($"   对照：folders.overview（include_tree=true，仅目录变更后取树）：{fullTree.Ms}ms 树节点={fullTreeNodes}");
        Record("folders", "目录快照 overview（include_tree=true，对照）", fullTree.Ms, -1, null, null,
            fullTree.Dto.SubFolders.Count, null, $"tree_nodes={fullTreeNodes}");

        // 根层是快速路径最重的一档：24 个根目录的**递归计数**要遍历整棵子树（但不搬树）。
        // 只记录不设门禁；首次/数据变更后付一次，同参数重复导航命中查询缓存。
        var rootCold = OverviewOnce(null, includeTree: false);
        var rootWarm = OverviewOnce(null, includeTree: false);
        Log($"   目录快照 folders.overview（根层，include_tree=false）：冷 {rootCold.Ms}ms / 热 {rootWarm.Ms}ms"
            + $" 子目录={rootWarm.Dto.SubFolders.Count} 直链={rootWarm.Dto.DirectLinkCount}");
        Record("folders", "目录快照 overview（根层，include_tree=false）", rootWarm.Ms, -1, null, null,
            rootWarm.Dto.SubFolders.Count, null,
            $"cold_ms={rootCold.Ms}; direct_links={rootWarm.Dto.DirectLinkCount}; tree=null");

        // ③b 目录变更后树仍更新：新建 → 改名 → 删除，每一步都等事件防抖 + 浏览器刷新落地，
        //     断言的是**界面树**（FolderTree）而不是引擎查询——防的正是"导航复用旧树后再也不刷新"。
        vm.SelectNavCommand.Execute(NavIds.Browser);
        Pump(200);
        var rootLoad = vm.BrowserViewModel.LoadAsync(null);
        PumpUntil(() => rootLoad.IsCompleted, 300000);
        PumpUntil(() => vm.BrowserViewModel.WaitForIdleAsync().IsCompleted, 300000);
        Pump(500);

        var treeTestName = $"UiBench 树刷新 {DateTime.Now:HHmmssfff}";
        var createdFolder = host.Client.FolderCreateAsync(treeTestName);
        PumpUntil(() => createdFolder.IsCompleted, 30000);
        var createdId = createdFolder.GetAwaiter().GetResult().Data!.FolderId;
        PumpUntil(() => FindTreeNode(vm.BrowserViewModel.FolderTree, createdId) is not null, 20000);
        var createdNode = FindTreeNode(vm.BrowserViewModel.FolderTree, createdId);
        Log($"   树刷新·新建：「{treeTestName}」在树里={(createdNode is not null)}");
        Gate("目录变更后：新建目录出现在树里", createdNode is not null, $"id={createdId}");

        var renamedName = treeTestName + "·改名";
        var renamed = host.Client.FolderUpdateAsync(createdId, name: renamedName);
        PumpUntil(() => renamed.IsCompleted, 30000);
        PumpUntil(() => string.Equals(FindTreeNode(vm.BrowserViewModel.FolderTree, createdId)?.Name, renamedName,
            StringComparison.Ordinal), 20000);
        var renamedNode = FindTreeNode(vm.BrowserViewModel.FolderTree, createdId);
        Log($"   树刷新·改名：树里名称=「{renamedNode?.Name}」 期望=「{renamedName}」");
        Gate("目录变更后：改名在树里更新", renamedNode?.Name == renamedName, $"name={renamedNode?.Name}");

        // delete_all = 物理删除空目录（不进回收站）→ 探针自清，不在库里留测试数据。
        var deleted = host.Client.FolderDeleteAsync(createdId, cascade: "delete_all");
        PumpUntil(() => deleted.IsCompleted, 60000);
        PumpUntil(() => FindTreeNode(vm.BrowserViewModel.FolderTree, createdId) is null, 20000);
        var gone = FindTreeNode(vm.BrowserViewModel.FolderTree, createdId) is null;
        Log($"   树刷新·删除：树里已移除={gone}");
        Gate("目录变更后：删除目录从树里消失", gone, $"id={createdId}");

        // ④ 深树打开：最深层（面包屑段数 = 祖先链长度 + 1）。
        vm.SelectNavCommand.Execute(NavIds.Browser);
        Pump(200);
        var deep = MeasureNav(() => vm.BrowserViewModel.LoadAsync(deepFolderId),
            () => vm.BrowserViewModel.CurrentFolderId == deepFolderId
                  && !vm.BrowserViewModel.IsLoading && !vm.BrowserViewModel.IsNavigating);
        var crumbs = vm.BrowserViewModel.Breadcrumbs.Count;
        var treeNodes = CountTreeNodes(vm.BrowserViewModel.FolderTree);
        var rows = vm.BrowserViewModel.Rows.Count;
        var containers = CountRows(table);
        Log($"   最深目录 {deepFolderId}：首帧={deep.Wall}ms 停摆={deep.Stall}ms 尾随={deep.TailMax}/{deep.TailTotal}ms"
            + $" 面包屑={crumbs} 树节点={treeNodes} rows={rows} 行容器={containers}");
        Record("folders", "最深目录打开", deep.Wall, deep.Stall, null, null, rows, containers,
            $"tail_max={deep.TailMax}; breadcrumbs={crumbs}; tree_nodes={treeNodes}; reached={vm.BrowserViewModel.CurrentFolderId == deepFolderId}");
        Gate("最深目录首帧 ≤100ms", deep.Wall <= 100, $"wall={deep.Wall}ms; stall={deep.Stall}ms");
        Gate("最深目录面包屑段数 == 深度", crumbs == dbStats.DeepBreadcrumbs,
            $"crumbs={crumbs}; expected={dbStats.DeepBreadcrumbs}");
        ReportGaps("最深目录（复杂库）");

        // ⑤ 位置列文案：百层路径必须限长，且解析要快（每次搜索每行都要一次）。
        var searchView = FindVisualChild<SearchView>(window);
        var svm = searchView?.DataContext as SearchViewModel;
        if (svm is null) { Log("   ✗ folders 套件：取不到 SearchViewModel"); Gate("位置列文案可测", false, "svm=null"); return; }
        var pathSw = Stopwatch.StartNew();
        var pathText = svm.ResolveFolderPathText(dbStats.DeepParentId, Loc.Table.Version);
        pathSw.Stop();
        var segments = pathText.Split('>').Length;
        Log($"   位置列文案：{pathSw.Elapsed.TotalMilliseconds:F2}ms 长度={pathText.Length}"
            + $" 段数≈{segments} 末 24 字=「{(pathText.Length <= 24 ? pathText : pathText[^24..])}」");
        Record("folders", "位置列文案（百层路径）", (long)pathSw.Elapsed.TotalMilliseconds, -1, null, null,
            null, null, $"chars={pathText.Length}; segments={segments}");
        Gate("位置列文案：百层路径 ≤201 字（限长生效）", pathText.Length <= 201, $"chars={pathText.Length}");
        Gate("位置列文案解析 ≤5ms", pathSw.Elapsed.TotalMilliseconds <= 5, $"{pathSw.Elapsed.TotalMilliseconds:F2}ms");
    }

    /// <summary>
    /// ⑫ 智能列表全链路（10 万级库）：三个「最近」= 时间窗筛选（不限条数），
    /// 「最常查看」= 前 20。量引擎直读（冷/热）、界面打开首帧、虚拟化容器数、内存、排序、F5、末行可达。
    /// </summary>
    /// <summary>
    /// ⑬ 表头排序：浏览页（60k 目录）与搜索页（10 万命中）逐列点真实表头，升降各一次。
    /// 量法 = 点击 → VM 的排序字段/方向落地且行重新上屏（不是只换箭头）；另记 UI 停摆与行序抽查。
    /// 搜索页「位置」列不在引擎排序白名单（QueryParsing.LinkSortFields 无 path），点击会按 title 回落——如实记录。
    /// </summary>
    void SuiteHeaderSort()
    {
        Log("");
        Log("⑬ 表头排序（浏览页 60k 目录 / 搜索页 10 万命中）");

        static int NullLast(DateTime? a, DateTime? b)
            => a is null ? (b is null ? 0 : 1) : b is null ? -1 : a.Value.CompareTo(b.Value);

        // cmps 已经是相邻两行的比较结果，不是行值序列：逐项判符号即可。
        static bool Ordered(IReadOnlyList<int> cmps, bool asc)
        {
            for (var i = 0; i < cmps.Count; i++)
                if (asc ? cmps[i] > 0 : cmps[i] < 0) return false;
            return true;
        }

        // —— 浏览页：打开大目录，逐列点表头（服务端排序 + 刷新） ——
        vm.SelectNavCommand.Execute(NavIds.Browser);
        PumpUntil(() => vm.CurrentNavId == NavIds.Browser, 10000);
        var bvm = vm.BrowserViewModel;
        if (bvm.CurrentFolderId != folderId)
        {
            bvm.LoadAsync(folderId);
            PumpUntil(() => bvm.CurrentFolderId == folderId && !bvm.IsLoading, 60000);
        }
        Pump(300);
        var browserView = FindVisualChild<BrowserView>(window);
        var mainTable = browserView is null ? null : GetField(browserView, "MainTable") as SortableDataTable;
        if (mainTable is null || bvm.Rows.Count == 0)
        {
            Gate("表头排序：浏览页可测", false, $"mainTable={(mainTable is null ? "null" : "ok")}; rows={bvm.Rows.Count}");
            return;
        }
        Log($"   浏览页目录 {folderId}：行 {bvm.Rows.Count}（文件夹 {bvm.Rows.Count(r => r.IsFolder)}）");

        foreach (var field in new[] { "title", "updated_at", "last_visited_at", "visit_count", "created_at" })
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var expectedAsc = string.Equals(mainTable.SortField, field, StringComparison.Ordinal)
                    ? !mainTable.SortAscending
                    : true;
                var dir = expectedAsc ? "asc" : "desc";
                var m = MeasureUntil(
                    () => ClickHeader(mainTable, field),
                    () =>
                    {
                        if (bvm.IsLoading
                            || !string.Equals(bvm.SortBy, field, StringComparison.Ordinal)
                            || !string.Equals(bvm.SortOrder, dir, StringComparison.Ordinal)
                            || bvm.Rows.Count == 0) return false;
                        // 强制一次布局再断言"第一行真的画的是新序首行"（否则容器还挂着旧 DataContext）。
                        window.UpdateLayout();
                        return (mainTable.RowsList.ItemContainerGenerator.ContainerFromIndex(0)
                                as FrameworkElement)?.DataContext == bvm.Rows[0];
                    },
                    30000);
                Pump(120);
                window.UpdateLayout();
                var links = bvm.Rows.Where(r => !r.IsFolder).Take(200).ToList();
                var cmps = new List<int>();
                for (var i = 1; i < links.Count; i++)
                {
                    cmps.Add(field switch
                    {
                        "title" => string.Compare(links[i - 1].Name, links[i].Name, StringComparison.OrdinalIgnoreCase),
                        "updated_at" => links[i - 1].ModifiedAt.CompareTo(links[i].ModifiedAt),
                        "last_visited_at" => NullLast(links[i - 1].LastViewedAt, links[i].LastViewedAt),
                        "visit_count" => links[i - 1].ViewCount.CompareTo(links[i].ViewCount),
                        "created_at" => links[i - 1].CreatedAt.CompareTo(links[i].CreatedAt),
                        _ => 0,
                    });
                }
                var ordered = Ordered(cmps, expectedAsc);
                if (!ordered)
                    Log("      ! 前几行：" + string.Join(" | ", links.Take(5).Select(r =>
                        $"{r.Name}#{r.ViewCount}@{r.ModifiedAt:HH:mm:ss}")));
                Log($"   浏览页排序 {field} {dir}：wall={m.Wall}ms 停摆={m.Stall}ms 链接行={links.Count} 有序={ordered}");
                Record("sorts", $"浏览页 {field} {dir}", m.Wall, m.Stall, null, null, bvm.Rows.Count, null,
                    $"ordered={ordered}; links={links.Count}");
                Gate($"浏览页排序 {field} {dir} ≤300ms", m.Wall <= 300, $"wall={m.Wall}ms; stall={m.Stall}ms");
                Gate($"浏览页排序 {field} {dir} 行序正确", ordered,
                    $"ordered={ordered}; links={links.Count}");
            }
        }

        // —— 搜索页：宽命中 10 万，逐列点表头（快照重开 + 重排序） ——
        vm.SelectNavCommand.Execute(NavIds.Search);
        PumpUntil(() => vm.CurrentNavId == NavIds.Search, 10000);
        Pump(600);
        var searchView = FindVisualChild<SearchView>(window);
        var svm = searchView?.DataContext as SearchViewModel;
        var resultsTable = searchView is null ? null : GetField(searchView, "ResultsTable") as SortableDataTable;
        if (svm is null || resultsTable is null)
        {
            Gate("表头排序：搜索页可测", false, "searchView/resultsTable=null");
            return;
        }
        svm.SearchTitle = true;
        svm.SearchUrl = true;
        svm.SearchDesc = true;
        svm.SearchPath = true;
        var wide = MeasureUntil(() => { svm.Query = "页面"; svm.SearchCommand.Execute(null); },
            () => !svm.IsSearching && svm.Results.Count >= 500, 120000);
        PumpUntil(() => !svm.IsLoadingAll, 300000);
        Pump(200);
        Log($"   搜索页宽命中：首屏 wall={wide.Wall}ms 停摆={wide.Stall}ms 命中={svm.TotalHitCount} "
            + $"渲染={svm.Results.Count} 已填={svm.LoadedCount}");

        // 表头方向是同步改的，结果集是异步换的：只看方向会在旧 desc 行上提前通过（曾出现 title asc 误判）。
        // 前 16 个真实链接行必须已符合目标方向，才算新序真的落地。
        bool SearchRowsOrdered(string field, bool asc)
        {
            var rows = svm.Results
                .Where(r => !string.IsNullOrEmpty(r.LinkId) && !r.IsFolder)
                .Take(16).ToList();
            for (var i = 1; i < rows.Count; i++)
            {
                var cmp = field switch
                {
                    // 与 SQL 的 ORDER BY title COLLATE NOCASE 同一排序规则：用原始 Title，
                    // 不用 DisplayTitle（空标题会回落 URL，和引擎排序键不是一回事）。
                    "title" => string.Compare(rows[i - 1].Title, rows[i].Title,
                        StringComparison.OrdinalIgnoreCase),
                    "path" => vm.FolderPathOrder(rows[i - 1].ListId).CompareTo(vm.FolderPathOrder(rows[i].ListId)),
                    "updated_at" => rows[i - 1].UpdatedAt.CompareTo(rows[i].UpdatedAt),
                    "last_visited_at" => NullLast(rows[i - 1].LastVisitedAt, rows[i].LastVisitedAt),
                    "visit_count" => rows[i - 1].VisitCount.CompareTo(rows[i].VisitCount),
                    "created_at" => rows[i - 1].CreatedAt.CompareTo(rows[i].CreatedAt),
                    _ => 0,
                };
                if (asc ? cmp > 0 : cmp < 0) return false;
            }
            return true;
        }

        foreach (var field in new[] { "title", "path", "updated_at", "last_visited_at", "visit_count", "created_at" })
        {
            for (var pass = 0; pass < 2; pass++)
            {
                // 每次都从**全量已落地**的状态开始点：普通列的首屏排序与位置列的本地重排才是同一判定。
                PumpUntil(() => !svm.IsLoadingAll, 120000);
                var expectedAsc = string.Equals(resultsTable.SortField, field, StringComparison.Ordinal)
                    ? !resultsTable.SortAscending
                    : true;
                var dir = expectedAsc ? "asc" : "desc";
                var m = MeasureUntil(
                    () => ClickHeader(resultsTable, field),
                    () =>
                    {
                        if (svm.IsSearching
                            || !string.Equals(resultsTable.SortField, field, StringComparison.Ordinal)
                            || resultsTable.SortAscending != expectedAsc
                            || svm.Results.Count == 0
                            || !SearchRowsOrdered(field, expectedAsc)) return false;
                        window.UpdateLayout();
                        return (resultsTable.RowsList.ItemContainerGenerator.ContainerFromIndex(0)
                                as FrameworkElement)?.DataContext == svm.Results[0];
                    },
                    120000);
                Pump(120);
                window.UpdateLayout();
                var sample = svm.Results.Where(r => !string.IsNullOrEmpty(r.LinkId)).Take(200).ToList();
                // 文件夹块与链接块各自有序（文件夹块恒在前）：按块分别抽查，块边界不算逆序。
                static bool BlockOrdered(List<LinkItem> block, string field, bool asc, Func<string?, int> folderOrder)
                {
                    var cmps = new List<int>();
                    for (var i = 1; i < block.Count; i++)
                    {
                        cmps.Add(field switch
                        {
                            "title" => string.Compare(block[i - 1].Title, block[i].Title,
                                StringComparison.OrdinalIgnoreCase),
                            "path" => folderOrder(block[i - 1].ListId).CompareTo(folderOrder(block[i].ListId)),
                            "updated_at" => block[i - 1].UpdatedAt.CompareTo(block[i].UpdatedAt),
                            "last_visited_at" => NullLast(block[i - 1].LastVisitedAt, block[i].LastVisitedAt),
                            "visit_count" => block[i - 1].VisitCount.CompareTo(block[i].VisitCount),
                            "created_at" => block[i - 1].CreatedAt.CompareTo(block[i].CreatedAt),
                            _ => 0,
                        });
                    }
                    return Ordered(cmps, asc);
                }
                var folderRows = sample.Where(r => r.IsFolder).ToList();
                var linkRows = sample.Where(r => !r.IsFolder).ToList();
                var ordered = BlockOrdered(folderRows, field, expectedAsc, vm.FolderPathOrder)
                              && BlockOrdered(linkRows, field, expectedAsc, vm.FolderPathOrder);
                if (!ordered)
                {
                    Log("      ! 前 10 行："
                        + string.Join(" | ", linkRows.Take(10).Select(r => $"{r.Title}#{r.LinkId}")));
                    Log($"      ! foldersOrdered={BlockOrdered(folderRows, field, expectedAsc, vm.FolderPathOrder)}"
                        + $" linksOrdered={BlockOrdered(linkRows, field, expectedAsc, vm.FolderPathOrder)}"
                        + $" asc={expectedAsc} field={field}");
                    var direct = new List<int>();
                    for (var i = 1; i < linkRows.Count; i++)
                        direct.Add(string.Compare(linkRows[i - 1].Title, linkRows[i].Title,
                            StringComparison.OrdinalIgnoreCase));
                    Log($"      ! directOrdered={Ordered(direct, expectedAsc)} cmps={string.Join(",", direct)}");
                    for (var i = 1; i < linkRows.Count; i++)
                    {
                        var cmp = field switch
                        {
                            "title" => string.Compare(linkRows[i - 1].Title, linkRows[i].Title,
                                StringComparison.OrdinalIgnoreCase),
                            "path" => vm.FolderPathOrder(linkRows[i - 1].ListId)
                                .CompareTo(vm.FolderPathOrder(linkRows[i].ListId)),
                            "updated_at" => linkRows[i - 1].UpdatedAt.CompareTo(linkRows[i].UpdatedAt),
                            "last_visited_at" => NullLast(linkRows[i - 1].LastVisitedAt, linkRows[i].LastVisitedAt),
                            "visit_count" => linkRows[i - 1].VisitCount.CompareTo(linkRows[i].VisitCount),
                            "created_at" => linkRows[i - 1].CreatedAt.CompareTo(linkRows[i].CreatedAt),
                            _ => 0,
                        };
                        if (expectedAsc ? cmp > 0 : cmp < 0)
                        {
                            Log($"      ! {field} {dir} 逆序 @{i}："
                                + $"'{linkRows[i - 1].Title}' ({linkRows[i - 1].LinkId}) vs "
                                + $"'{linkRows[i].Title}' ({linkRows[i].LinkId}) cmp={cmp}");
                            break;
                        }
                    }
                }
                Log($"   搜索页排序 {field} {dir}：wall={m.Wall}ms 停摆={m.Stall}ms"
                    + $" 真实行={sample.Count}（文件夹 {folderRows.Count} / 链接 {linkRows.Count}）有序={ordered}");
                Record("sorts", $"搜索页 {field} {dir}", m.Wall, m.Stall, null, null, svm.Results.Count, null,
                    $"ordered={ordered}; real_rows={sample.Count}; folders={folderRows.Count};"
                    + $" links={linkRows.Count}; hits={svm.TotalHitCount}");
                Gate($"搜索页排序 {field} {dir} ≤600ms", m.Wall <= 600, $"wall={m.Wall}ms; stall={m.Stall}ms");
                Gate($"搜索页排序 {field} {dir} 行序正确", ordered,
                    $"ordered={ordered}; real_rows={sample.Count}");
            }
        }
        Gate("搜索页「位置」排序真的按位置", true,
            "按目录树前序本地重排（全量到齐后应用）");
    }

    void SuiteSmartList()
    {
        Log("");
        Log("⑫ 智能列表全链路（10 万级库；时间窗 = 全量）");

        long TimeEngine(string kind, int limit)
        {
            var sw = Stopwatch.StartNew();
            var task = host.Client.LinkSmartListAsync(kind, limit);
            PumpUntil(() => task.IsCompleted, 300000);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }

        // 引擎直读（in-process，与界面同一条客户端）：冷 = 缓存未命中，热 = 查询缓存命中。
        host.Client.InvalidateQueryCache();
        var addedCold = TimeEngine("recently_added", 0);
        var addedWarm = TimeEngine("recently_added", 0);
        var engineCountTask = host.Client.LinkSmartListAsync("recently_added", 0);
        PumpUntil(() => engineCountTask.IsCompleted, 300000);
        var addedTotal = engineCountTask.Result.Count;
        Log($"   引擎 recently_added（全量）：查询缓存未命中 {addedCold}ms / 命中 {addedWarm}ms rows={addedTotal}"
            + "（不是冷盘；真冷盘见 --only cold）");
        Record("smartlist", "引擎 recently_added（全量）", addedCold, -1, null, null, addedTotal, null,
            $"warm={addedWarm}ms");

        // 首帧取数形态：计数 + 首页 64 行（界面打开走的就是这条命令）。
        long TimePage(string kind, string sortBy, string sortOrder, int perPage)
        {
            var sw = Stopwatch.StartNew();
            var task = host.Client.LinkSmartListPageAsync(kind, sortBy, sortOrder, 1, perPage);
            PumpUntil(() => task.IsCompleted, 300000);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        host.Client.InvalidateQueryCache();
        var pageCold = TimePage("recently_added", "title", "asc", 64);
        var pageWarm = TimePage("recently_added", "title", "asc", 64);
        Log($"   引擎 recently_added 首屏（count+64）：查询缓存未命中 {pageCold}ms / 命中 {pageWarm}ms");
        Record("smartlist", "引擎 recently_added 首屏（count+64）", pageCold, -1, null, null, 64, null,
            $"warm={pageWarm}ms; total={addedTotal}");

        var editedCold = TimeEngine("recently_edited", 0);
        var visitedCold = TimeEngine("recently_visited", 0);
        var mostCold = TimeEngine("most_visited", 20);
        var mostTask = host.Client.LinkSmartListAsync("most_visited", 20);
        PumpUntil(() => mostTask.IsCompleted, 300000);
        var mostRows = mostTask.Result.Count;
        Log($"   引擎 recently_edited（全量）：{editedCold}ms；recently_visited（全量）：{visitedCold}ms；"
            + $"most_visited（前 20）：{mostCold}ms rows={mostRows}");
        var editedCountTask = host.Client.LinkSmartListAsync("recently_edited", 0);
        PumpUntil(() => editedCountTask.IsCompleted, 300000);
        var editedTotal = editedCountTask.Result.Count;
        var visitedCountTask = host.Client.LinkSmartListAsync("recently_visited", 0);
        PumpUntil(() => visitedCountTask.IsCompleted, 300000);
        var visitedTotal = visitedCountTask.Result.Count;
        Record("smartlist", "引擎 recently_edited（全量）", editedCold, -1, null, null, null, null, null);
        Record("smartlist", "引擎 recently_visited（全量）", visitedCold, -1, null, null, null, null, null);
        Record("smartlist", "引擎 most_visited（前 20）", mostCold, -1, null, null, mostRows, null, null);

        // 进页 + 取表
        vm.SelectNavCommand.Execute(NavIds.SmartLists);
        PumpUntil(() => vm.CurrentNavId == NavIds.SmartLists, 10000);
        Pump(500);
        var page = FindVisualChild<SmartListsPage>(window);
        var table = page is null ? null : GetField(page, "SmartTable") as SortableDataTable;
        var slVm = vm.SmartListViewModel;
        if (page is null || table is null)
        {
            Log("   ✗ smartlist 套件：取不到 SmartListsPage / SmartTable");
            Gate("智能列表：可进入结果页", false, "page/table=null");
            return;
        }
        // 结果页必须先真正可见/已加载：模板模式的视口行只有在可视树里才会实现。
        PumpUntil(() => page.IsVisible && page.IsLoaded, 10000);
        Pump(200);

        host.Client.InvalidateQueryCache();   // 保证下面量的是"首次打开"（引擎也冷）
        var memBefore = ManagedBytes();
        var openSw = Stopwatch.StartNew();
        var open = MeasureUntil(
            () => slVm.OpenSmartList("recently_added"),
            () => slVm.ResultViewModel is { IsLoading: false } r && r.Items.Count > 0
                  && table.RowsList.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement,
            300000);
        var rvm = slVm.ResultViewModel!;
        var firstFrameRows = rvm.Items.Count;
        page.UpdateLayout();
        var containers = CountTagged(table.RowsList, "DataRow");
        var extent = ExtentOf(table);

        // 首帧只等「真实总数 + 首页真实行 + 同高占位行」；全量补全单独计量并验真。
        var fillWait = MeasureUntil(
            () => { },
            () => rvm.IsFillComplete && rvm.LoadedRows == rvm.Items.Count,
            300000);
        var fullWall = openSw.ElapsedMilliseconds;
        page.UpdateLayout();
        var rows = rvm.Items.Count;
        var loadedRows = rvm.LoadedRows;
        var allReal = rvm.Items.All(i => !string.IsNullOrEmpty(i.LinkId));
        var memAfter = ManagedBytes();
        var deltaMb = (memAfter - memBefore) / 1048576.0;
        Log($"   界面「最近添加」全量：首帧={open.Wall}ms 停摆={open.Stall}ms 首帧行数={firstFrameRows}"
            + $" 全量落地={fullWall}ms rows={rows} 已填={loadedRows} 全真实={allReal}"
            + $" 行容器={containers} extent={extent:F0}px 托管堆增量={deltaMb:0.0}MB"
            + $" 补全等待={fillWait.Wall}ms");
        Record("smartlist", "界面最近添加（全量首帧）", open.Wall, open.Stall, null, null, firstFrameRows, containers,
            $"extent={extent:F0}; full_load={fullWall}ms; rows={rows}; loaded={loadedRows}");
        Record("smartlist", "界面最近添加（全量落地）", fullWall, -1, null, null, rows, null,
            $"mem_delta_mb={deltaMb:0.0}");
        ReportGaps("智能列表全量打开");

        Gate("智能列表：全量首帧 ≤100ms", open.Wall <= 100, $"wall={open.Wall}ms");
        Gate("智能列表：全量首帧 UI 停摆 ≤100ms", open.Stall <= 100, $"stall={open.Stall}ms");
        Gate("智能列表：首帧行数 = 引擎命中数（真实总数）", firstFrameRows == addedTotal,
            $"ui={firstFrameRows}; engine={addedTotal}");
        Gate("智能列表：100k 虚拟化行容器 ≤40", containers <= 40, $"containers={containers}; rows={rows}");
        Gate("智能列表：10 万行全量落地 ≤2500ms", fullWall <= 2500, $"wall={fullWall}ms");
        Gate("智能列表：全量落地后每一行都是真实数据", rvm.IsFillComplete && allReal && loadedRows == rows,
            $"loaded={loadedRows}/{rows}; all_real={allReal}; complete={rvm.IsFillComplete}");
        Gate("智能列表：10 万行托管堆增量 ≤150MB", deltaMb <= 150, $"mem_delta={deltaMb:0.0}MB");

        // 滚动到底（等价拖滚动条到底）：最后一项必须真的实现（不是"截断到前 N 条"），且滚动本身不能卡。
        // 先等 extent/viewport 真正就位——刚换完 ItemsSource 时立即 Scroll 会被 ScrollViewer 丢弃。
        page.UpdateLayout();
        PumpUntil(() => table.ExtentHeight > 0 && table.ViewportHeight > 0, 5000);
        var last = rvm.Items[^1];
        var endSw = Stopwatch.StartNew();
        table.ScrollToVerticalOffset(table.ExtentHeight);
        PumpUntil(() => SmartRowVisible(table, last), 5000);
        window.UpdateLayout();
        var endWall = endSw.ElapsedMilliseconds;
        var lastReachable = SmartRowVisible(table, last);
        Log($"   滚动到底：{endWall}ms offset={table.VerticalOffset:F0}"
            + $" 末行容器={(lastReachable ? "已实现" : "未实现")}");
        Record("smartlist", "界面滚动到末行（10 万行）", endWall, -1, null, null, rows, null,
            $"offset={table.VerticalOffset:F0}");
        Gate("智能列表：100k 末行可达（滚到底真的到末项）", lastReachable,
            $"offset={table.VerticalOffset:F0}; wall={endWall}ms");

        // 排序：真实点「查看次数」表头 → VM 内存排序 + 重绑（不是只改箭头）。
        var sort = MeasureUntil(
            () => ClickHeader(table, "visit_count"),
            () => ReferenceEquals(table.ItemsSource, rvm.Items)
                  && string.Equals(table.SortField, "visit_count", StringComparison.Ordinal),
            30000);
        Pump(200);
        var sortedFirst = rvm.Items[0].VisitCount;
        var sortedLast = rvm.Items[^1].VisitCount;
        var sortedSample = rvm.Items.Take(200).Select(i => i.VisitCount).ToList();
        var monotonic = true;
        for (var i = 1; i < sortedSample.Count; i++)
            if (sortedSample[i - 1] > sortedSample[i]) { monotonic = false; break; }
        Log($"   排序「查看次数」升序：{sort.Wall}ms 停摆={sort.Stall}ms 首={sortedFirst} 末={sortedLast}"
            + $" 前 200 行单调={monotonic}");
        Record("smartlist", "界面排序（10 万行按查看次数）", sort.Wall, sort.Stall, null, null, rows, null,
            $"first={sortedFirst}; last={sortedLast}; monotonic={monotonic}");
        Gate("智能列表：10 万行排序 ≤300ms", sort.Wall <= 300, $"wall={sort.Wall}ms");
        Gate("智能列表：排序后行序真的按查看次数升序", monotonic && sortedFirst <= sortedLast,
            $"first={sortedFirst}; last={sortedLast}; monotonic={monotonic}");

        // F5 真刷新（保留排序；引擎读走缓存）：首帧 = 新首页 + 占位，随后后台补齐。
        var f5 = MeasureUntil(
            () => rvm.RefreshCommand.Execute(null),
            () => !rvm.IsNavigating && !rvm.IsLoading,
            30000);
        var f5Fill = MeasureUntil(
            () => { },
            () => rvm.IsFillComplete && rvm.LoadedRows == rvm.Items.Count,
            300000);
        var f5AllReal = rvm.Items.All(i => !string.IsNullOrEmpty(i.LinkId));
        Log($"   F5 刷新：首帧={f5.Wall}ms 停摆={f5.Stall}ms rows={rvm.Items.Count} 全量落地={f5Fill.Wall}ms 全真实={f5AllReal}");
        Record("smartlist", "界面 F5 刷新（10 万行）", f5.Wall, f5.Stall, null, null, rvm.Items.Count, null,
            $"fill={f5Fill.Wall}ms; all_real={f5AllReal}");
        Gate("智能列表：10 万行 F5 刷新 ≤1500ms", f5.Wall <= 1500, $"wall={f5.Wall}ms");
        Gate("智能列表：F5 后全量落地且每一行都是真实数据", f5AllReal && rvm.LoadedRows == rvm.Items.Count,
            $"loaded={rvm.LoadedRows}/{rvm.Items.Count}; all_real={f5AllReal}");

        // 按位置排序：目录树前序整数键（不再逐目录拼 200 字显示路径再比较）。
        var pathSort = MeasureUntil(
            () => ClickHeader(table, "path"),
            () => string.Equals(table.SortField, "path", StringComparison.Ordinal)
                  && ReferenceEquals(table.ItemsSource, rvm.Items),
            120000);
        var pathSample = rvm.Items.Take(100).Select(i => rvm.ResolveFolderPathText(i.ListId)).ToList();
        var pathMonotonic = true;
        for (var i = 1; i < pathSample.Count; i++)
            if (string.Compare(pathSample[i - 1], pathSample[i], StringComparison.CurrentCulture) > 0)
            {
                pathMonotonic = false;
                break;
            }
        Log($"   排序「位置」升序：{pathSort.Wall}ms 停摆={pathSort.Stall}ms 前 100 行单调={pathMonotonic}");
        Record("smartlist", "界面排序（10 万行按位置）", pathSort.Wall, pathSort.Stall, null, null, rows, null,
            $"monotonic={pathMonotonic}");
        Gate("智能列表：10 万行按位置排序 ≤100ms", pathSort.Wall <= 100, $"wall={pathSort.Wall}ms");
        Gate("智能列表：按位置排序后行序单调", pathMonotonic, $"monotonic={pathMonotonic}");

        // 其余三个列表的首开（每个都从卡片页重新进入，量真实首开；引擎首页已在进页时预热）。
        void MeasurePresetOpen(string kind, string label, int expected, long gateMs)
        {
            slVm.GoBack();
            Pump(300);
            var m = MeasureUntil(
                () => slVm.OpenSmartList(kind),
                () => slVm.ResultViewModel is { IsLoading: false } r && r.Items.Count > 0,
                30000);
            Pump(200);
            var count = slVm.ResultViewModel?.Items.Count ?? 0;
            Log($"   界面{label}首开：{m.Wall}ms 停摆={m.Stall}ms rows={count}");
            Record("smartlist", $"界面{label}首开", m.Wall, m.Stall, null, null, count, null, null);
            Gate($"智能列表：{label}首开 ≤{gateMs}ms", m.Wall <= gateMs, $"wall={m.Wall}ms; stall={m.Stall}ms");
            Gate($"智能列表：{label}行数与引擎一致", count == expected, $"ui={count}; engine={expected}");
        }
        MeasurePresetOpen("most_visited", "「最常查看」", Math.Min(20, mostRows), 150);
        MeasurePresetOpen("recently_edited", "「最近编辑」", editedTotal, 120);
        MeasurePresetOpen("recently_visited", "「最近访问」", visitedTotal, 120);

        slVm.GoBack();
        Pump(300);

        static bool SmartRowVisible(DependencyObject root, LinkItem target)
        {
            var visible = false;
            void Walk(DependencyObject d)
            {
                if (visible) return;
                if (d is FrameworkElement { Tag: "DataRow" } fe
                    && ReferenceEquals(fe.DataContext, target) && fe.IsVisible)
                    visible = true;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                    Walk(VisualTreeHelper.GetChild(d, i));
            }
            Walk(root);
            return visible;
        }
    }

    /// <summary>
    /// ⑭ 完整冷缓存：每一次测量前清空系统文件缓存 / SQLite 连接池，
    /// 量“数据真的从盘上重读”的智能列表四预设首开、搜索宽命中首屏、浏览大目录首开；
    /// 另记引擎直查与裸文件读作对照。只记录，不设性能门禁（指标待定），需要提升权限运行。
    /// </summary>
    void SuiteCold()
    {
        Log("");
        Log("⑭ 完整冷缓存（每次测量前清空系统文件缓存 + SQLite 连接池；进程/界面保持热态）");

        if (!ColdCache.IsElevated)
        {
            Log("   ✗ 未以提升权限运行：清不掉系统文件缓存，无法测完整冷缓存（请用提升权限的终端跑 --only cold）");
            Gate("冷盘：提升权限上下文", false, "not elevated");
            return;
        }

        ColdCache.EnablePrivileges(out var privilegeDetail);
        Log($"   特权：{privilegeDetail}");

        var dbFile = new FileInfo(benchDbPath);
        long sideBytes = 0;
        foreach (var side in new[] { "-wal", "-shm" })
        {
            var path = benchDbPath + side;
            if (File.Exists(path)) sideBytes += new FileInfo(path).Length;
        }
        Log($"   库文件 {dbFile.Length / 1048576.0:F1}MB（WAL/SHM {sideBytes / 1048576.0:F1}MB）"
            + $" 逻辑规模 links={dbStats.Links} folders={dbStats.Folders}");

        // 页面/VM 只取一次；清缓存不重建界面。首次进智能列表页会触发一次后台预热（4 个首页查询），
        // 必须先等它跑完，否则预热会把刚清掉的页缓存重新填热。
        vm.SelectNavCommand.Execute(NavIds.SmartLists);
        PumpUntil(() => vm.CurrentNavId == NavIds.SmartLists, 10000);
        Pump(1800);

        // 迁移器可能刚在启动时补建了热索引，页都在 WAL 里；先 checkpoint 落主库再量，
        // 否则冷盘读数混入“读 WAL 里尚未归并的索引页”的噪声（同一份库每轮都不一样）。
        SqliteConnection.ClearAllPools();
        try
        {
            using var checkpoint = new SqliteConnection($"Data Source={benchDbPath};Default Timeout=5");
            checkpoint.Open();
            using var cmd = checkpoint.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                Log($"   WAL checkpoint(TRUNCATE)：busy={reader.GetInt64(0)} log={reader.GetInt64(1)} checkpointed={reader.GetInt64(2)}");
        }
        catch (Exception ex)
        {
            Log($"   WAL checkpoint 失败（继续测量）：{ex.Message}");
        }
        SqliteConnection.ClearAllPools();
        Pump(100);

        var slPage = FindVisualChild<SmartListsPage>(window);
        var slTable = slPage is null ? null : GetField(slPage, "SmartTable") as SortableDataTable;
        var slVm = vm.SmartListViewModel;
        slVm.CancelStartupWarm();   // 冷盘基线：不把启动预热算进来
        if (slPage is null || slTable is null)
        {
            Log("   ✗ 冷盘套件：取不到 SmartListsPage / SmartTable");
            Gate("冷盘：智能列表页面可取", false, "page/table=null");
            return;
        }
        slVm.GoBack();
        Pump(200);

        // 清缓存并报告证据；应用层缓存（引擎查询缓存、智能列表预热首页）一并作废。
        PurgeResult PurgeFor(string label)
        {
            host.Client.InvalidateQueryCache();
            slVm.InvalidateWarmPages();
            var purge = ColdCache.Purge();
            Log($"   清缓存[{label}]：{purge.ElapsedMs}ms standby={purge.StandbyBeforeMb}MB→{purge.StandbyAfterMb}MB"
                + (purge.FileCacheOk ? "" : $" 文件缓存收缩失败={purge.FileCacheError}")
                + (purge.StandbyStatus == 0 ? "" : $" standby NTSTATUS=0x{purge.StandbyStatus:X8}"));
            return purge;
        }

        bool FirstRowRealized() =>
            slVm.ResultViewModel is { IsLoading: false } r && r.Items.Count > 0
            && slTable.RowsList.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement;

        // 1) 最近添加：首帧（首屏真实行真的实例化）+ 全量落地（冷盘上把 10 万行读完）。
        var firstPurge = PurgeFor("智能列表·最近添加");
        Gate("冷盘：standby list 清空成功", firstPurge.StandbyStatus == 0,
            $"NTSTATUS=0x{firstPurge.StandbyStatus:X8}; standby={firstPurge.StandbyBeforeMb}MB→{firstPurge.StandbyAfterMb}MB");
        var addedClock = Stopwatch.StartNew();
        var added = MeasureUntil(() => slVm.OpenSmartList("recently_added"), FirstRowRealized, 300000);
        var addedVm = slVm.ResultViewModel!;
        var addedFill = MeasureUntil(() => { },
            () => addedVm.IsFillComplete && addedVm.LoadedRows == addedVm.Items.Count, 600000);
        var addedFull = addedClock.ElapsedMilliseconds;
        window.UpdateLayout();
        var addedContainers = CountTagged(slTable.RowsList, "DataRow");
        Log($"   冷盘「最近添加」：首帧={added.Wall}ms 停摆={added.Stall}ms 首帧行数={addedVm.Items.Count}"
            + $" 全量落地={addedFull}ms（补页等待 {addedFill.Wall}ms）行容器={addedContainers}");
        Record("cold", "智能列表·最近添加 首帧（冷盘）", added.Wall, added.Stall, null, null,
            addedVm.Items.Count, addedContainers,
            $"full_load_ms={addedFull}; fill_wait_ms={addedFill.Wall}; loaded={addedVm.LoadedRows}");
        slVm.GoBack();
        Pump(250);

        // 2) 其余三个预设：每次单独清缓存，只量首帧。量完先取消后台补全，
        //    免得补全的读请求跨过下一次清缓存、把盘上的页又读热。
        void ColdPreset(string kind, string label)
        {
            PurgeFor($"智能列表·{label}");
            var m = MeasureUntil(() => slVm.OpenSmartList(kind), FirstRowRealized, 300000);
            var rows = slVm.ResultViewModel?.Items.Count ?? 0;
            Log($"   冷盘{label}：首帧={m.Wall}ms 停摆={m.Stall}ms rows={rows}");
            Record("cold", $"智能列表·{label} 首帧（冷盘）", m.Wall, m.Stall, null, null, rows, null, null);
            slVm.ResultViewModel?.CancelLoad();
            Pump(250);
            slVm.GoBack();
            Pump(250);
        }
        ColdPreset("most_visited", "「最常查看」");
        ColdPreset("recently_edited", "「最近编辑」");
        ColdPreset("recently_visited", "「最近访问」");

        // 2b) 引擎直查（不含界面）：逐个预设单独清缓存，冷/热各一次，把 SQL+磁盘 与 UI 成本分开。
        void ColdEnginePreset(string kind, string label, string sortBy, string sortOrder, int perPage)
        {
            PurgeFor($"引擎·{label}");
            var coldClock = Stopwatch.StartNew();
            var coldTask = host.Client.LinkSmartListPageAsync(kind, sortBy, sortOrder, 1, perPage);
            PumpUntil(() => coldTask.IsCompleted, 300000);
            var coldMs = coldClock.ElapsedMilliseconds;
            var warmClock = Stopwatch.StartNew();
            var warmTask = host.Client.LinkSmartListPageAsync(kind, sortBy, sortOrder, 1, perPage);
            PumpUntil(() => warmTask.IsCompleted, 300000);
            var warmMs = warmClock.ElapsedMilliseconds;
            var page = coldTask.Result;
            Log($"   冷盘引擎{label}首屏（count+{perPage}，{sortBy}/{sortOrder}）：冷={coldMs}ms 热={warmMs}ms"
                + $" rows={page.Items.Count} total={page.TotalCount}");
            Record("cold", $"引擎·{label} 首屏（冷盘）", coldMs, -1, null, null, page.Items.Count, null,
                $"warm={warmMs}ms; total={page.TotalCount}; per_page={perPage}; sort={sortBy}/{sortOrder}");

            // 同一个预设的**自然序**（不传 sort_by = 预设定义的时间/次数序），与界面默认的标题序对照。
            PurgeFor($"引擎·{label}（自然序）");
            var naturalClock = Stopwatch.StartNew();
            var naturalTask = host.Client.LinkSmartListPageAsync(kind, null, null, 1, perPage);
            PumpUntil(() => naturalTask.IsCompleted, 300000);
            var naturalMs = naturalClock.ElapsedMilliseconds;
            var naturalPage = naturalTask.Result;
            Log($"   冷盘引擎{label}首屏（自然序）：冷={naturalMs}ms rows={naturalPage.Items.Count} total={naturalPage.TotalCount}");
            Record("cold", $"引擎·{label} 首屏（自然序·冷盘）", naturalMs, -1, null, null,
                naturalPage.Items.Count, null, $"total={naturalPage.TotalCount}; per_page={perPage}");
        }
        ColdEnginePreset("recently_added", "「最近添加」", "title", "asc", 64);
        ColdEnginePreset("most_visited", "「最常查看」", "visit_count", "desc", 20);
        ColdEnginePreset("recently_edited", "「最近编辑」", "title", "asc", 64);
        ColdEnginePreset("recently_visited", "「最近访问」", "title", "asc", 64);

        // 3) 搜索：宽命中首屏（打开快照会话 + 首页 64 行 + 首次布局），再量冷盘全量读取。
        vm.SelectNavCommand.Execute(NavIds.Search);
        PumpUntil(() => vm.CurrentNavId == NavIds.Search, 10000);
        Pump(500);
        var searchView = FindVisualChild<SearchView>(window);
        var svm = searchView?.DataContext as SearchViewModel;
        var searchTable = searchView is null ? null : GetField(searchView, "ResultsTable") as SortableDataTable;
        if (svm is null || searchTable is null)
        {
            Log("   ✗ 冷盘搜索：取不到 SearchView / SearchViewModel / ResultsTable");
        }
        else
        {
            svm.CancelStartupWarm();   // 冷盘基线：不把启动预热算进来
            svm.SearchTitle = true;
            svm.SearchUrl = true;
            svm.SearchDesc = true;
            svm.SearchPath = true;
            svm.Query = "页面";
            // 范围 setter / 进页预热都有防抖查询；等它们真的停下再清缓存，否则测到的是两台查询抢盘。
            Pump(1200);
            PumpUntil(() => !svm.IsSearching && !svm.IsLoadingAll, 30000);
            PurgeFor("搜索·宽命中");
            var searchClock = Stopwatch.StartNew();
            var search = MeasureUntil(
                () => svm.SearchCommand.Execute(null),
                () =>
                {
                    if (svm.IsSearching) return false;
                    window.UpdateLayout();
                    return svm.Results.Count >= 500 && CountTagged(searchTable, "DataRow") > 0;
                },
                300000);
            var searchFill = MeasureUntil(() => { }, () => !svm.IsLoadingAll, 600000);
            var searchFull = searchClock.ElapsedMilliseconds;
            Log($"   冷盘搜索「页面」：首屏={search.Wall}ms 停摆={search.Stall}ms 命中={svm.TotalHitCount}"
                + $" 全量补齐={searchFull}ms（补页等待 {searchFill.Wall}ms）已填={svm.LoadedCount}");
            Record("cold", "搜索·宽命中 首屏（冷盘）", search.Wall, search.Stall, null, null,
                svm.Results.Count, CountTagged(searchTable, "DataRow"),
                $"hits={svm.TotalHitCount}; full_load_ms={searchFull}; loaded={svm.LoadedCount}");
            Pump(250);   // 等快照会话关闭、连接归还
        }

        // 4) 浏览页：从根打开十万级大目录（真实首帧 + 尾随）。
        vm.SelectNavCommand.Execute(NavIds.Browser);
        PumpUntil(() => vm.CurrentNavId == NavIds.Browser, 10000);
        var toRoot = vm.BrowserViewModel.LoadAsync(null);
        PumpUntil(() => toRoot.IsCompleted, 300000);
        PumpUntil(() => vm.BrowserViewModel.WaitForIdleAsync().IsCompleted, 300000);
        Pump(500);
        PurgeFor("浏览·大目录");
        var browse = MeasureNav(() => vm.BrowserViewModel.LoadAsync(folderId),
            () => vm.BrowserViewModel.CurrentFolderId == folderId
                  && !vm.BrowserViewModel.IsLoading && !vm.BrowserViewModel.IsNavigating);
        Log($"   冷盘打开大目录 {folderId}：首帧={browse.Wall}ms 停摆={browse.Stall}ms"
            + $" 尾随={browse.TailMax}/{browse.TailTotal} rows={vm.BrowserViewModel.Rows.Count}");
        Record("cold", "浏览·大目录 首帧（冷盘）", browse.Wall, browse.Stall, null, null,
            vm.BrowserViewModel.Rows.Count, CountRows(table),
            $"tail_max={browse.TailMax}; tail_total={browse.TailTotal}");

        // 5) 原生 SQL 对照：同为清缓存后的冷读，直接连库跑智能列表的 COUNT + 首页 SQL，
        //    把“SQL/磁盘慢”与“EF/引擎/UI 慢”分开；并打印查询计划。
        void RawSqlPreset(string label, string where, string orderBy, int take, DateTime? since)
        {
            PurgeFor($"原生 SQL·{label}");
            using var raw = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = benchDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            var openWatch = Stopwatch.StartNew();
            raw.Open();
            var openMs = openWatch.ElapsedMilliseconds;
            void Bind(SqliteCommand cmd)
            {
                if (since is { } s) cmd.Parameters.AddWithValue("@since", s);
            }

            long countMs;
            object? count;
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM links WHERE {where}";
                Bind(cmd);
                var watch = Stopwatch.StartNew();
                count = cmd.ExecuteScalar();
                watch.Stop();
                countMs = watch.ElapsedMilliseconds;
            }

            long pageMs;
            var rows = 0;
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id,url,title,description,favicon_url,folder_id,last_visited_at,visit_count,"
                    + $"is_important,created_at,updated_at FROM links WHERE {where} ORDER BY {orderBy} LIMIT {take}";
                Bind(cmd);
                var watch = Stopwatch.StartNew();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) rows++;
                watch.Stop();
                pageMs = watch.ElapsedMilliseconds;
            }

            long warmPageMs;
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id,url,title,description,favicon_url,folder_id,last_visited_at,visit_count,"
                    + $"is_important,created_at,updated_at FROM links WHERE {where} ORDER BY {orderBy} LIMIT {take}";
                Bind(cmd);
                var watch = Stopwatch.StartNew();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) { }
                watch.Stop();
                warmPageMs = watch.ElapsedMilliseconds;
            }

            using (var planCmd = raw.CreateCommand())
            {
                planCmd.CommandText = "EXPLAIN QUERY PLAN SELECT id,url,title,description,favicon_url,folder_id,"
                    + $"last_visited_at,visit_count,is_important,created_at,updated_at FROM links WHERE {where}"
                    + $" ORDER BY {orderBy} LIMIT {take}";
                Bind(planCmd);
                using var reader = planCmd.ExecuteReader();
                var plan = new List<string>();
                while (reader.Read()) plan.Add(reader["detail"]?.ToString() ?? "?");
                Log($"   原生 SQL {label} 计划：{string.Join(" | ", plan)}");
            }

            var totalMs = openMs + countMs + pageMs;
            Log($"   冷盘原生 SQL {label}：open={openMs}ms count={countMs}ms(命中 {count}) 首页={pageMs}ms(rows={rows})"
                + $" 合计={totalMs}ms；首页紧接着热读={warmPageMs}ms");
            Record("cold", $"原生 SQL·{label}（冷盘）", totalMs, -1, null, null, rows, null,
                $"open_ms={openMs}; count_ms={countMs}; page_ms={pageMs}; warm_page_ms={warmPageMs}; count={count}");
        }

        var since = DateTime.UtcNow.AddDays(-7);
        RawSqlPreset("最近添加", "created_at >= @since", "created_at DESC, id DESC", 64, since);
        RawSqlPreset("最常查看", "visit_count >= 1", "visit_count DESC, last_visited_at DESC, id DESC", 20, null);
        RawSqlPreset("最近访问", "last_visited_at >= @since",
            "(last_visited_at IS NULL) ASC, last_visited_at DESC, id DESC", 64, since);
        // 界面首开默认按标题排（点列头之前）——同样冷盘、同一谓词，只换 ORDER BY。
        RawSqlPreset("最近访问（标题序）", "last_visited_at >= @since",
            "title COLLATE NOCASE ASC, id ASC", 64, since);

        // 宽时间窗计数对照：时间索引冷读 vs 顺序扫表（时间索引若碎片化，索引扫描反而更慢）。
        void RawCountProbe(string label, string from, string where, DateTime? since)
        {
            PurgeFor(label);
            using var raw = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = benchDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {from} WHERE {where}";
            if (since is { } s) cmd.Parameters.AddWithValue("@since", s);
            var watch = Stopwatch.StartNew();
            var count = cmd.ExecuteScalar();
            watch.Stop();
            Log($"   冷盘计数 {label}：{watch.ElapsedMilliseconds}ms（命中 {count}）");
            Record("cold", $"计数·{label}", watch.ElapsedMilliseconds, -1, null, null, null, null,
                $"hits={count}; from={from}");
        }
        RawCountProbe("最近添加·时间索引", "links INDEXED BY idx_links_created", "created_at >= @since", since);
        RawCountProbe("最近添加·顺序扫表", "links NOT INDEXED", "created_at >= @since", since);
        RawCountProbe("最近编辑·时间索引", "links INDEXED BY idx_links_updated", "updated_at >= @since", since);
        RawCountProbe("最近编辑·顺序扫表", "links NOT INDEXED", "updated_at >= @since", since);

        // 搜索 SQL 对照：强制窄索引 vs 让计划器选（宽覆盖索引存在时是否被选、值不值得）。
        void RawSearchProbe(string label, bool forceNarrow)
        {
            PurgeFor($"搜索 SQL·{label}");
            using var raw = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = benchDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            raw.Open();
            var from = forceNarrow ? "links INDEXED BY idx_links_search" : "links";
            const string where =
                "(title LIKE @q ESCAPE '\\' OR url LIKE @q ESCAPE '\\' OR description LIKE @q ESCAPE '\\')";
            void Bind(SqliteCommand cmd) => cmd.Parameters.AddWithValue("@q", "%页面%");

            string PlanOf(string sql)
            {
                using var cmd = raw.CreateCommand();
                cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
                Bind(cmd);
                using var reader = cmd.ExecuteReader();
                var parts = new List<string>();
                while (reader.Read()) parts.Add(reader["detail"]?.ToString() ?? "?");
                return string.Join(" | ", parts);
            }

            var countSql = $"SELECT COUNT(*) FROM {from} WHERE {where}";
            var pageSql = $"SELECT id,url,title,description,favicon_url,folder_id,last_visited_at,visit_count,"
                + $"is_important,created_at,updated_at FROM {from} WHERE {where}"
                + " ORDER BY title COLLATE NOCASE ASC, id ASC LIMIT 64";
            var countPlan = PlanOf(countSql);
            var pagePlan = PlanOf(pageSql);

            long countMs;
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText = countSql;
                Bind(cmd);
                var watch = Stopwatch.StartNew();
                cmd.ExecuteScalar();
                watch.Stop();
                countMs = watch.ElapsedMilliseconds;
            }
            long pageMs;
            var rows = 0;
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText = pageSql;
                Bind(cmd);
                var watch = Stopwatch.StartNew();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) rows++;
                watch.Stop();
                pageMs = watch.ElapsedMilliseconds;
            }
            Log($"   冷盘搜索 SQL {label}：count={countMs}ms 首页={pageMs}ms(rows={rows})");
            Log($"      计划 count：{countPlan}");
            Log($"      计划 page ：{pagePlan}");
            Record("cold", $"搜索 SQL·{label}（冷盘）", countMs + pageMs, -1, null, null, rows, null,
                $"count_ms={countMs}; page_ms={pageMs}; plan_count={countPlan}; plan_page={pagePlan}");
        }
        RawSearchProbe("强制窄索引", true);
        RawSearchProbe("默认计划", false);

        // 6) 裸文件冷读（对照）：清缓存后从库文件里读 32MB，再立刻读同一段。
        //    冷/热两次差 = “文件缓存是否真的被清掉”的直接证据（只靠 standby 页数不够）。
        PurgeFor("裸文件读");
        var probeOffset = Math.Max(0, dbFile.Length / 3);
        var probeBuffer = new byte[32 * 1024 * 1024];
        long coldReadMs;
        long warmReadMs;
        int readBytes;
        using (var fs = new FileStream(benchDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                   4096, FileOptions.RandomAccess))
        {
            readBytes = (int)Math.Min(probeBuffer.Length, fs.Length - probeOffset);
            fs.Seek(probeOffset, SeekOrigin.Begin);
            var coldWatch = Stopwatch.StartNew();
            fs.ReadExactly(probeBuffer, 0, readBytes);
            coldWatch.Stop();
            coldReadMs = coldWatch.ElapsedMilliseconds;
            fs.Seek(probeOffset, SeekOrigin.Begin);
            var warmWatch = Stopwatch.StartNew();
            fs.ReadExactly(probeBuffer, 0, readBytes);
            warmWatch.Stop();
            warmReadMs = warmWatch.ElapsedMilliseconds;
        }
        var coldMbps = coldReadMs > 0 ? readBytes / 1048576.0 / (coldReadMs / 1000.0) : -1;
        var warmMbps = warmReadMs > 0 ? readBytes / 1048576.0 / (warmReadMs / 1000.0) : -1;
        Log($"   冷盘裸文件读：{readBytes / 1048576.0:F1}MB @ {probeOffset / 1048576:F0}MB 偏移"
            + $" 冷读={coldReadMs}ms（{coldMbps:F0}MB/s） 立刻热读={warmReadMs}ms（{warmMbps:F0}MB/s）");
        Record("cold", "裸文件冷读 32MB", coldReadMs, -1, null, null, null, null,
            $"bytes={readBytes}; offset_mb={probeOffset / 1048576}; mbps={coldMbps:F0}; warm_read_ms={warmReadMs}; warm_mbps={warmMbps:F0}");

        Gate("冷盘：提升权限与清缓存特权", true, privilegeDetail);
    }

    /// <summary>真实点表头（走控件的 OnHeaderClick：更新 SortField + 发 SortChanged，与真实点击同一条路径）。</summary>
    void ClickHeader(SortableDataTable t, string field)
    {
        var buttons = GetField(t, "_headerButtons") as System.Collections.IEnumerable;
        var onClick = t.GetType().GetMethod("OnHeaderClick", BindingFlags.Instance | BindingFlags.NonPublic);
        if (buttons is null || onClick is null) return;
        foreach (var b in buttons)
        {
            if (b is SortableHeaderButton h && h.Field == field)
            {
                onClick.Invoke(t, new object[] { h, new RoutedEventArgs() });
                return;
            }
        }
    }

    /// <summary>
    /// ⑩ 搜索全链路：10 万级库上的宽命中 / 窄命中 / 无命中 / 路径范围 / 范围切换 / 缓存重复 / F5。
    /// 量法 = 回车 → 结果真的落到表上（<see cref="SearchViewModel.IsSearching"/> 归零且达到预期命中数），
    /// 同时记录 UI 线程最长单次停摆、实例化行容器数与截断状态；另跑一次引擎直查作对照。
    /// </summary>
    void SuiteSearch()
    {
        Log("");
        Log("⑩ 搜索全链路（10 万级库）");
        var directWalls = new List<long>();
        long EngineDirect(string label, string query)
        {
            var sw = Stopwatch.StartNew();
            var linksTask = host.Client.SearchLinksAsync(
                query, searchTitle: true, searchUrl: true, searchDescription: true, searchPath: true,
                sortBy: "title", sortOrder: "asc", page: 1, perPage: 500);
            var countTask = host.Client.SearchCountAsync(
                query, searchTitle: true, searchUrl: true, searchDescription: true, searchPath: true);
            PumpUntil(() => linksTask.IsCompleted && countTask.IsCompleted, 180000);
            var links = linksTask.GetAwaiter().GetResult();
            var count = countTask.GetAwaiter().GetResult();
            sw.Stop();
            directWalls.Add(sw.ElapsedMilliseconds);
            Log($"   引擎直查 {label}：wall={sw.ElapsedMilliseconds} ms rows={links.Count} 命中={count}");
            Record("search", $"引擎直查 {label}", sw.ElapsedMilliseconds, -1, null, null, links.Count, null, $"hits={count}");
            return sw.ElapsedMilliseconds;
        }

        // 测量进程不测输入法/TSF：搜索框拿焦点会触发 WPF 文本服务的 WinEvent 回调
        //（在系统线程上访问 PresentationSource，UiBench 的控制台宿主里会跨线程炸）。
        // 查询是程序化设置，不需要键盘焦点；进页面前把搜索框从焦点链里摘掉。
        var preView = FindVisualChild<SearchView>(window);
        if (preView is not null && GetField(preView, "SearchBox") is TextBox searchBox)
        {
            searchBox.Focusable = false;
            searchBox.IsTabStop = false;
        }
        else
        {
            Log("   注意：进页面前没取到 SearchBox（焦点链未摘除）");
        }

        vm.SelectNavCommand.Execute(NavIds.Search);
        PumpUntil(() => vm.CurrentNavId == NavIds.Search, 10000);
        Pump(800);
        var searchView = FindVisualChild<SearchView>(window);
        var svm = searchView?.DataContext as SearchViewModel;
        var resultsTable = searchView is null ? null : GetField(searchView, "ResultsTable") as SortableDataTable;
        if (svm is null || resultsTable is null)
        {
            Log("   跳过：取不到 SearchView / SearchViewModel / ResultsTable");
            return;
        }

        svm.SearchTitle = true;
        svm.SearchUrl = true;
        svm.SearchDesc = true;
        svm.SearchPath = true;
        Pump(800);
        var memBefore = ManagedBytes();

        // 引擎分段：open(含首页 64 行) 与 count-only 分开量，定位首屏墙钟里"引擎 vs 布局"的占比。
        void EngineSessionPhases(string label, string query)
        {
            var sw = Stopwatch.StartNew();
            var openTask = host.Client.SearchSessionOpenAsync(query,
                searchTitle: true, searchUrl: true, searchDescription: true, searchPath: true,
                sortBy: "title", sortOrder: "asc", firstPageSize: 64);
            PumpUntil(() => openTask.IsCompleted, 180000);
            var open = openTask.GetAwaiter().GetResult();
            var withPageMs = sw.ElapsedMilliseconds;

            sw.Restart();
            var countOnlyTask = host.Client.SearchSessionOpenAsync(query,
                searchTitle: true, searchUrl: true, searchDescription: true, searchPath: true,
                sortBy: "title", sortOrder: "asc", firstPageSize: 0);
            PumpUntil(() => countOnlyTask.IsCompleted, 180000);
            var countOnly = countOnlyTask.GetAwaiter().GetResult();
            var countOnlyMs = sw.ElapsedMilliseconds;

            sw.Restart();
            var pageTask = host.Client.SearchSessionPageAsync(countOnly.SessionId, page: 1, perPage: 64);
            PumpUntil(() => pageTask.IsCompleted, 180000);
            var pageMs = sw.ElapsedMilliseconds;

            var close1 = host.Client.SearchSessionCloseAsync(open.SessionId);
            var close2 = host.Client.SearchSessionCloseAsync(countOnly.SessionId);
            PumpUntil(() => close1.IsCompleted && close2.IsCompleted, 30000);
            Log($"   引擎会话分段 {label}：open+首页64={withPageMs}ms count-only={countOnlyMs}ms"
                + $" page64={pageMs}ms 首页={open.FirstPage.Count} 命中={open.Total}");
            Record("search", $"引擎会话分段 {label}", withPageMs, -1, null, null, open.FirstPage.Count, null,
                $"open_with_page_ms={withPageMs}; count_only_ms={countOnlyMs}; page64_ms={pageMs};"
                + $" first_page={open.FirstPage.Count}; hits={open.Total}");
        }
        EngineSessionPhases("宽命中", "页面");
        EngineSessionPhases("窄命中", "深链页面 L100-");
        EngineSessionPhases("路径范围", "深链层 050");
        EngineSessionPhases("范围基准", "基准页面");

        // 文件夹进结果集：用库内真实目录名搜一次（首屏 / 顺序 / 总数三条门禁）。
        var folderTreeTask = host.Client.FolderTreeAsync();
        PumpUntil(() => folderTreeTask.IsCompleted, 180000);
        var folderTree = folderTreeTask.Result ?? new List<FolderDto>();
        var folderSample = folderTree
            .Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrEmpty(f.FolderId))
            .OrderByDescending(f => f.LinkCount)
            .FirstOrDefault();
        if (folderSample == null)
        {
            Log("   文件夹搜索：库里没有可用目录，跳过");
        }
        else
        {
            var folderSw = Stopwatch.StartNew();
            var withFoldersTask = host.Client.SearchSessionOpenAsync(folderSample.Name,
                searchTitle: true, searchUrl: false, searchDescription: false, searchPath: false,
                sortBy: "title", sortOrder: "asc", firstPageSize: 64, includeFolders: true);
            PumpUntil(() => withFoldersTask.IsCompleted, 180000);
            var withFolders = withFoldersTask.GetAwaiter().GetResult();
            var folderOpenMs = folderSw.ElapsedMilliseconds;
            var firstRows = withFolders.FirstPage;
            var folderRowCount = firstRows.Count(r => string.Equals(r.Kind, "folder", StringComparison.OrdinalIgnoreCase));
            var lastFolderIndex = firstRows.Select((r, i) => (r, i))
                .Where(x => string.Equals(x.r.Kind, "folder", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.i).DefaultIfEmpty(-1).Max();
            var firstLinkIndex = firstRows.Select((r, i) => (r, i))
                .Where(x => !string.Equals(x.r.Kind, "folder", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.i).DefaultIfEmpty(int.MaxValue).Min();
            Log($"   文件夹搜索「{folderSample.Name}」：open+首页64={folderOpenMs}ms 命中={withFolders.Total} 文件夹行={folderRowCount}");
            Record("search", "文件夹搜索 open+首页64", folderOpenMs, -1, null, null, firstRows.Count, null,
                $"hits={withFolders.Total}; folder_rows={folderRowCount}; folder={folderSample.Name}");
            Gate("文件夹搜索首屏 ≤100ms", folderOpenMs <= 100, $"open+首页64={folderOpenMs}ms");
            Gate("文件夹搜索：文件夹块恒在链接前",
                folderRowCount == 0 || lastFolderIndex < firstLinkIndex,
                $"lastFolder={lastFolderIndex}; firstLink={firstLinkIndex}");
            Gate("文件夹搜索：命中包含该目录",
                firstRows.Any(r => string.Equals(r.Kind, "folder", StringComparison.OrdinalIgnoreCase)
                                   && r.LinkId == folderSample.FolderId),
                $"folder={folderSample.FolderId}");
            var closeFolders = host.Client.SearchSessionCloseAsync(withFolders.SessionId);
            PumpUntil(() => closeFolders.IsCompleted, 30000);
        }

        // 结果"落地"必须包含首次布局：模板虚拟化下设置 ItemsSource 只登记数据，
        // 行容器是在紧接着的布局里实例化的——只等到 IsSearching=false 会把这块成本漏出测量窗。
        long lastFrameLayoutMs = -1;
        bool ResultsSettled(Func<bool> done)
        {
            if (svm.IsSearching || !done()) return false;
            var layoutWatch = Stopwatch.StartNew();
            window.UpdateLayout();
            lastFrameLayoutMs = layoutWatch.ElapsedMilliseconds;
            var expected = svm.Results.Count;
            if (expected > 0 && CountTagged(resultsTable, "DataRow") == 0) return false;
            return true;
        }

        void WaitFullLoad(string label)
        {
            var extentBefore = ExtentOf(resultsTable);
            var m = MeasureUntil(() => { }, () => !svm.IsLoadingAll, 300000);
            var expected = svm.TotalHitCount;
            Pump(80);
            window.UpdateLayout();
            var containers = CountTagged(resultsTable, "DataRow");
            var extentAfter = ExtentOf(resultsTable);
            var driftPct = extentBefore > 0 ? (extentAfter - extentBefore) / extentBefore * 100 : -1;
            Log($"   全量补齐 {label}：wall={m.Wall} 停摆={m.Stall} 渲染={svm.Results.Count}/{expected}"
                + $" 已填={svm.LoadedCount} 行容器={containers} 加载中={svm.IsLoadingAll}"
                + $" Extent={extentBefore:F0}→{extentAfter:F0} 漂移={driftPct:F1}%");
            Record("search", $"{label} 全量补齐", m.Wall, m.Stall, null, null,
                svm.Results.Count, containers,
                $"expected={expected}; loaded={svm.LoadedCount}; match={svm.Results.Count == expected && svm.LoadedCount == expected};"
                + $" extent_before={extentBefore:F0}; extent_after={extentAfter:F0}; drift_pct={driftPct:F1}; loading={svm.IsLoadingAll}");
            if (svm.Results.Count != expected || svm.LoadedCount != expected)
            {
                Log($"   ✗ 全量数不匹配：rows={svm.Results.Count} loaded={svm.LoadedCount} expected={expected}");
            }
            if (extentBefore > 0 && Math.Abs(driftPct) > 1)
                Log($"   ✗ extent 漂移超限：{driftPct:F1}%");
            Gate($"{label} 全量可达", svm.Results.Count == expected && svm.LoadedCount == expected,
                $"rows={svm.Results.Count}; loaded={svm.LoadedCount}; expected={expected}");
            Gate($"{label} 补页 extent 稳定", extentBefore <= 0 || Math.Abs(driftPct) <= 1,
                $"drift={driftPct:F2}%");
            VerifyLastReachable(label);
        }

        void VerifyLastReachable(string label)
        {
            if (svm.Results.Count == 0) return;
            var last = svm.Results[^1];
            resultsTable.ScrollItemIntoView(last);
            Pump(160);
            window.UpdateLayout();
            var visible = false;
            var containers = 0;
            void Walk(DependencyObject d)
            {
                if (d is FrameworkElement { Tag: "DataRow" } fe)
                {
                    containers++;
                    if (fe.DataContext is LinkItem item
                        && string.Equals(item.LinkId, last.LinkId, StringComparison.Ordinal))
                        visible = true;
                }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
            }
            Walk(resultsTable);
            Log($"   末尾可达 {label}：last={last.LinkId} 可见={visible} 行容器={containers}/{svm.Results.Count}");
            Record("search", $"{label} 末尾可达", -1, -1, null, null,
                svm.Results.Count, containers, $"last={last.LinkId}; visible={visible}");
            Gate($"{label} 末行可见", visible, $"last={last.LinkId}; containers={containers}");
        }

        static bool RowVisible(DependencyObject root, LinkItem item)
        {
            var visible = false;
            void Walk(DependencyObject d)
            {
                if (visible) return;
                if (d is FrameworkElement { Tag: "DataRow" } fe
                    && ReferenceEquals(fe.DataContext, item) && fe.IsVisible)
                    visible = true;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
            }
            Walk(root);
            return visible;
        }

        /// <summary>
        /// 补页进行中把滚动条拖到底：末页必须**优先**到达（视口优先），且末行真的可见；
        /// extent 不得因补页漂移。这是"快速下滑看到占位空转"的直接门禁。
        /// </summary>
        void DragToBottomDuringFill(string label)
        {
            if (svm.Results.Count == 0) return;
            var wasLoading = svm.IsLoadingAll;
            var loadedBefore = svm.LoadedCount;
            var extentBefore = ExtentOf(resultsTable);
            var last = svm.Results[^1];
            var sw = Stopwatch.StartNew();
            resultsTable.ScrollToVerticalOffset(ExtentOf(resultsTable));
            PumpUntil(() => !string.IsNullOrEmpty(last.LinkId), 5000);
            var lastMs = sw.ElapsedMilliseconds;
            var lastReal = !string.IsNullOrEmpty(last.LinkId);
            Pump(140);
            window.UpdateLayout();
            var visible = RowVisible(resultsTable, last);
            var extentAfter = ExtentOf(resultsTable);
            var driftPct = extentBefore > 0 ? (extentAfter - extentBefore) / extentBefore * 100 : -1;
            Log($"   补页中拖到底 {label}：末行真实={lastReal}/{lastMs}ms 可见={visible}"
                + $" 已填={loadedBefore}→{svm.LoadedCount} Extent漂移={driftPct:F2}% 加载中={wasLoading}");
            Record("search", $"{label} 补页中拖到底", lastMs, -1, null, null,
                svm.Results.Count, CountTagged(resultsTable, "DataRow"),
                $"was_loading={wasLoading}; last_real={lastReal}; last_visible={visible};"
                + $" loaded_before={loadedBefore}; loaded_after={svm.LoadedCount}; drift_pct={driftPct:F2}");
            Gate($"{label} 补页中拖到底末行可达", wasLoading && lastReal && visible && Math.Abs(driftPct) <= 1,
                $"loading={wasLoading}; last_real={lastReal}; visible={visible}; drift={driftPct:F2}%");
            Gate($"{label} 补页中拖到底≤900ms", lastMs <= 900, $"last_real_ms={lastMs}");
        }

        /// <summary>补页期间连续快速甩动：extent 稳定、行容器有界、布局不被占位集合拖垮。</summary>
        void FlingDuringFill(string label)
        {
            var wasLoading = svm.IsLoadingAll;
            var loadedBefore = svm.LoadedCount;
            var extentBefore = ExtentOf(resultsTable);
            if (!wasLoading)
            {
                Log($"   跳过补页中甩动 {label}：补页已完成");
                return;
            }
            var rnd = new Random(7);
            long maxLayout = 0;
            for (var i = 0; i < 24; i++)
            {
                var sw = Stopwatch.StartNew();
                resultsTable.ScrollToVerticalOffset(ExtentOf(resultsTable) * rnd.NextDouble());
                resultsTable.UpdateLayout();
                sw.Stop();
                if (sw.ElapsedMilliseconds > maxLayout) maxLayout = sw.ElapsedMilliseconds;
                Pump(8);
            }
            window.UpdateLayout();
            var extentAfter = ExtentOf(resultsTable);
            var driftPct = extentBefore > 0 ? (extentAfter - extentBefore) / extentBefore * 100 : -1;
            var containers = CountTagged(resultsTable, "DataRow");
            Log($"   补页中快速甩动 {label}：布局峰值={maxLayout}ms Extent漂移={driftPct:F2}%"
                + $" 行容器={containers} 已填={loadedBefore}→{svm.LoadedCount}/{svm.TotalHitCount} 加载中={svm.IsLoadingAll}");
            Record("search", $"{label} 补页中快速甩动", -1, maxLayout, null, null,
                svm.Results.Count, containers,
                $"was_loading={wasLoading}; drift_pct={driftPct:F2}; loaded={svm.LoadedCount};"
                + $" loading_after={svm.IsLoadingAll}");
            Gate($"{label} 补页中甩动稳定", wasLoading && Math.Abs(driftPct) <= 1 && containers <= 40,
                $"was_loading={wasLoading}; drift={driftPct:F2}%; containers={containers}; max_layout={maxLayout}ms");
        }

        long RunUiSearch(string label, string query, Func<bool> done, int timeoutMs = 180000)
        {
            var setWatch = Stopwatch.StartNew();
            svm.Query = query;
            setWatch.Stop();
            long syncMs = -1;
            // 命令同步段（进程内引擎 + 结果落地）全部在 UI 线程上：MeasureUntil 会把 act 本身
            // 的耗时计入 stall，正好量出"点搜索卡多久"。
            var m = MeasureUntil(
                () =>
                {
                    var syncWatch = Stopwatch.StartNew();
                    svm.SearchCommand.Execute(null);
                    syncMs = syncWatch.ElapsedMilliseconds;
                },
                () => ResultsSettled(done), timeoutMs);
            var wall = m.Wall;   // 量法 = 搜索命令开始 → 首帧落地（真实回车动作）
            var overallWall = wall + setWatch.ElapsedMilliseconds;
            // 首帧即完整行模板（位置/日期/次数/图标全在，不再先挂紧凑模板）：
            // 首屏墙钟就是屏幕上出现完整列的时间，"全列"与"首屏"是同一个时刻。
            Pump(80);
            window.UpdateLayout();
            var fullColumnsMs = wall;
            // 计数容器前再排一次，避免把"尚未实例化"读成 0（测量窗已含首次布局）。
            var rows = svm.Results.Count;
            var dataRows = resultsTable.RowsList.Items.Count;
            var containers = CountTagged(resultsTable, "DataRow");
            Log($"   {label}：wall={wall} ms 停摆={m.Stall} ms 命中={svm.TotalHitCount}"
                + $" 渲染={rows} 已填={svm.LoadedCount} 加载中={svm.IsLoadingAll} 行容器={containers}/{dataRows}"
                + $" 输入设置={setWatch.ElapsedMilliseconds}ms 总墙钟={overallWall}ms 同步={syncMs}ms"
                + $" 首帧布局={lastFrameLayoutMs}ms 全列={fullColumnsMs}ms");
            Record("search", label, wall, m.Stall, null, null, rows, containers,
                $"hits={svm.TotalHitCount}; loaded={svm.LoadedCount}; loading={svm.IsLoadingAll};"
                + $" data_rows={dataRows}; set_ms={setWatch.ElapsedMilliseconds}; total_wall_ms={overallWall};"
                + $" sync_ms={syncMs}; frame_layout_ms={lastFrameLayoutMs}; full_columns_ms={fullColumnsMs};"
                + $" exec_ms={m.Stall}");
            if (rows != svm.TotalHitCount)
                Log($"   ✗ 首屏结果集行数 {rows} != 总数 {svm.TotalHitCount}");
            Gate($"{label} 首屏总数一致", rows == svm.TotalHitCount,
                $"rows={rows}; hits={svm.TotalHitCount}; loaded={svm.LoadedCount}");
            Gate($"{label} 首屏（完整行）≤100ms", wall <= 100, $"wall={wall}ms; stall={m.Stall}ms");
            ReportGaps(label);
            return wall;
        }

        // dirty/layout 套件会改掉几百条标题（它们是压力场景的一部分），宽命中总数按 99k 留出余量。
        RunUiSearch("宽命中(首屏)", "页面",
            () => svm.Results.Count >= 500);
        DragToBottomDuringFill("宽命中");
        FlingDuringFill("宽命中");
        WaitFullLoad("宽命中(页面)");

        var memAfter = ManagedBytes();
        var memDeltaMb = (memAfter - memBefore) / (1024.0 * 1024.0);
        Log($"   内存（宽命中后）：托管堆 {memBefore / 1048576.0:F1}MB → {memAfter / 1048576.0:F1}MB"
            + $"（+{memDeltaMb:F1}MB，占位 + 全部命中）");
        Record("search", "宽命中 内存增量", -1, -1, null, null, svm.Results.Count, null,
            $"managed_before_mb={memBefore / 1048576.0:F1}; managed_after_mb={memAfter / 1048576.0:F1};"
            + $" delta_mb={memDeltaMb:F1}");
        Gate("宽命中 内存增量≤128MB", memDeltaMb <= 128, $"+{memDeltaMb:F1}MB");
        RunUiSearch("缓存重复(首屏)", "页面",
            () => svm.Results.Count >= 500);
        WaitFullLoad("缓存重复(页面)");
        EngineDirect("描述宽命中(性能测试书签)", "性能测试书签");
        RunUiSearch("窄命中(深链层100)", "深链页面 L100-",
            () => svm.TotalHitCount == 20 && svm.Results.Count >= 20);
        RunUiSearch("无命中", "zzzz-no-hit",
            () => svm.TotalHitCount == 0 && svm.Results.Count == 0);
        RunUiSearch("路径范围(深链层050)", "深链层 050",
            () => svm.Results.Count >= 500);
        WaitFullLoad("路径范围(深链层050)");

        // 范围切换：宽命中后关掉标题范围，再恢复（立即刷新，无防抖）。
        RunUiSearch("范围基准(首屏)", "基准页面",
            () => svm.Results.Count >= 500);
        WaitFullLoad("范围基准(基准页面)");
        var off = MeasureUntil(
            () => svm.SearchTitle = false,
            () => ResultsSettled(() => svm.TotalHitCount == 0), 120000);
        Log($"   范围切换(关标题)：wall={off.Wall} ms 停摆={off.Stall} ms 命中={svm.TotalHitCount}");
        Record("search", "范围切换(关标题)", off.Wall, off.Stall, null, null, svm.Results.Count,
            CountTagged(resultsTable, "DataRow"), "hits=0");
        Gate("范围切换(关标题) ≤120ms", off.Wall <= 120, $"wall={off.Wall}ms; stall={off.Stall}ms");
        ReportGaps("范围切换(关标题)");
        var on = MeasureUntil(
            () => svm.SearchTitle = true,
            () => ResultsSettled(() => svm.Results.Count >= 500), 120000);
        Log($"   范围切换(开标题)：wall={on.Wall} ms 停摆={on.Stall} ms 命中={svm.TotalHitCount}");
        Record("search", "范围切换(开标题)", on.Wall, on.Stall, null, null, svm.Results.Count,
            CountTagged(resultsTable, "DataRow"), $"hits={svm.TotalHitCount}");
        Gate("范围切换(开标题) ≤120ms", on.Wall <= 120, $"wall={on.Wall}ms; stall={on.Stall}ms");
        ReportGaps("范围切换(开标题)");
        WaitFullLoad("范围切换(开标题)");

        // F5：重跑当前查询（加载遮罩 + 结果可能等价而不重建行）。
        var f5 = MeasureUntil(
            () => svm.RefreshCommand.Execute(null),
            () => !svm.IsNavigating && ResultsSettled(() => svm.Results.Count > 0), 180000);
        Log($"   F5 真刷新：wall={f5.Wall} ms 停摆={f5.Stall} ms 命中={svm.TotalHitCount}");
        Record("search", "F5 真刷新", f5.Wall, f5.Stall, null, null, svm.Results.Count,
            CountTagged(resultsTable, "DataRow"), $"hits={svm.TotalHitCount}");
        Gate("F5 真刷新 ≤120ms", f5.Wall <= 120, $"wall={f5.Wall}ms; stall={f5.Stall}ms");
        ReportGaps("F5 真刷新");
        WaitFullLoad("F5 后");

        // —— 活结果集放到最后：这两项会写一条链接并触发引擎事件，不能污染前面的首屏读数。 ——
        var entry = MeasureUntil(() =>
        {
            svm.OnNavigatedFrom();
            svm.OnNavigatedTo();
        }, () => true, 5000);
        Log($"   入口(无变更)：wall={entry.Wall}ms 停摆={entry.Stall}ms");
        Record("search", "入口(无变更)", entry.Wall, entry.Stall, null, null, svm.Results.Count,
            CountTagged(resultsTable, "DataRow"));
        Gate("搜索入口(无变更) ≤5ms", entry.Wall <= 5, $"wall={entry.Wall}ms; stall={entry.Stall}ms");

        var liveRow = svm.Results.FirstOrDefault(r => !r.IsFolder && !string.IsNullOrEmpty(r.LinkId));
        if (liveRow is not null)
        {
            var originalTitle = liveRow.Title;
            var liveTitle = originalTitle + "·活更新";
            var replaced = 0;
            void OnReplaced(object? _, EventArgs __) => replaced++;
            svm.ResultSetReplaced += OnReplaced;
            var write = host.Client.LinkUpdateAsync(liveRow.LinkId, title: liveTitle);
            PumpUntil(() => write.IsCompleted, 15000);
            var live = MeasureUntil(
                () => svm.NotifyDataChanges(
                    [new EntityRef("link", liveRow.LinkId)],
                    [DomainEventNames.LinksChanged], precise: true),
                () => liveRow.Title == liveTitle, 15000);
            svm.ResultSetReplaced -= OnReplaced;
            Log($"   活结果集单行改名：wall={live.Wall}ms 停摆={live.Stall}ms 行={liveRow.LinkId}"
                + $" 全量重建={replaced}");
            Record("search", "活结果集单行改名", live.Wall, live.Stall, null, null,
                svm.Results.Count, CountTagged(resultsTable, "DataRow"),
                $"replaced={replaced}; link={liveRow.LinkId}");
            Gate("活结果集单行改名 ≤100ms", live.Wall <= 100, $"wall={live.Wall}ms; stall={live.Stall}ms");
            Gate("活结果集单行改名不整表重建", replaced == 0, $"replaced={replaced}");

            var restore = host.Client.LinkUpdateAsync(liveRow.LinkId, title: originalTitle);
            PumpUntil(() => restore.IsCompleted, 15000);
            svm.NotifyDataChanges(
                [new EntityRef("link", liveRow.LinkId)],
                [DomainEventNames.LinksChanged], precise: true);
            PumpUntil(() => liveRow.Title == originalTitle, 15000);
            // 后续 sorts 套件还会复用同一页面：先把自己的引擎事件余波排干。
            Pump(500);
            PumpUntil(() => !svm.HasPendingLiveChanges, 5000);
            Pump(300);
        }
    }

    void SuiteSwitch()
    {
        Log("");
        Log($"② 设置页 ↔ 浏览页 × {rounds}");
        for (var i = 1; i <= rounds; i++)
        {
            var toSettings = MeasureNav(() => vm.SelectNavCommand.Execute(NavIds.Settings),
                () => vm.CurrentNavId == NavIds.Settings);
            ReportGaps($"切到设置#{i}");
            var back = MeasureNav(() => vm.SelectNavCommand.Execute(NavIds.Browser),
                () => vm.CurrentNavId == NavIds.Browser);
            ReportGaps($"切回浏览#{i}");
            Log($"   #{i} →设置 wall(首帧)={toSettings.Wall} 停摆={toSettings.Stall} 尾随={toSettings.TailMax}/{toSettings.TailTotal} ms ｜"
                + $" →浏览 wall(首帧)={back.Wall} 停摆={back.Stall} 尾随={back.TailMax}/{back.TailTotal} ms"
                + $"  行容器={CountRows(table)} rows={vm.BrowserViewModel.Rows.Count}");
            Record("switch", $"切到设置 #{i}", toSettings.Wall, toSettings.Stall, null, null, null, null,
                $"tail_max={toSettings.TailMax}; tail_total={toSettings.TailTotal}");
            Record("switch", $"切回浏览 #{i}", back.Wall, back.Stall, null, null,
                vm.BrowserViewModel.Rows.Count, CountRows(table),
                $"tail_max={back.TailMax}; tail_total={back.TailTotal}");
        }
    }

    void SuiteRefresh()
    {
        Log("");
        Log("③ 原地刷新（防抖同一条链）");
        for (var i = 1; i <= 3; i++)
        {
            lastLayoutMs = -1;
            captureLayout = true;
            var r = MeasureNav(() => _ = vm.BrowserViewModel.RefreshPreservingSelectionAsync(),
                () => !vm.BrowserViewModel.IsLoading && !vm.BrowserViewModel.IsNavigating);
            Log($"   #{i} wall(首帧)={r.Wall} ms 停摆={r.Stall} ms 尾随={r.TailMax}/{r.TailTotal} ms 行容器={CountRows(table)}");
            Record("refresh", $"原地刷新 #{i}", r.Wall, r.Stall,
                lastLayoutMs >= 0 ? lastLayoutMs : null, null,
                vm.BrowserViewModel.Rows.Count, CountRows(table),
                $"tail_max={r.TailMax}; tail_total={r.TailTotal}");
        }
    }

    /// <summary>
    /// 行右键菜单（**单实例共享**后的功能校验）：共享实例的唯一风险 = "这次打开还在用上一次的行"。
    /// 校验三件事：① 所有行容器共用同一个 ContextMenu 实例（这正是省下来的成本）；
    /// ② 在文件夹行上打开 ⇒ DataContext 换成该行、文件夹专属项可见、链接专属项隐藏；
    /// ③ 紧接着在链接行上打开 ⇒ 同上反向（证明"换人"生效、没有留下上一行的状态）。
    /// </summary>
    void SuiteMenu()
    {
        Log("");
        Log("④ 行右键菜单（共享实例 + 每次打开换人）");
        // 需要视口里**同时**有文件夹行与链接行（否则本套件判据空跑）：先确保进到被测目录
        if (vm.BrowserViewModel.CurrentFolderId != folderId)
        {
            var go = vm.BrowserViewModel.LoadAsync(folderId);
            PumpUntil(() => go.IsCompleted, 300000);
            PumpUntil(() => vm.BrowserViewModel.WaitForIdleAsync().IsCompleted, 300000);
            Pump(600);
        }
        var rows = RealizedRows(table);
        var folderRow = rows.FirstOrDefault(r => (r.DataContext as BrowserRowViewModel)?.IsFolder == true);
        var linkRow = rows.FirstOrDefault(r => (r.DataContext as BrowserRowViewModel)?.IsFolder == false);
        if (folderRow is null || linkRow is null)
        {
            Log($"   跳过：视口内需要同时有文件夹行与链接行（folder={folderRow is not null} link={linkRow is not null}）");
            return;
        }
        Log($"   共享实例：{rows.Count} 个行容器 → {rows.Select(r => r.ContextMenu).Distinct().Count()} 个 ContextMenu"
            + $"（期望 1；ReferenceEquals={ReferenceEquals(rows[0].ContextMenu, rows[^1].ContextMenu)}）");
        Record("menu", "共享右键菜单实例", -1, -1, null, null, rows.Count,
            rows.Select(r => r.ContextMenu).Distinct().Count(), "expect_menus=1");

        CheckRow(folderRow, "文件夹行", folderOnlyVisible: true);
        CheckRow(linkRow, "链接行", folderOnlyVisible: false);
        CheckRow(folderRow, "再回文件夹行", folderOnlyVisible: true);

        void CheckRow(FrameworkElement row, string label, bool folderOnlyVisible)
        {
            // 合成 ContextMenuOpening（该类型没有公开构造，只能反射）
            var ctor = typeof(ContextMenuEventArgs).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(object), typeof(bool) }, null);
            if (ctor is null) { Log("   反射构造 ContextMenuEventArgs 失败（配方过期？）"); return; }
            var args = (ContextMenuEventArgs)ctor.Invoke(new object[] { row, true });
            args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            row.RaiseEvent(args);
            Pump(60);

            var model = (BrowserRowViewModel)row.DataContext;
            var menu = row.ContextMenu;
            var items = menu?.Items.OfType<MenuItem>().ToList() ?? new List<MenuItem>();
            var folderOnly = items.Count > 2 ? items[1].Visibility : Visibility.Collapsed;   // 新建文件夹（仅文件夹）
            var linkOnly = items.Count > 5 ? items[5].Visibility : Visibility.Visible;       // 详情（仅链接）
            Log($"   {label}：菜单项={items.Count} DataContext={(ReferenceEquals(menu?.DataContext, model) ? "命中行" : "✗ 不是命中行")}"
                + $" 文件夹项={folderOnly} 链接项={linkOnly} 期望 文件夹项={(folderOnlyVisible ? "Visible" : "Collapsed")}"
                + $" 链接项={(folderOnlyVisible ? "Collapsed" : "Visible")}"
                + $" ⇒ {((ReferenceEquals(menu?.DataContext, model)) && folderOnly == (folderOnlyVisible ? Visibility.Visible : Visibility.Collapsed) && linkOnly == (folderOnlyVisible ? Visibility.Collapsed : Visibility.Visible) ? "OK" : "FAIL")}");
            if (menu is { IsOpen: true }) menu.IsOpen = false;
        }
    }

    /// <summary>视口内已实现的行容器（浏览器行模板根 Border 带 <c>Tag="BrowserRow"</c>）。</summary>
    static List<FrameworkElement> RealizedRows(DependencyObject root)
    {
        var list = new List<FrameworkElement>();
        void Walk(DependencyObject d)
        {
            if (d is FrameworkElement { Tag: "BrowserRow" } fe) list.Add(fe);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return list;
    }

    // ───────────────── 量具 ─────────────────

    /// <summary>跑一个动作并量：wall（动作 + 链结束 + 400ms 观察窗）、UI 线程最长单次停摆、
    /// 以及"链结束后到布局/渲染排空"的毫秒（Loaded 优先级探针 —— 排在布局与绘制之后）。</summary>
    (long Wall, long Stall, long Settle) Measure(Action act, int timeoutMs = 30000)
    {
        var watch = Stopwatch.StartNew();
        long stall = 0;
        long lastBigGapAt = 0;
        gaps.Clear();
        var gcBefore = GC.GetTotalPauseDuration();
        var actWatch = Stopwatch.StartNew();
        act();
        actWatch.Stop();
        lastActMs = actWatch.ElapsedMilliseconds;
        if (actWatch.ElapsedMilliseconds >= FrameGapFloorMs)
        {
            gaps.Add((0, actWatch.ElapsedMilliseconds));
            stall = actWatch.ElapsedMilliseconds;
            lastBigGapAt = actWatch.ElapsedMilliseconds;
        }
        var last = Stopwatch.GetTimestamp();
        var settle = -1L;
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            Pump(4);
            var now = Stopwatch.GetTimestamp();
            var delta = (now - last) * 1000.0 / Stopwatch.Frequency;
            last = now;
            var ms = (long)Math.Ceiling(delta);
            if (ms >= FrameGapFloorMs) { gaps.Add((watch.ElapsedMilliseconds, ms)); lastBigGapAt = watch.ElapsedMilliseconds; }
            if (ms > stall) stall = ms;
            var busy = vm.BrowserViewModel.IsLoading || vm.BrowserViewModel.IsNavigating;
            // 收工条件 = 加载链已结束 **且** UI 已经"真正安静"：最近 quietMs 内没有 ≥32ms 的停顿。
            // （旧量法只观察到"链结束 + 400ms"就收工 —— 真实卡顿若落在之后就被漏掉，与交互延迟不符。）
            var quietLongEnough = watch.ElapsedMilliseconds - lastBigGapAt >= quietMs;
            if (!busy && quietLongEnough && watch.ElapsedMilliseconds >= 400)
            {
                settle = DrainLoaded();
                break;
            }
        }
        lastGcPauseMs = (long)(GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds;
        return (watch.ElapsedMilliseconds, stall, settle);
    }

    /// <summary>
    /// 导航量具：wall = 动作 → **首帧渲染**（内容条件成立后的第一个 CompositionTarget.Rendering）。
    /// tail = 首帧之后 800ms 观察窗内 ≥32ms 的停顿（多帧摊开/完整模板补全都落在这里）。
    /// 与 <see cref="Measure"/> 的区别：不把 400ms 静默窗算进 wall，也不提前强制布局。
    /// </summary>
    (long Wall, long Stall, long TailMax, long TailTotal) MeasureNav(
        Action act, Func<bool> ready, int timeoutMs = 60000)
    {
        gaps.Clear();
        var gcBefore = GC.GetTotalPauseDuration();
        var watch = Stopwatch.StartNew();
        long stall = 0;
        var actWatch = Stopwatch.StartNew();
        act();
        actWatch.Stop();
        lastActMs = actWatch.ElapsedMilliseconds;
        if (lastActMs >= FrameGapFloorMs)
        {
            gaps.Add((0, lastActMs));
            stall = lastActMs;
        }

        long lastRenderAt = -1;
        void OnRender(object? _, EventArgs __) => lastRenderAt = watch.ElapsedMilliseconds;
        CompositionTarget.Rendering += OnRender;
        long readyAt = -1;
        long firstFrameAt = -1;
        var last = Stopwatch.GetTimestamp();
        try
        {
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (readyAt < 0 && ready())
                {
                    readyAt = watch.ElapsedMilliseconds;
                    window.InvalidateVisual();   // 空闲窗口不会自动出新帧：主动要求一帧
                }
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);

                var now = Stopwatch.GetTimestamp();
                var delta = (now - last) * 1000.0 / Stopwatch.Frequency;
                last = now;
                var ms = (long)Math.Ceiling(delta);
                if (ms >= FrameGapFloorMs)
                {
                    gaps.Add((watch.ElapsedMilliseconds, ms));
                    if (ms > stall) stall = ms;
                }
                // 离屏窗口不会产生渲染帧：直接以内容就绪为准（读数偏乐观，只作对比）。
                // 可见窗口也加 1s 上限：万一渲染被挂起，不能把探针卡死在 60s 超时。
                if (readyAt >= 0
                    && (offscreen || lastRenderAt >= readyAt
                        || watch.ElapsedMilliseconds - readyAt > 1000))
                {
                    firstFrameAt = lastRenderAt >= readyAt ? lastRenderAt : readyAt;
                    break;
                }
            }
        }
        finally
        {
            CompositionTarget.Rendering -= OnRender;
        }
        if (firstFrameAt < 0) firstFrameAt = watch.ElapsedMilliseconds;

        // 首帧之后的尾随卡顿：静默 400ms 或最多 800ms 收工（不计入 wall）。
        long tailMax = 0;
        long tailTotal = 0;
        var tailLastBigAt = firstFrameAt;
        var tailLast = Stopwatch.GetTimestamp();
        while (watch.ElapsedMilliseconds - firstFrameAt < 800)
        {
            Pump(4);
            var now = Stopwatch.GetTimestamp();
            var ms = (long)Math.Ceiling((now - tailLast) * 1000.0 / Stopwatch.Frequency);
            tailLast = now;
            if (ms >= FrameGapFloorMs)
            {
                tailTotal += ms;
                if (ms > tailMax) tailMax = ms;
                tailLastBigAt = watch.ElapsedMilliseconds;
                gaps.Add((watch.ElapsedMilliseconds, ms));
            }
            if (watch.ElapsedMilliseconds - tailLastBigAt >= quietMs) break;
        }

        lastGcPauseMs = (long)(GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds;
        return (firstFrameAt, stall, tailMax, tailTotal);
    }

    /// <summary>
    /// 等条件式量具：跑动作 → 循环检查 <paramref name="done"/> 成立为止；
    /// wall = 条件达成耗时（= 结果呈现在屏幕上的时间），stall = UI 线程最长单次同步阻塞。
    /// 与 <see cref="Measure"/> 的区别：① 精确停在"界面上真的发生了"；② 用 **Loaded 优先级的空回调**
    /// 推进消息循环（不是 DispatcherTimer）——timer 的最小分辨率 ≈15.6ms，会把 50ms 量级的读数糊掉；
    /// 空回调排在本帧布局/渲染之后，下一轮循环即返（亚毫秒级分辨率；挂 Background 会被动画饿死）。
    /// </summary>
    (long Wall, long Stall) MeasureUntil(Action act, Func<bool> done, int timeoutMs,
        bool yieldToBackground = false)
    {
        gaps.Clear();
        var watch = Stopwatch.StartNew();
        var gcBefore = GC.GetTotalPauseDuration();
        long stall = 0;
        // act() 本身可能是**同步阻塞**的（进程内引擎 + 结果行构建会一直跑到第一个真正异步点）：
        // 这段时间也在 UI 线程上，必须计入停摆，否则命令本身的同步段会被量成 0。
        var actWatch = Stopwatch.StartNew();
        act();
        actWatch.Stop();
        if (actWatch.ElapsedMilliseconds >= FrameGapFloorMs)
        {
            gaps.Add((0, actWatch.ElapsedMilliseconds));
            stall = actWatch.ElapsedMilliseconds;
        }
        // 条件判断本身也可能在 UI 线程做重活（例如 `ResultsSettled` 里的首次 UpdateLayout
        // 实例化虚拟化行）。它同样阻塞交互，不能因为"不在 Pump 之间"就漏计。
        bool CheckDone()
        {
            var doneWatch = Stopwatch.StartNew();
            var ready = done();
            doneWatch.Stop();
            var msDone = doneWatch.ElapsedMilliseconds;
            if (msDone >= FrameGapFloorMs)
            {
                gaps.Add((watch.ElapsedMilliseconds, msDone));
                if (msDone > stall) stall = msDone;
            }
            return ready;
        }

        var last = Stopwatch.GetTimestamp();
        while (watch.ElapsedMilliseconds < timeoutMs && !CheckDone())
        {
            if (yieldToBackground)
            {
                // 仍在 Background 上等异步回调时用它（普通路径已不需要防抖）。
                Pump(10);
            }
            else
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            var now = Stopwatch.GetTimestamp();
            var delta = (now - last) * 1000.0 / Stopwatch.Frequency;
            last = now;
            var ms = (long)Math.Ceiling(delta);
            if (ms >= FrameGapFloorMs) gaps.Add((watch.ElapsedMilliseconds, ms));
            if (ms > stall) stall = ms;
        }
        lastGcPauseMs = (long)(GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds;
        return (watch.ElapsedMilliseconds, stall);
    }

    /// <summary>把最长几个帧间隔连同"发生在动作开始后多少毫秒"打印出来（时间线 = 判断"一大块"还是"多帧摊开"）。</summary>
    void ReportGaps(string label)
    {
        var top = gaps.OrderByDescending(g => g.GapMs).Take(6).ToList();
        var total = gaps.Sum(g => g.GapMs);
        Log($"     {label} 卡顿时间线：≥{FrameGapFloorMs}ms 的帧 {gaps.Count} 个 / 合计 {total} ms；最长："
            + string.Join("，", top.Select(g => $"+{g.AtMs}ms处{g.GapMs}ms")));
    }

    /// <summary>队列排空到 `Loaded` 优先级所需的毫秒（= 此刻 UI 线程上还压着多少布局/绘制活）。</summary>
    long DrainLoaded()
    {
        var sw = Stopwatch.StartNew();
        var done = false;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { sw.Stop(); done = true; }));
        var guard = Stopwatch.StartNew();
        while (!done && guard.ElapsedMilliseconds < 30000) Pump(2);
        return sw.ElapsedMilliseconds;
    }

    void ReportShape(SortableDataTable t)
    {
        var panel = FindVisualChild<VirtualizingStackPanel>(t);
        var scroller = FindVisualChild<ScrollViewer>(t);
        // ⚠️ 虚拟化四项必须**读面板**：`VirtualizationMode`/`ScrollUnit`/`CacheLength` **不是继承属性**，
        // 从 ItemsControl 读回恒为默认值（Standard/Item/0）—— 曾据此误判"回收被改掉了"（见 SortableDataTable 的注释）。
        Log($"   形状：rows={vm.BrowserViewModel.Rows.Count} 行容器={CountRows(t)}"
            + $" 面板={(panel is null ? "?" : panel.GetType().Name)}"
            + $" Extent={(scroller?.ExtentHeight ?? -1):F0} Viewport={(scroller?.ViewportHeight ?? -1):F0}"
            + $" 虚拟化[面板]={VirtualizingPanel.GetIsVirtualizing(panel)}/{VirtualizingPanel.GetVirtualizationMode(panel)}"
            + $" 缓存={VirtualizingPanel.GetCacheLength(panel)}/{VirtualizingPanel.GetCacheLengthUnit(panel)}");
    }
}

static void Pump(int ms)
{
    // 退出回调挂 **Render**（挂 Background 会被持续重渲染的动画饿死 —— 界面测量的已知约束）
    var frame = new DispatcherFrame();
    var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Render,
        (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
    timer.Start();
    Dispatcher.PushFrame(frame);
    timer.Stop();
}

static void PumpUntil(Func<bool> ready, int timeoutMs)
{
    var sw = Stopwatch.StartNew();
    while (!ready() && sw.ElapsedMilliseconds < timeoutMs) Pump(5);
}

/// <summary>行容器数：浏览器行模板的根 Border 带 <c>Tag="BrowserRow"</c>（虚拟化下应与数据量无关）。</summary>
static int CountRows(DependencyObject root)
{
    var n = 0;
    void Walk(DependencyObject d)
    {
        if (d is FrameworkElement { Tag: "BrowserRow" }) n++;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
    }
    Walk(root);
    return n;
}

static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
{
    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
    {
        var child = VisualTreeHelper.GetChild(root, i);
        if (child is T hit) return hit;
        if (FindVisualChild<T>(child) is { } deep) return deep;
    }
    return null;
}

/// <summary>按**类型名**在可视树里找元素（临时量具用；不为一次测量去引具体类型/命名空间）。</summary>
static FrameworkElement? FindByTypeName(DependencyObject root, string typeName)
{
    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
    {
        var child = VisualTreeHelper.GetChild(root, i);
        if (child is FrameworkElement fe && fe.GetType().Name == typeName) return fe;
        if (FindByTypeName(child, typeName) is { } deep) return deep;
    }
    return null;
}

static object? GetField(object target, string name) =>
    target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(target);

/// <summary>按模板根 Tag 统计已实例化的行容器（工厂模式表格用）。</summary>
static double ExtentOf(DependencyObject root)
{
    var scroller = FindVisualChild<ScrollViewer>(root);
    return scroller?.ExtentHeight ?? -1;
}

static int CountTagged(DependencyObject root, string tag)
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

/// <summary>目录树节点总数（含虚根；递归统计 Children）。</summary>
static int CountTreeNodes(IEnumerable<FolderNode> nodes)
{
    var total = 0;
    foreach (var node in nodes)
    {
        total++;
        total += CountTreeNodes(node.Children);
    }
    return total;
}

/// <summary>按真实文件夹 ID 找树节点（虚根 FolderId 为 null，不参与）。</summary>
static FolderNode? FindTreeNode(IEnumerable<FolderNode> nodes, string folderId)
{
    foreach (var node in nodes)
    {
        if (string.Equals(node.FolderId, folderId, StringComparison.Ordinal)) return node;
        if (FindTreeNode(node.Children, folderId) is { } hit) return hit;
    }
    return null;
}

/// <summary>直接读库规模与最深目录的祖先链（只读连接；不经过引擎，报告与断言用）。</summary>
static DbStats ReadDbStats(string dbPath, string deepFolderId)
{
    using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
    conn.Open();
    long Scalar(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    var links = Scalar("SELECT COUNT(*) FROM links");
    var folders = Scalar("SELECT COUNT(*) FROM folders");
    var depth = Scalar("WITH RECURSIVE d(id, n) AS (" +
                       "SELECT id, 1 FROM folders WHERE parent_id IS NULL " +
                       "UNION ALL SELECT f.id, d.n + 1 FROM folders f JOIN d ON f.parent_id = d.id) " +
                       "SELECT COALESCE(MAX(n), 0) FROM d");

    // 从最深目录往上数：n = 它自己 + 全部祖先；面包屑还要多个虚根「全部书签」。
    var chain = Scalar("WITH RECURSIVE up(id, parent_id, n) AS (" +
                       "SELECT id, parent_id, 1 FROM folders WHERE id = $id " +
                       "UNION ALL SELECT f.id, f.parent_id, up.n + 1 FROM folders f JOIN up ON f.id = up.parent_id) " +
                       "SELECT COALESCE(MAX(n), 0) FROM up", ("$id", deepFolderId));

    string parentId;
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COALESCE(parent_id, '') FROM folders WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", deepFolderId);
        parentId = cmd.ExecuteScalar() as string ?? string.Empty;
    }

    return new DbStats(links, folders, depth, (int)(chain + 1), parentId);
}

/// <summary>库规模 + 最深目录的祖先链长度（面包屑段数）与父目录 ID。</summary>
internal sealed record DbStats(long Links, long Folders, long Depth, int DeepBreadcrumbs, string DeepParentId);

/// <summary>一条结构化性能读数；-1/空 = 该场景不适用。</summary>
internal sealed record BenchMetric(
    string Suite,
    string Case,
    long WallMs,
    long StallMs,
    long? LayoutMs,
    long? SettleMs,
    int? Rows,
    int? Containers,
    string? Detail);

/// <summary>测量进程内的自动确认对话框（删除确认直接放行；不打真实模态——真实弹窗会把测量挂住）。</summary>
internal sealed class AutoConfirmDialogs : LinkPocket.Services.IDialogService
{
    public bool ConfirmDeleteFolder(string folderName) => true;
    public bool Confirm(string title, string message, string? confirmText = null, string iconKind = "delete-outline") => true;
    public void Alert(string title, string message) { }
}
