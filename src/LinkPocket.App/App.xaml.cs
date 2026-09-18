using System.Windows;
using LinkPocket.Services;
using Material3.Wpf;

namespace LinkPocket;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DisableWerDumps();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 必须在 InitializeComponent（App.xaml 资源合并）之后调用，
        // 否则 M3 角色画刷会被 App.xaml 的 ResourceDictionary 整体覆盖。
        M3Theme.Apply(
            Material3.Core.MaterialTheme.FromSeed(Material3.Core.Argb.FromArgb(0x67, 0x50, 0xA4)),
            isDark: false,
            Resources);
        LpIcons.RegisterAll();
        // 用户定稿（2026-09-16）：全局界面基面 = 禁用态删除按钮的浅紫。
        // 设定值按显示器校色偏移反推（#EAE4ED 上屏 ≈ 按钮的屏显 #EDE2F4）。
        // 必须在 M3Theme.Apply 之后覆盖（Apply 会写入整套生成调色板，晚于此处会被冲掉）。
        Resources["SurfaceContainerLow"] = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xEA, 0xE4, 0xED));
        // 用户定稿（2026-09-16 第二轮）：卡面/胶囊底去灰 —— 原生成色偏暖灰（屏显 #EEE6EC），
        // 在浅紫基面上显"灰蒙蒙"。卡面改近白浅紫（浮起），悬停/胶囊底改明确的深一档紫灰（反馈清晰）。
        Resources["SurfaceContainerHigh"] = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xF6, 0xF1, 0xF8));
        Resources["SurfaceContainerHighest"] = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE3, 0xD9, 0xEB));
        base.OnStartup(e);

        // 组合根装配：主题应用之后创建主窗口（与原 StartupUri 的实例化时机一致）。
        // 装配失败（典型 = 旧格式库被 schema 红线拒绝 / 库文件损坏）必须对用户可见——
        // 启动期尚无窗口，用原生 MessageBox 一次性暴露原因后退出（红线特例：启动失败必须暴露）。
        try
        {
            var host = Services.AppHost.CreateDefault();
            var window = new MainWindow(host);
            MainWindow = window; // ShutdownMode=OnMainWindowClose 依赖此引用
            window.Show();
        }
        catch (Exception ex)
        {
            Logger.Error("应用启动失败", ex);
            MessageBox.Show($"应用启动失败：{ex.Message}", "LinkPocket", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// 退出兜底：默认 OnLastWindowClose 下，若存在被异常吞掉后残留的隐藏窗口
    /// 或后端线程未结束，进程会残留在后台。改为「主窗口关闭即退出」，
    /// 并在 Exit 时强制终止整个进程，确保关闭窗口 = 进程结束。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("应用退出，强制结束进程");
        //（两阶段）：
        // ① 先把 SQLite 连接池全部断开——池化连接持有的 WAL 文件句柄会阻止 checkpoint，
        //    显式清池触发 SQLite 把 WAL 收拢回主库文件（否则强杀后日志/WAL 可能丢尾）；
        // ② 再交 WPF 完成正常关闭序（base.OnExit），最后兜底强杀确保无残留进程。
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        catch { /* 清池失败不阻断退出 */ }
        base.OnExit(e);
        Environment.Exit(0);
    }

    private void DisableWerDumps()
    {
        // 仅关闭 Windows Error Reporting 的弹窗（不是 .NET 的 dump 收集）——按名注释，别误解为禁用 dump
        try
        {
            Environment.SetEnvironmentVariable("WER_DISABLE_DIALOGS", "1");
        }
        catch { }
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("UI线程未处理异常", e.Exception);
        // 不静默吞——异常必须暴露给用户（多数情况界面状态已不可信），
        // 但保留「已提交写不被否定」语义：不崩溃、提示用户自行决策（重启/继续）。
        // 弹窗本身放 try/catch：异常处理路径出错时以日志为准，绝不二次弹窗死循环。
        try
        {
            if (Application.Current.MainWindow is { IsLoaded: true })
                LinkPocket.Views.ConfirmDialog.Show("界面异常",
                    $"发生未处理异常：{e.Exception.Message}\n\n应用可能处于不一致状态，建议重启。",
                    "知道了", "alert-circle-outline");
        }
        catch { }
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Logger.Error("AppDomain未处理异常", ex);
    }
}