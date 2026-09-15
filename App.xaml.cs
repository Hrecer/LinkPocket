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