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

        // 组合根装配（阶段 7）：主题应用之后创建主窗口（与原 StartupUri 的实例化时机一致）。
        var host = Services.AppHost.CreateDefault();
        var window = new MainWindow(host);
        MainWindow = window; // ShutdownMode=OnMainWindowClose 依赖此引用
        window.Show();
    }

    /// <summary>
    /// 退出兜底：默认 OnLastWindowClose 下，若存在被异常吞掉后残留的隐藏窗口
    /// 或后端线程未结束，进程会残留在后台。改为「主窗口关闭即退出」，
    /// 并在 Exit 时强制终止整个进程，确保关闭窗口 = 进程结束。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("应用退出，强制结束进程");
        base.OnExit(e);
        Environment.Exit(0);
    }

    private void DisableWerDumps()
    {
        try
        {
            Environment.SetEnvironmentVariable("WER_DISABLE_DIALOGS", "1");
        }
        catch { }
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("UI线程未处理异常", e.Exception);
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Logger.Error("AppDomain未处理异常", ex);
    }
}