using System.Windows;
using LinkPocket.Services;
using Material3.Wpf;

namespace LinkPocket;

public partial class App : Application
{
    public App()
    {
        Services.AppServices.Initialize();
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
        base.OnStartup(e);
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