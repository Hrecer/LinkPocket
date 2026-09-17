using System.Collections.Generic;
using System.Windows;

namespace LinkPocket.Services;

/// <summary>
/// 区域视图注册表（阶段 10 视图注册/路由装配）：navId → 页面实例的唯一登记点。
/// Shell（MainWindow）在构造时注册六页并完成各自的依赖注入（DataContext / Configure / 事件转发）；
/// 页面的显隐仍由 CurrentNavId 的 XAML 数据触发器驱动（视觉结构冻结，行为与阶段 9 逐字一致）。
/// 新增页面 = 注册一条 + 注入依赖，路由与事件刷新自动生效；未来 Headless/Web 宿主可复用同一装配契约。
/// </summary>
public sealed class ViewRegistry
{
    private readonly Dictionary<string, FrameworkElement> _views = new();

    /// <summary>登记一个区域页面（同一 navId 重复注册 = 覆盖）。</summary>
    public void Register(string navId, FrameworkElement view) => _views[navId] = view;

    /// <summary>按 navId 取页面实例（未注册返回 null）。</summary>
    public FrameworkElement? Resolve(string navId)
        => _views.TryGetValue(navId, out var view) ? view : null;

    /// <summary>已注册的 navId 集合（诊断/测试用）。</summary>
    public IEnumerable<string> NavIds => _views.Keys;
}
