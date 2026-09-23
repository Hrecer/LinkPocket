using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
public partial class MainWindow : Window, Services.IDialogService, Services.INavigationService, Services.IBrowserLocateHost
{
    private readonly Services.AppHost _host;

    private readonly IServiceScope _scope;

    private readonly Services.ViewRegistry _regions = new();

    private readonly SearchViewModel _searchVm;

    public MainWindow(Services.AppHost host)
    {
        _host = host;
        InitializeComponent();
        // 页面对象图按窗口取一份（容器 Scoped）：再开一个窗口拿到的是干净的 VM 图，
        // 不继承上一个窗口的选中/编辑态；窗口关闭时随作用域一起释放。
        _scope = host.Services.CreateScope();
        var vm = _scope.ServiceProvider.GetRequiredService<MainViewModel>();
        DataContext = vm;
        // 端口登记：本窗口实现 IDialogService/INavigationService/IBrowserLocateHost，
        // 组合根持有槽位实例，ViewModel 经构造注入消费——不再经过任何静态注册点。
        _host.Ports.Dialogs = this;
        _host.Ports.Navigation = this;
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
            () => vm.RefreshFolderTreeAndUIAsync());
        SettingsView.Configure(client, vm.ReinitializeDatabaseAsync,
            () => vm.RefreshFolderTreeAndUIAsync(), _host.Ai);
        AiPage.Configure(_host.Ai, navId => vm.SelectNavCommand.Execute(navId));
        vm.ToolsDataChanged += (_, _) => _ = ToolsView.OnExternalDataChangedAsync();
        // 进入工具页：入口对齐（去重结果可能已被其它页面的变更置于陈旧；页内按视图状态决定重跑）
        vm.NavigatedToTools += (_, _) => _ = ToolsView.OnNavigatedToAsync();

        // MainViewModel 的 search 路由事件 → 搜索页 ViewModel（进入保内容+静默刷新 / 离开清选中 / 数据变更重跑）
        vm.NavigatedToSearch += (_, _) => _searchVm.OnNavigatedTo();
        vm.NavigatedFromSearch += (_, _) => _searchVm.OnNavigatedFrom();
        vm.SearchRefreshRequested += (_, _) => _ = _searchVm.RefreshFromEventAsync();

        Loaded += MainWindow_Loaded;
        StateChanged += Window_StateChanged;
        SizeChanged += (_, _) => UpdateShellClip();
        // 分段胶囊导航 = SlidingNavStrip 组件（几何贴齐与滑动动画由组件自持）：
        // 选中项经 TwoWay 绑定同步，本窗口零导航动画代码。
    }

    #region 端口实现（IDialogService / INavigationService / IBrowserLocateHost，供 ViewModel 解耦调用）
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
        // 无边框大圆角壳：最大化时收掉外距与圆角（窗口贴满工作区），
        // 恢复时回到悬浮卡形态（外距 18 = 阴影呼吸空间，外圆角 22）。
        if (WindowState == WindowState.Maximized)
        {
            WindowShell.Margin = new Thickness(8);
            WindowShell.CornerRadius = new CornerRadius(0);
        }
        else
        {
            WindowShell.Margin = new Thickness(18);
            WindowShell.CornerRadius = new CornerRadius(22);
        }
        UpdateShellClip();
    }

    /// <summary>CornerRadius 不会圆角裁切子内容：用 RectangleGeometry 裁出窗口圆角。</summary>
    private void UpdateShellClip()
    {
        var w = WindowShell.ActualWidth;
        var h = WindowShell.ActualHeight;
        if (WindowState == WindowState.Maximized || w <= 0 || h <= 0)
        {
            WindowShell.Clip = null;
            return;
        }
        WindowShell.Clip = new RectangleGeometry(
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
        foreach (var id in _regions.NavIds)
        {
            if (_regions.Resolve(id) is { } page)
                page.Visibility = string.Equals(id, navId, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>窗口关闭：释放本窗口的页面对象图作用域（引擎面是应用级的，不在这里释放）。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _scope.Dispose();
        base.OnClosed(e);
    }
}
