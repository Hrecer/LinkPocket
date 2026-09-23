using LinkPocket.Services;
using LinkPocket.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LinkPocket;

/// <summary>
/// 前端对象图（唯一装配登记点）：Shell、页面 ViewModel、共享件与端口全部由容器解析。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么现在才有容器</b>：此前装配散在 <see cref="MainWindow"/> 的构造函数里（逐个 <c>new</c> +
/// <c>Configure(...)</c> 注入），新增一个页面依赖就要在窗口里加一行、并按顺序摆放；容器把"谁依赖谁"
/// 变成一份可读的登记表，解析顺序由容器保证。
/// </para>
/// <para>
/// <b>边界</b>：容器只被 Shell/组合根引用（契约层与引擎层不认识它）。引擎侧的装配仍在共享
/// <c>Composition.EngineComposer</c>（唯一 <c>new</c> 具体实现的地方），这里只登记它的产物。
/// </para>
/// <para>
/// <b>生命周期：页面对象图是 Scoped（每个窗口一张），引擎面是 Singleton</b>。
/// 引擎（客户端 / 事件枢纽 / 端口 / 定位组件）全应用唯一；而 ViewModel 图**属于窗口**——
/// 一个窗口一份状态（选中集合、导航、编辑态）。这样"再开一个窗口"（测试宿主会关掉再开）
/// 拿到的是干净的一份，而不是继承上一个窗口的残留状态；生产只有一个窗口，行为与"应用单例"相同。
/// </para>
/// <para>
/// <b>MainViewModel 的内部件</b>：浏览/回收站/智能列表/设置四个页面 VM 目前仍由
/// <see cref="MainViewModel"/> 在自己的构造函数里创建（它们是 Shell VM 的组成部分），
/// 容器以"转发登记"的方式把它们暴露出去（<c>sp =&gt; sp.GetRequiredService&lt;MainViewModel&gt;().X</c>），
/// 于是消费方一律经容器拿，实例归属不变。
/// </para>
/// </remarks>
internal static class AppServiceGraph
{
    /// <summary>登记整张前端对象图（引擎面 → 共享件 → Shell → 页面 VM）。</summary>
    public static ServiceProvider Build(AppHost host)
    {
        var services = new ServiceCollection();

        // —— 引擎面（组合根产物；AppHost 是它们的持有者；全应用唯一）——
        services.AddSingleton(host);
        services.AddSingleton(host.Client);
        services.AddSingleton(host.Wire);
        services.AddSingleton(host.Hub);
        services.AddSingleton(host.Ports);
        services.AddSingleton(host.Locator);
        services.AddSingleton<UiEventHub>(_ => host.Hub);
        services.AddSingleton<UiPortProvider>(_ => host.Ports);
        services.AddSingleton<IContentLocator>(_ => host.Locator);

        // —— 窗口与页面对象图（每个窗口一张：见类注释的"生命周期"一节）——
        services.AddScoped(sp =>
        {
            var h = sp.GetRequiredService<AppHost>();
            return new MainViewModel(h.Client, h.Hub, h.Ports, h.Locator);
        });

        // 页面 VM：解析顺序 = Shell VM 先就位（下面几条都从它取实例或委托）
        services.AddScoped(sp => sp.GetRequiredService<MainViewModel>().BrowserViewModel);
        services.AddScoped(sp => sp.GetRequiredService<MainViewModel>().TrashViewModel);
        services.AddScoped(sp => sp.GetRequiredService<MainViewModel>().SmartListViewModel);
        services.AddScoped(sp => sp.GetRequiredService<MainViewModel>().SettingsViewModel);

        services.AddScoped(sp =>
        {
            var h = sp.GetRequiredService<AppHost>();
            var shell = sp.GetRequiredService<MainViewModel>();
            // 端口在 Shell 构造期登记（MainWindow 实现 IDialogService/INavigationService），
            // 本 VM 只在窗口构造中被解析 —— 那时端口必然已就位。
            return new SearchViewModel(h.Client, h.Ports.Navigation!, h.Ports.Dialogs!,
                folderId => shell.FolderPathValue(folderId), h.Locator);
        });

        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }
}
