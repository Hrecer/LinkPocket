using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using LinkPocket.Contracts;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using LinkPocket.Views;

using LinkPocket.I18n;
using Microsoft.Extensions.DependencyInjection;

namespace LinkPocket;

/// <summary>
/// Shell（窗口壳）：标题栏/窗口态/全局导航药丸 + 端口实现（IDialogService/INavigationService/IBrowserLocateHost）。
/// 页面职责全部在各自 View + ViewModel：搜索页 = Views/SearchView + SearchViewModel，
/// 其余页各自持有 ViewModel；本窗口只做装配与端口转发，不持有任何页面业务逻辑。
/// </summary>
public partial class MainWindow : Window, Services.IDialogService, Services.INavigationService, Services.IBrowserLocateHost,
    Services.IBrowseEditState, Services.IUiNoticeService
{
    private readonly Services.AppHost _host;

    private readonly IServiceScope _scope;

    private readonly Services.ViewRegistry _regions = new();

    private readonly SearchViewModel _searchVm;

    /// <summary>启动直达目录（<c>--folder &lt;id&gt;</c>，供外部工具/深路径交互实测使用）。</summary>
    internal string? StartupFolderId { get; set; }


    /// <summary>用户正在拖动/缩放（`WM_ENTERSIZEMOVE`…`WM_EXITSIZEMOVE`）。圆角只在循环外重判：
    /// 循环内跟着矩形走，窗口擦过一个贴靠位就会一帧方一帧圆（现场：角在跳）。</summary>
    private bool _inSizeMove;

    public MainWindow(Services.AppHost host)
    {
        _host = host;
        InitializeComponent();

        // 标题栏的**整条高度**都算窗口拖拽区：原来 CaptionHeight 固定 44，而标题栏实际更高，
        // 下部那截落在标题栏里却不属于 caption → 拖不动。交互控件已标 IsHitTestVisibleInChrome，
        // 空白处（含底部内边距）统一交给系统 caption 拖动。
        TitleBar.SizeChanged += (_, _) => UpdateCaptionHeight();
        Loaded += (_, _) => UpdateCaptionHeight();

        // 拖拽游标兜底（资源管理器口径）：应用内永远不显示“禁止”，只有拖出窗口才交给系统显示。
        // 内层落点会把非法区设成 None；这里在冒泡末端（含已处理事件）恢复成移动/复制。
        AddHandler(DragDrop.DragEnterEvent, new DragEventHandler(WindowDragCursorFallback), true);
        AddHandler(DragDrop.DragOverEvent, new DragEventHandler(WindowDragCursorFallback), true);
        // 页面对象图按窗口取一份（容器 Scoped）：再开一个窗口拿到的是干净的 VM 图，
        // 不继承上一个窗口的选中/编辑态；窗口关闭时随作用域一起释放。
        _scope = host.Services.CreateScope();
        var vm = _scope.ServiceProvider.GetRequiredService<MainViewModel>();
        DataContext = vm;
        // 端口登记：本窗口实现 IDialogService/INavigationService/IBrowserLocateHost，
        // 组合根持有槽位实例，ViewModel 经构造注入消费——不再经过任何静态注册点。
        _host.Ports.Dialogs = this;
        _host.Ports.Navigation = this;
        _host.Ports.BrowseEdit = this;
        _host.Ports.Notices = this;
        _host.LocateHost = this;

        // ===== 区域视图注册 / 路由装配：navId → 页面的专一装配登记点 =====
        // 页面不再持有组合根（Host 已废除），依赖由 Shell 经窄接口注入；
        // 页面 DataContext = 各自的 ViewModel（浏览页=BrowserViewModel，其余页见下）。
        // ⚠️ 唯一事实来源：**这一份注册表同时是导航表的唯一登记点** —— 页面显隐由本窗口按它重投影
        //（见 ApplyNavVisibility），XAML 里不再有第二份 navId 字面量触发器，NavIds 常量只在注册处用一次。
        // 新增页面 = 注册一条 + 注入依赖即可。
        _regions.Register(NavIds.Browser, BrowserPage);
        _regions.Register(NavIds.Search, SearchView);
        _regions.Register(NavIds.Trash, TrashView);
        _regions.Register(NavIds.SmartLists, SmartListsView);
        _regions.Register(NavIds.Ai, AiPage);
        _regions.Register(NavIds.Tools, ToolsView);
        _regions.Register(NavIds.Settings, SettingsView);

        // 显隐投影：CurrentNavId 变一次重投影一次（注册表 = 画布，注册了谁就管谁）
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentNavId) && DataContext is MainViewModel active)
            {
                // 用户切页 = 真实使用开始：任何还没跑完的启动预热都让路。
                active.SmartListViewModel.CancelStartupWarm();
                _searchVm?.CancelStartupWarm();
                ApplyNavVisibility(active.CurrentNavId);
            }
        };
        ApplyNavVisibility(vm.CurrentNavId);

        BrowserPage.DataContext = vm.BrowserViewModel;
        // 搜索页（MVVM）：ViewModel 由容器解析（登记表见 AppServiceGraph）；「位置」路径解析
        // 复用 MainViewModel 的目录树（与浏览页/智能列表同一份）。
        _searchVm = _scope.ServiceProvider.GetRequiredService<SearchViewModel>();
        SearchView.DataContext = _searchVm;
        TrashView.DataContext = vm.TrashViewModel;
        SmartListsView.DataContext = vm.SmartListViewModel;
        // 工具页/设置页：依赖由容器解析后经 Configure 注入（页面是 XAML 实例化的控件，
        // 不认识容器；navigation = 本窗口，实现 INavigationService 端口）。
        // locator 仍是 ID 跳转工具用的"进目录 + 选中行"组件，两者并存、互不替代。
        var client = _scope.ServiceProvider.GetRequiredService<EngineClient>();
        var locator = _scope.ServiceProvider.GetRequiredService<IContentLocator>();
        ToolsView.Configure(client, locator, this,
            listId => vm.ResolveLinkPathAsync(listId),
            _host.Ports.BrowseEdit, _host.Ports.Notices);
        SettingsView.Configure(client, vm.ReinitializeDatabaseAsync,
            () => vm.RefreshFolderTreeAndUIAsync(), _host.Ai, _host.Updates);
        AiPage.Configure(_host.Ai);
        vm.ToolsDataChanged += (_, _) => _ = ToolsView.OnExternalDataChangedAsync();
        // 进入工具页：入口对齐（去重结果可能已被其它页面的变更置于陈旧；页内按视图状态决定重跑）
        vm.NavigatedToTools += (_, _) => _ = ToolsView.OnNavigatedToAsync();

        // MainViewModel 的 search 路由事件 → 搜索页 ViewModel（进入保内容+静默刷新 / 离开清选中 / 数据变更重跑）
        vm.NavigatedToSearch += (_, _) => _searchVm.OnNavigatedTo();
        vm.NavigatedFromSearch += (_, _) => _searchVm.OnNavigatedFrom();
        vm.SearchRefreshRequested += (_, _) => _ = _searchVm.RefreshFromEventAsync();

        // 右键必有「刷新」（标题栏/导航条除外）：菜单里的刷新项走各页登记的刷新命令，
        // 与 F5 是同一个命令对象（见 PageRefresh / ContextRefresh）。
        ContextRefresh.Install(this, TitleBar);

        Loaded += MainWindow_Loaded;
        // 圆角是分层窗自己画的（Win10 没有 DWM 圆角）。窗口矩形 = 可见窗口，所以这里只管一件事：
        // 最大化时把圆角收成 0（四角本来就在屏幕外，留着只会在屏幕上缺出四个缺口）。
        StateChanged += (_, _) => ApplyCornerRadius();
        // 裁剪必须跟着**壳自己的**尺寸：改圆角/尺寸时窗口尺寸不一定变，只有壳变。
        WindowShell.SizeChanged += (_, _) => UpdateShellClip();
        Loaded += (_, _) => ApplyCornerRadius();
        // 贴靠（Aero Snap）不改 WindowState（仍是 Normal），只能按几何判；但**只在几何提交之后**判，
        // 而且拖动/缩放循环里不判（那时矩形会擦过各种贴靠位，跟着判就是角在跳）。
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
        // 分段胶囊导航 = SlidingNavStrip 组件（几何贴齐与滑动动画由组件自持）：
        // 选中项经 TwoWay 绑定同步，本窗口零导航动画代码。
    }

    #region 端口实现（IDialogService / INavigationService / IBrowserLocateHost / IBrowseEditState / IUiNoticeService，供 ViewModel 解耦调用）

    // —— IBrowseEditState（编辑优先闸）——
    bool Services.IBrowseEditState.IsEditing
        => DataContext is MainViewModel vm && vm.BrowserViewModel.IsEditingInProgress;

    string? Services.IBrowseEditState.EditingEntityId
        => DataContext is MainViewModel vm ? vm.BrowserViewModel.EditingEntityId : null;

    bool Services.IBrowseEditState.IsEditingEntity(string id)
        => DataContext is MainViewModel vm && vm.BrowserViewModel.IsEditingEntity(id);

    // —— IUiNoticeService（全站共用的 Toast 栈）——
    // 计时与淡出由 NoticeBar 自持；这里只把文案交给唯一实例。
    void Services.IUiNoticeService.Show(string text) => ShellNotice.Show(text);

    void Services.INavigationService.OpenLinkInBrowser(string linkId) => OpenLinkInBrowserPage(linkId);

    void Services.INavigationService.OpenFolderInBrowser(string folderId)
    {
        if (string.IsNullOrEmpty(folderId) || DataContext is not MainViewModel vm) return;
        vm.SelectNavCommand.Execute(NavIds.Browser);
        _ = vm.BrowserViewModel.LoadAsync(folderId);
    }

    /// <summary>切到搜索页（Ctrl+E / Ctrl+F）：切页后由 MainViewModel.OnNavigatedToSearch → ResetToEmpty
    /// 触发搜索页把焦点收进搜索框，无需本窗口再持有搜索框引用。</summary>
    void Services.INavigationService.NavigateToSearch()
    {
        if (DataContext is MainViewModel vm) vm.SelectNavCommand.Execute(NavIds.Search);
    }

    // —— IBrowserLocateHost（「跳转」= 进入目标目录并选中目标行）——
    // 本窗口只提供两个原语：切页 + 委托浏览页执行"进入目录并选中一行"。
    // 目标类型判别、容器目录推导等算法全部在 Services/ContentLocator（组件），窗口不参与。

    bool Services.IBrowserLocateHost.IsEditingInProgress
        => DataContext is MainViewModel vm && vm.BrowserViewModel.IsEditingInProgress;

    void Services.IBrowserLocateHost.ShowBrowser()
    {
        if (DataContext is MainViewModel vm)
            vm.SelectNavCommand.Execute(NavIds.Browser);
    }

    Task<bool> Services.IBrowserLocateHost.EnterAndSelectAsync(string? folderId, string rowId)
        => DataContext is MainViewModel vm
            ? vm.BrowserViewModel.NavigateAndSelectAsync(folderId, rowId)
            : Task.FromResult(false);

    Task Services.INavigationService.RefreshTrashPageAsync() => TrashView is TrashPage tp ? tp.RefreshAsync() : Task.CompletedTask;

    bool Services.IDialogService.ConfirmDeleteFolder(string folderName)
        => ConfirmDialog.Show(Loc.T("common.title.deleteFolder"), Loc.T("browser.confirm.folderToTrash", folderName), Loc.T("common.delete"));

    // Windows 口径：删除类确认 = 整体移入回收站，不罗列后果；视觉统一走 ConfirmDialog 唯一入口
    bool Services.IDialogService.Confirm(string title, string message, string? confirmText, string iconKind)
        => ConfirmDialog.Show(title, message, confirmText, iconKind);

    // 提示/警告：失败提示属警告类 → 沿用 WarnBg chip（删除/警告一律奶油黄，规范不变）
    void Services.IDialogService.Alert(string title, string message)
        => ConfirmDialog.Show(title, message, Loc.T("common.ok"), "alert-circle-outline");

    #endregion

    private void UpdateCaptionHeight()
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome == null || TitleBar.ActualHeight <= 0) return;

        // CaptionHeight 从**窗口顶端**量，而标题栏在 WindowShell 的外边距之内：
        // 必须算上标题栏顶端的窗口坐标偏移，否则最下面那截（= 外边距高度）永远拖不动。
        try
        {
            var top = TitleBar.TransformToAncestor(this).Transform(new Point(0, 0)).Y;
            var height = top + TitleBar.ActualHeight;
            // 只在真的变了才写：写它会让系统重算非客户区（拖动/缩放期间尤其贵）。
            if (Math.Abs(chrome.CaptionHeight - height) > 0.5) chrome.CaptionHeight = height;
        }
        catch (InvalidOperationException)
        {
            chrome.CaptionHeight = TitleBar.ActualHeight;
        }
    }

    /// <summary>圆角半径：平时 22；最大化或正好填满一个平铺格时 0（四角在屏幕外 / 贴着屏幕边，
    /// 留着只会在屏幕上啃出缺口）。</summary>
    private void ApplyCornerRadius()
    {
        var radius = WindowState == WindowState.Maximized || IsTiled() ? 0 : 22;
        if (Math.Abs(WindowShell.CornerRadius.TopLeft - radius) < 0.1) return;
        WindowShell.CornerRadius = new CornerRadius(radius);
        UpdateShellClip();
    }

    /// <summary>
    /// `CornerRadius` 不裁子内容：给壳补一次圆角裁剪。
    /// ⚠️ 必须挂在**壳自己的** `SizeChanged` 上——只挂窗口的会让裁剪停在旧尺寸上，内容四角不圆。
    /// </summary>
    /// <summary>拖动/缩放的起止 + 几何提交后的重判。见 ctor 里的说明。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmEnterSizeMove)
        {
            _inSizeMove = true;
        }
        else if (msg == WmExitSizeMove)
        {
            _inSizeMove = false;
            ApplyCornerRadius();
        }
        else if (msg == WmWindowPosChanged && !_inSizeMove)
        {
            ApplyCornerRadius();
        }
        return IntPtr.Zero;
    }

    /// <summary>按窗口**实际矩形**判是否正好填满一个平铺格（贴靠态）。</summary>
    private bool IsTiled()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect) && IsTiledRect(rect);
    }

    /// <summary>给定外框是否为平铺格：贴齐工作区边 + 尺寸属于**同一个布局族**。</summary>
    /// <remarks>
    /// 宽高不能各自独立取分数：列类布局（1/3、1/2、2/3、70/30、1/4、3/4）**高度必定整格**，
    /// 行类布局**宽度必定整格**，角格只有「列宽 × 半高」。宽高任意组合都算贴靠的话，
    /// 窗口在贴靠位附近挪动会被反复判成「又贴靠了」（现场：直角/圆角来回闪）。
    /// 贴边与满格一律按**覆盖**判：最大化矩形会被 `WindowChrome` 按缩放边向外膨胀一圈。
    /// </remarks>
    private static bool IsTiledRect(NativeRect wr)
    {
        var monitor = MonitorFromRect(ref wr, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;
        var info = new NativeMonitorInfo { cbSize = Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var work = info.rcWork;
        double w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
        double ww = work.Right - work.Left, wh = work.Bottom - work.Top;
        if (ww <= 0 || wh <= 0) return false;

        const double tol = 4;
        bool Near(double value, double target) => Math.Abs(value - target) <= tol;

        bool ColumnWidth(double value) =>
            Near(value, ww / 4) || Near(value, ww / 3) || Near(value, ww * 0.3)
            || Near(value, ww * 0.4) || Near(value, ww / 2) || Near(value, ww * 0.6)
            || Near(value, ww * 2 / 3) || Near(value, ww * 0.7) || Near(value, ww * 3 / 4);

        bool RowHeight(double value) =>
            Near(value, wh / 4) || Near(value, wh / 3) || Near(value, wh / 2)
            || Near(value, wh * 2 / 3) || Near(value, wh * 3 / 4);

        var left = wr.Left <= work.Left + tol;
        var right = wr.Right >= work.Right - tol;
        var top = wr.Top <= work.Top + tol;
        var bottom = wr.Bottom >= work.Bottom - tol;
        var fullW = w >= ww - tol;
        var fullH = h >= wh - tol;

        if (fullW && fullH) return true;                                 // 满工作区
        if (fullH && (left || right) && ColumnWidth(w)) return true;      // 竖切：列
        if (fullW && (top || bottom) && RowHeight(h)) return true;        // 横切：行
        return (left || right) && (top || bottom) && ColumnWidth(w) && Near(h, wh / 2);   // 角格
    }

    private void UpdateShellClip()
    {
        var radius = WindowShell.CornerRadius.TopLeft;
        var w = WindowShell.ActualWidth;
        var h = WindowShell.ActualHeight;
        WindowShell.Clip = radius <= 0 || w <= 0 || h <= 0
            ? null
            : new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        // 「所属文件夹」名称解析用的扁平文件夹列表（搜索页路径列 / 智能列表位置列同一份）
        await viewModel.LoadFolderTreeAsync();

        // 「浏览」页是默认首页（CurrentNavId 初始即 "browser"，启动时不会走 SelectNav），
        // 因此必须在这里主动装载一次，否则左侧文件夹树与列表在启动时是空的、
        // 要手动点一下「浏览」才会加载。
        await viewModel.BrowserViewModel.LoadAsync(null);
        if (!string.IsNullOrEmpty(StartupFolderId))
        {
            await viewModel.BrowserViewModel.LoadAsync(StartupFolderId);
            LpLog.Info($"startup folder: requested={StartupFolderId} current={viewModel.BrowserViewModel.CurrentFolderId ?? "root"}",
                category: "app.lifecycle");
        }
        // 导航条（SlidingNavStrip 组件）在首次布局后自行贴齐选中项，窗口无需干预。

        // 启动后空闲预热：首帧与浏览页就绪后延迟 2 秒，后台把智能列表四预设首页与
        // 搜索覆盖索引读热；不建 10 万占位行池、不碰 UI 线程。用户已打开搜索/智能列表则跳过。
        _ = RunStartupWarmupAsync(viewModel);

        // 写路径预热要赶在用户第一次新建文件夹之前完成，所以不等 2 秒；事务回滚，不留任何数据。
        _ = WarmWritePathAsync();
    }

    private readonly CancellationTokenSource _startupWarmCts = new();

    private async Task WarmWritePathAsync()
    {
        var ct = _startupWarmCts.Token;
        try
        {
            var watch = Stopwatch.StartNew();
            await Task.Run(() => _host.WarmWritePathAsync(ct), ct);
            LpLog.Info($"startup warm: write path {watch.ElapsedMilliseconds}ms", category: "app.lifecycle");
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭：预热让路。
        }
        catch (Exception ex)
        {
            LpLog.Warn("startup warm: write path failed", ex, category: "app.lifecycle");
        }
    }

    private async Task RunStartupWarmupAsync(MainViewModel vm)
    {
        var ct = _startupWarmCts.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            if (ct.IsCancellationRequested) return;
            if (vm.CurrentNavId is NavIds.Search or NavIds.SmartLists) return;

            LpLog.Info("startup warm: begin (smart lists + search index)", category: "app.lifecycle");
            // Task.Run 保证整段预热不占 UI 线程；WarmForStartupAsync 内部只存页数据，不建行池。
            await Task.Run(() => vm.SmartListViewModel.WarmForStartupAsync(), ct);
            if (ct.IsCancellationRequested) return;
            if (vm.CurrentNavId is NavIds.Search or NavIds.SmartLists) return;

            _searchVm.WarmIndexForStartup();
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭或用户已接管：预热让路。
        }
        catch (Exception ex)
        {
            LpLog.Warn("startup warm failed", ex, category: "app.lifecycle");
        }
    }

    /// <summary>
    /// 搜索结果「跳转/打开/编辑」：切到「浏览」页并打开该链接的详情页
    /// （无需先导航到它所在的文件夹，详情页自带所属路径与打开/编辑/删除操作）。
    /// </summary>
    private void OpenLinkInBrowserPage(string linkId)
    {
        if (string.IsNullOrEmpty(linkId) || DataContext is not MainViewModel vm) return;

        vm.SelectNavCommand.Execute(NavIds.Browser);
        _ = vm.BrowserViewModel.OpenDetailPageByIdAsync(linkId);
    }

    /// <summary>
    /// 页面显隐的唯一投影点：当前 navId 对应的页面 Visible，其余（含未注册项）Collapsed。
    /// </summary>
    /// <remarks>
    /// 旧写法在 XAML 里为六页各写一个 <c>DataTrigger</c>（字面 navId 字符串），于是"有哪些页、叫什么 id"
    /// 存在两份表：注册表一份、XAML 一份 —— 新增页面时漏改任何一处都不会报错，只会"点不动/不显示"。
    /// </remarks>
    private void ApplyNavVisibility(string navId)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var id in _regions.NavIds)
        {
            if (_regions.Resolve(id) is { } page)
                page.Visibility = string.Equals(id, navId, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        }

        // 显隐翻转本身只是赋值 —— "卡"发生在翻转之后：被显示的页面要跑 measure/arrange +
        // 首帧模板实例化（虚拟化只实例化视口那几屏，但那几屏仍要建）。
        // 量法：`Loaded` 优先级的回调排在布局/渲染那一档之后 ⇒ 此刻的墙钟 ≈ 翻转→布局跑完。
        // 纯观测：回调里只写一行 cat=app.nav 的日志，不阻塞、不改时序。
        var shown = _regions.Resolve(navId);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            LpLog.Write(LogLevel.Info, NavLogCategory,
                $"visibility->{navId} shown={shown?.GetType().Name ?? "(none)"}",
                elapsedMs: watch.ElapsedMilliseconds)));
    }

    /// <summary>
    /// 应用内拖拽游标兜底：内层落点已把非法区设成 None 时，恢复成移动/复制。
    /// 只认本应用自己的拖拽载荷（外部文件等仍由系统决定）；拖出窗口后没有本处理器，系统照常显示禁止。
    /// </summary>
    private static void WindowDragCursorFallback(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(LinkPocket.Views.Browser.BrowserView.BrowserDragPayload))) return;
        if (e.Effects != DragDropEffects.None) return;
        e.Effects = (Keyboard.Modifiers & ModifierKeys.Control) != 0
            ? DragDropEffects.Copy
            : DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>切页耗时留痕的分类（与 MainViewModel / BrowserViewModel 同一支：现场日志按它取）。</summary>
    private const string NavLogCategory = "app.nav";

    /// <summary>窗口关闭：释放本窗口的页面对象图作用域（引擎面是应用级的，不在这里释放）。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _startupWarmCts.Cancel();
        if (DataContext is MainViewModel vm)
        {
            vm.SmartListViewModel.CancelStartupWarm();
            _searchVm.CancelStartupWarm();
        }
        _scope.Dispose();
        base.OnClosed(e);
    }

    private const int WmWindowPosChanged = 0x0047;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref NativeMonitorInfo lpmi);
}
