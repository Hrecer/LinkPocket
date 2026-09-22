using System.Windows;
using LinkPocket.Services;
using LinkPocket.Theming;
using LinkPocket.I18n;
using LinkPocket.Contracts;

namespace LinkPocket;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        // fire-and-forget 任务链（如页面刷新）异常无人 await → 默认静默；挂观测钩子留痕（不 SetObserved，不改运行时语义）
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DisableWerDumps();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 外观装配：**唯一入口** ThemeService（读偏好 → 求解 → 库基线 → 全量权威表 → 字体令牌 → 留痕）。
        // 必须在 InitializeComponent（App.xaml 资源合并）之后调用，
        // 否则 M3 角色画刷会被 App.xaml 的 ResourceDictionary 整体覆盖。
        //
        // 历史（为什么要收成一处）：原先这里是「M3Theme.Apply(FromSeed(#6750A4)) + 手打 3 个表面补丁」，
        // 而探针又抄了一份同样的补丁 —— 双份事实源，改主题必漂移；且画刷写死在 UIKit.xaml，
        // 换种子只改库角色、画刷纹丝不动（换主题只会"半主题化"）。
        // 现在：颜色计算全在 LinkPocket.Theming，宿主与探针都只调 ThemeService。
        var (fellBack, reason) = ThemeService.ApplyFromPreferences(Resources);

        // 语言：偏好里存的只是字符串，解释与生效全在 I18n。**必须在建主窗口之前**——
        // 首帧就得是最终语言，先画中文再跳英文等于给用户看一次闪烁。
        LocaleService.Apply(AppLocales.Resolve(ThemeService.LanguageMode, ThemeService.LanguageOverride, out var languageFellBack));

        // 根别名登记（各语言的根显示名 → 契约层）：路径首段匹配在 UIKit、根级占用名校验在 Kernel，
        // 两者都不许引 I18n，所以由组合根把事实登记过去。

        LpIcons.RegisterAll();
        base.OnStartup(e);

        // 偏好损坏 / 字体缺失 / 语言认不出来 → **如实提示一次**（观测面纪律：不许静默回退）
        var notices = new List<string>();
        if (fellBack && reason is not null) notices.Add(reason);
        if (languageFellBack)
            notices.Add(Loc.T("app.err.languageMode", ThemeService.LanguageMode));
        if (notices.Count > 0)
        {
            var text = string.Join("\n\n", notices);
            LpLog.Warn($"startup fallback: {text}", category: ThemeService.LogCategory);
            try
            {
                MessageBox.Show(text + Loc.T("startup.fallbackNote"), "LinkPocket",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* 提示失败不阻断启动；日志已留痕 */ }
        }

        // 组合根装配：主题应用之后创建主窗口（与原 StartupUri 的实例化时机一致）。
        // ⚠️ 注意：本方法的调用**由 Application 的构造函数排进 Dispatcher 队列**，与是否调用 Run() 无关——
        // 任何一次泵消息都会让它跑起来。探针（SmartProbe）不 Run 但照样泵消息，所以在修掉这点之前
        // 它自己建的主窗口之外会**再多出一个**本方法建的完整主窗口，探针于是量到了另一棵可视树
        // （实测：4494 个可见行单元格里只有 1 个在探针自己那棵树里）。
        // 探针侧现在的做法是：先泵一次让本方法跑完，再复用 Application.Current.MainWindow（见 Program.Main）。
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
            LpLog.Error("application startup failed", ex);
            LpLog.Flush(TimeSpan.FromSeconds(2));   // 启动失败即退出：先落盘再弹窗
            MessageBox.Show(Loc.T("startup.failed"), "LinkPocket", MessageBoxButton.OK, MessageBoxImage.Error);
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
        LpLog.Info("application exiting, forcing process termination");
        //（两阶段）：
        // ① 先把 SQLite 连接池全部断开——池化连接持有的 WAL 文件句柄会阻止 checkpoint，
        //    显式清池触发 SQLite 把 WAL 收拢回主库文件（否则强杀后日志/WAL 可能丢尾）；
        // ② 再交 WPF 完成正常关闭序（base.OnExit），最后刷日志 + 兜底强杀确保无残留进程。
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        catch { /* 清池失败不阻断退出 */ }
        base.OnExit(e);
        LpLog.Shutdown();   // 观测面收尾：刷盘 + 卸管道（此后记录被计数丢弃，不再落盘）
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
        LpLog.Error("UI thread unhandled exception", e.Exception);
        LpLog.Flush(TimeSpan.FromSeconds(2));   // 异常现场先落盘（弹窗后界面状态不可信）
        // 不静默吞——异常必须暴露给用户（多数情况界面状态已不可信），
        // 但保留「已提交写不被否定」语义：不崩溃、提示用户自行决策（重启/继续）。
        // 弹窗本身放 try/catch：异常处理路径出错时以日志为准，绝不二次弹窗死循环。
        try
        {
            if (Application.Current.MainWindow is { IsLoaded: true })
                LinkPocket.Views.ConfirmDialog.Show(Loc.T("app.err.uiException"),
                    Loc.T("app.err.unhandled", e.Exception.Message),
                    Loc.T("app.err.startupTitle"), "alert-circle-outline");
        }
        catch { }
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LpLog.Error("AppDomain unhandled exception", ex);
            LpLog.Flush(TimeSpan.FromSeconds(2));   // 进程即将终止：同步刷盘保住现场
        }
    }

    /// <summary>未观察任务异常（fire-and-forget 链）：只留痕，不 SetObserved（不改运行时语义）。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LpLog.Error("unobserved task exception (TaskScheduler)", e.Exception, category: "app.lifecycle");
        LpLog.Flush(TimeSpan.FromSeconds(1));
    }
}