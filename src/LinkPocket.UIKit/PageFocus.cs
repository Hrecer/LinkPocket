using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LinkPocket.Views;

/// <summary>
/// 页面焦点不变式（**唯一实现**）：页面可见且应用在前台时，键盘焦点必须在**页内**——
/// 页面级快捷键挂在页面根、按焦点路由（见 <see cref="Input.ShortcutHost"/>），
/// 焦点一旦掉出页面，整页快捷键会静默失效（例如进到文件夹后必须点空白才能粘贴）。
///
/// <para>刷新会重建树/面包屑/列表，**被聚焦的容器（树节点、面包屑按钮、明细面板）随重建被移出可视树**，
/// WPF 会把焦点交给窗口（页外，还可能顺带画出原生焦点虚线框）——因此每条刷新链结束、每次"点空白"之后，
/// 都由页面把焦点收回页根。</para>
///
/// <para>浏览页与回收站原先各写一份逐字相同的 EnsurePageFocus / IsFocusWithinPage，现收口到本类；
/// 搜索页 / 智能列表 / 去重明细同样复用（不得写两遍）。</para>
/// </summary>
public static class PageFocus
{
    /// <summary>
    /// 主动把键盘焦点收进页根（页面被切到前台 / 点击某栏 / 返回卡片页时用）：
    /// 只在页已加载且可见时才抢；**不检查"焦点是否已在页内"**——这是"主动取焦点"的语义，
    /// 与 <see cref="Restore"/>（刷新收尾的守门）区分开。
    /// </summary>
    public static void Take(UIElement page)
    {
        if (page is FrameworkElement fe && (!fe.IsLoaded || !fe.IsVisible)) return;
        Keyboard.Focus(page);
    }

    /// <summary>当前键盘焦点是否落在 <paramref name="page"/> 之内（含其后代）。</summary>
    public static bool IsWithin(UIElement page)
    {
        var current = Keyboard.FocusedElement as DependencyObject;
        while (current != null)
        {
            if (ReferenceEquals(current, page)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <summary>
    /// 把键盘焦点收回页根。三条不抢（与浏览页/回收站既有口径一致）：
    /// 页不可见 / 应用不在前台 / 正在编辑文本（<see cref="Input.ShortcutHost.IsTextInputFocused"/>）；
    /// 焦点已在页内时也不动（避免打断用户正在行的栏内导航）。
    /// ⚠️ <paramref name="page"/> 必须 <c>Focusable="True" + FocusVisualStyle="{x:Null}"</c>（页面根样式约定）。
    /// </summary>
    public static void Restore(UIElement page)
    {
        if (!page.IsVisible) return;
        if (page is FrameworkElement fe && (!fe.IsLoaded || Window.GetWindow(fe)?.IsActive != true)) return;
        if (IsWithin(page)) return;
        if (Input.ShortcutHost.IsTextInputFocused()) return;   // 编辑中（地址栏 / 输入框）绝不抢
        Keyboard.Focus(page);
    }
}
