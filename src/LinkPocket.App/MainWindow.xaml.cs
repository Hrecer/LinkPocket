using System;
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

    /// <summary>贴靠/最大化时壳体贴满窗口（无外边距、无圆角）；普通浮动窗才保留悬浮壳。</summary>
    private bool _flushShell;
    private bool _frameModeApplied;

    /// <summary>用户正在拖边缘/拖标题栏（WM_ENTERSIZEMOVE 与 WM_EXITSIZEMOVE 之间）。
    /// 互动缩放期间只承认"至少一维满格"的平铺，理由见 <see cref="IsTiledRect"/>。</summary>
    private bool _inSizeMove;

    private HwndSource? _hwndSource;

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
                ApplyNavVisibility(active.CurrentNavId);
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
        StateChanged += Window_StateChanged;
        SizeChanged += (_, _) => UpdateShellClip();
        // 贴靠/标题栏拖动都走 Win32 窗口消息：在几何提交前同步切换壳形态，避免先闪一帧旧状态。
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
        };
        Loaded += (_, _) => UpdateFrameMode();
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
            chrome.CaptionHeight = top + TitleBar.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            chrome.CaptionHeight = TitleBar.ActualHeight;
        }
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

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // 最大化/还原/最小化都会改变窗口区域；贴靠态由几何判定（WindowState 仍是 Normal）。
        UpdateFrameMode();
    }

    /// <summary>普通浮动窗保留 18px 阴影外边距 + 22 圆角；贴靠/最大化时壳体贴满窗口，无外距、无圆角。</summary>
    private void UpdateFrameMode()
        => ApplyFrameMode(WindowState == WindowState.Maximized || IsSnappedToWorkArea());

    /// <summary>切换壳形态。只在形态真的变化时改属性，避免拖动/缩放期间反复触发整窗布局。</summary>
    private void ApplyFrameMode(bool flush)
    {
        if (_frameModeApplied && flush == _flushShell) return;
        _frameModeApplied = true;
        _flushShell = flush;

        WindowShell.Margin = flush ? new Thickness(0) : new Thickness(ShellMargin);
        WindowShell.CornerRadius = flush ? new CornerRadius(0) : new CornerRadius(22);
        // 缩放热区从**窗口外缘**往里量：浮动窗有 ShellMargin 的透明外边距挡在可见边框外面，
        // 贴靠/最大化没有那层外距 ⇒ 热区必须自己瘦回去，否则"看得见的内容"往下 26px 都还算缩放边
        // （鼠标停在标题栏上就被判成 HTTOP，出现上下箭头）。
        ApplyResizeBorder(flush ? ResizeGrip : ShellMargin + ResizeGrip);
        UpdateShellClip();

        // 外距变了 ⇒ 标题栏的窗口坐标偏移变了；等布局落地后重算 caption 高度。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateCaptionHeight));
    }

    private void ApplyResizeBorder(double thickness)
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome == null) return;
        var value = new Thickness(thickness);
        if (chrome.ResizeBorderThickness != value) chrome.ResizeBorderThickness = value;
    }

    /// <summary>
    /// 在窗口尺寸/位置**提交之前**按提议矩形判定贴靠：WM_WINDOWPOSCHANGING 里同步切换壳形态，
    /// 使 Windows 提交新几何后的第一帧就是无圆角/无外边距，不再先闪一帧旧状态。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 互动缩放/拖动的起止：起止本身不判形态，但"是否允许认角格"要按它来（见 IsTiledRect），
        // 且结束时必须重判一次——拖到边缘贴靠时，系统是在松手那一刻才把最终矩形提交上来的。
        if (msg == WmEnterSizeMove)
        {
            _inSizeMove = true;
        }
        else if (msg == WmExitSizeMove)
        {
            _inSizeMove = false;
            UpdateFrameMode();
        }

        if (WindowState == WindowState.Minimized) return IntPtr.Zero;

        if (msg == WmWindowPosChanging && lParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<NativeWindowPos>(lParam);
            if ((pos.flags & (SwpNoSize | SwpNoMove)) == 0 && pos.cx > 0 && pos.cy > 0)
            {
                var proposed = new NativeRect
                {
                    Left = pos.x,
                    Top = pos.y,
                    Right = pos.x + pos.cx,
                    Bottom = pos.y + pos.cy,
                };
                ApplyFrameMode(WindowState == WindowState.Maximized || IsTiledRect(proposed, !_inSizeMove));
            }
        }
        else if (msg == WmWindowPosChanged)
        {
            // 兜底：提议矩形被系统调整（最大化/跨屏 DPI）后，以实际矩形收敛。
            UpdateFrameMode();
        }
        return IntPtr.Zero;
    }

    /// <summary>贴靠态判定：窗口外框贴齐当前显示器工作区，且宽/高是常见平铺分数。</summary>
    private bool IsSnappedToWorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var wr)) return false;
        return IsTiledRect(wr, !_inSizeMove);
    }

    /// <summary>
    /// 给定外框是否为一个平铺格：贴齐工作区边缘 + 尺寸是某个**布局**里的分格。
    /// </summary>
    /// <remarks>
    /// ⚠️ 宽高不能各自独立取分数。Windows 的列类布局（1/3、1/2、2/3、70/30、1/4、3/4）**高度必定整格**，
    /// 行类布局**宽度必定整格**；只有"分区+横切"的组合布局才会出现宽高都非满格的格子。
    /// 旧写法让宽高任意组合都算贴靠，于是从左半屏往下拖上边缘时，高度依次扫过 3/4、0.7、2/3、0.6……
    /// 每扫过一个分数就判成"又贴靠了" → 壳形态在直角/圆角之间来回闪几次（用户实测：
    /// 一开头是圆角，往下拖到某个位置突然变方形，再往下又变回圆角）。
    /// </remarks>
    /// <param name="allowCornerTiles">是否承认"宽高都非满格"的角格（2×2 四分格、三分区右上角那类）。
    /// **互动缩放期间必须为 false**：这类格子和"用户把窗口拖到恰好半分"在几何上无法区分，
    /// 开着就会在拖到恰好半分的那一小段里闪回直角。</param>
    private static bool IsTiledRect(NativeRect wr, bool allowCornerTiles)
    {
        var monitor = MonitorFromRect(ref wr, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;
        var info = new NativeMonitorInfo { cbSize = Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var work = info.rcWork;
        double w = wr.Right - wr.Left;
        double h = wr.Bottom - wr.Top;
        double ww = work.Right - work.Left;
        double wh = work.Bottom - work.Top;
        if (ww <= 0 || wh <= 0) return false;

        const double tol = 4;
        bool Near(double value, double target) => Math.Abs(value - target) <= tol;

        // 列宽：Windows 11 的单列 / 双列 / 三列 / 70-30 等布局实际会出现的列分数
        bool ColumnWidth(double value) =>
            Near(value, ww / 4) || Near(value, ww / 3) || Near(value, ww * 0.3)
            || Near(value, ww * 0.4) || Near(value, ww / 2) || Near(value, ww * 0.6)
            || Near(value, ww * 2 / 3) || Near(value, ww * 0.7) || Near(value, ww * 3 / 4);

        // 行高：上下切分的布局只有这几档（0.3/0.4/0.7 之类是列宽专用，不能拿来比高度）
        bool RowHeight(double value) =>
            Near(value, wh / 4) || Near(value, wh / 3) || Near(value, wh / 2)
            || Near(value, wh * 2 / 3) || Near(value, wh * 3 / 4);

        var left = Near(wr.Left, work.Left);
        var right = Near(wr.Right, work.Right);
        var top = Near(wr.Top, work.Top);
        var bottom = Near(wr.Bottom, work.Bottom);
        var fullW = Near(w, ww);
        var fullH = Near(h, wh);

        if (fullW && fullH) return true;                                  // 满工作区
        if (fullH && (left || right) && ColumnWidth(w)) return true;       // 竖切：列
        if (fullW && (top || bottom) && RowHeight(h)) return true;         // 横切：行
        // 角格只有两种真实布局：2×2 四分格、以及"列 + 该列上下各半"的三分区 ⇒ 高度必须是半格。
        // 放开成任意行高会让"左半屏 + 3/4 高"这类用户随手拖出来的尺寸也算贴靠。
        return allowCornerTiles && (left || right) && (top || bottom)
            && ColumnWidth(w) && Near(h, wh / 2);
    }

    /// <summary>CornerRadius 不会圆角裁切子内容：用 RectangleGeometry 裁出窗口圆角。</summary>
    private void UpdateShellClip()
    {
        var w = WindowShell.ActualWidth;
        var h = WindowShell.ActualHeight;
        if (_flushShell || w <= 0 || h <= 0)
        {
            WindowShell.Clip = null;
            WindowContent.Clip = null;
            return;
        }
        WindowShell.Clip = new RectangleGeometry(
            new Rect(0, 0, w, h), 22, 22);
        // 内容宿主必须与背景壳同几何、同裁剪：内容已经不在投影子树里，
        // 圆角裁切要在这一层自己补回来，否则内容会画出圆角。
        WindowContent.Clip = new RectangleGeometry(
            new Rect(0, 0, w, h), 22, 22);
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
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _scope.Dispose();
        base.OnClosed(e);
    }

    private const int WmWindowPosChanging = 0x0046;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>浮动窗的阴影外边距（贴靠/最大化归零）——与 XAML 里 WindowShell.Margin 同源。</summary>
    private const double ShellMargin = 18;

    /// <summary>**看得见的边框**上的缩放热区厚度（从可见边框往里量）。</summary>
    private const double ResizeGrip = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPos
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

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
