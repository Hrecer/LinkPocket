using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LinkPocket.Views;

/// <summary>
/// 行错峰入场（MD3E）：**视口内看得见的行**一起**淡入**（错峰）。
///
/// <para>
/// ⚠️ <b>不再做"轻微上移"</b>（2026-09-26，用户实测报"每打开一个文件夹，隔差不多半秒整个界面会往上飘一点，
/// 上面先空出一块再填平"）：上移用 <c>RenderTransform</c> 把行内容先画低 <c>OffsetY</c> 像素 ——
/// 行内容一低，它**上方就露出一道缝**（= 用户说的"空出一块"），随后再"飘"回位（= "填平"）；
/// 错峰的尾段 12×30ms + 260ms ≈ 0.6s，正是"隔半秒"的来源。UiBench `--only shift` 实测：
/// 首行内容 Y 从 214 回到 204（**正好 10px**）而面板几何不动 —— 位移全部来自这支动画。
/// 淡入（<c>Opacity</c>）不动布局、也不制造缝隙，因此保留。
/// </para>
///
/// <para>**只在"用户发起的刷新"后播放**（导航加载口径：打开文件夹 / 切页 / 返回 / F5）；
/// 事件驱动的后台刷新（写操作后的 300ms 防抖、排序、跳转定位到当前目录…）一律静默——
/// 绝不按"集合有没有变更"来播（否则移动/粘贴后的那次刷新也会重播）。</para>
///
/// <para><b>覆盖范围 = 视口内全部行</b>：行列表是 UI 虚拟化的（只实例化可视区 ± 缓存页），
/// 而"播多少行"过去写死为前 <c>12</c> 行 —— 视口里第 13 行起（以及缓存页里的行）**永远不播**，
/// 一屏超过 12 行时表现为"后面几行直接跳出来"。现行判据按**行在视口里**来取
/// （虚拟化实例化 ≠ 看得见：缓存页里的容器也在可视树里），错峰只对**前若干档**递增、其余一起播
/// （再往后视觉上已看不出错峰，继续递增只是把整段拖长）。</para>
///
/// <para><b>唯一实现</b>：浏览页与回收站原先各写一份逐字相同的 QueueRowEntrance / PlayRowEntrance，
/// 现收口到本类；搜索页 / 智能列表 / 去重明细在 F5 后也调用它（动画代码不得写两遍，必须提取复用）。</para>
/// </summary>
public static class RowEntrance
{
    /// <summary>错峰的**档数**上限（第 12 行之后一起播，不再逐行递增）。</summary>
    private const int MaxStaggerSteps = 12;
    /// <summary>每行递增的起播延迟（ms）。</summary>
    private const int StaggerMs = 30;

    /// <summary>
    /// 对表格当前**视口内**的全部行播入场动画。延到布局完成后执行——调用点通常在"刷新链结束"，
    /// 此时行容器可能还没生成（工厂模式行是布局期建出来的），拿不到视口的实测高度。
    /// </summary>
    public static void Play(ItemsControl? rowsList)
    {
        if (rowsList == null) return;
        if (!SystemParameters.ClientAreaAnimation) return;   // 辅助功能：减少动态效果
        rowsList.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var scroller = FindScroller(rowsList);
            var index = 0;
            // 遍历**已实现的行容器**（= 面板的孩子）。不用 `for (i < Items.Count) ContainerFromIndex(i)`：
            // 那是按数据项逐个问生成器，10 001 行的表上就是 10 001 次空转（其中绝大多数根本没有容器）。
            var rows = new List<FrameworkElement>();
            var board = new Storyboard();
            foreach (var row in EnumerateRealizedRows(rowsList))
            {
                if (!IsInViewport(row, scroller)) continue;
                AddRow(board, rows, row, index++);
            }
            if (board.Children.Count == 0) return;

            var generation = ++_generation;
            // 结束回调 = **整批一次**（原先是每行两个 Completed 钩子 × 2 属性 = 视口内 ~130 个回调登记）。
            // ⚠️ **归属校验**（同原逐行版语义）：行容器会被虚拟化复用，上一次播放的迟到回调若在新一次播放
            // 开始后才到，直接清动画会把**这一次**的动画拆掉（该行当场跳到全不透明，实测"少数行没有入场动画"）。
            // 判据 = 本批是否仍是"最新那一批"；不是就说明容器已进入下一轮，什么都不做。
            board.Completed += (_, _) =>
            {
                if (generation != _generation) return;   // 迟到的回调：本轮不归我管
                foreach (var row in rows)
                {
                    row.BeginAnimation(UIElement.OpacityProperty, null);
                    row.Opacity = 1;
                }
            };
            board.Begin();
        }));
    }

    /// <summary>本批播放的序号（迟到回调的归属判据，见 <see cref="Play"/> 内的说明）。</summary>
    private static int _generation;

    /// <summary>
    /// 把一行的淡入**挂到同一个 Storyboard** 上（不逐行 <c>BeginAnimation</c>）。
    /// 为什么：逐行启动时，每个 <c>BeginAnimation</c> 都要走一次属性系统 + 建时钟，
    /// 实测视口 33 行 ≈ **46ms** 的同步阻塞（UiBench：导航后 +240~380ms 处那一块）；
    /// 合并成一次 <c>Storyboard.Begin()</c> 后内容与时长逐项不变（视觉无差），启动只付一次。
    /// 只动 <c>Opacity</c>：它不参与布局、也不会像位移那样在行上方露缝（见类注释）。
    /// </summary>
    private static void AddRow(Storyboard board, List<FrameworkElement> rows, FrameworkElement row, int index)
    {
        row.Opacity = 0;
        rows.Add(row);

        var begin = TimeSpan.FromMilliseconds(Math.Min(index, MaxStaggerSteps) * StaggerMs);
        var opacityAnimation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = begin };
        Storyboard.SetTarget(opacityAnimation, row);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath(UIElement.OpacityProperty));
        board.Children.Add(opacityAnimation);
    }

    /// <summary>已实现的行容器序列（= ItemsHost 面板的孩子；虚拟化下天然只覆盖可视区 ± 缓存页）。</summary>
    private static IEnumerable<FrameworkElement> EnumerateRealizedRows(ItemsControl rowsList)
    {
        var panel = FindVisualChild<VirtualizingStackPanel>(rowsList);
        if (panel is null) yield break;
        foreach (UIElement child in panel.Children)
            if (child is FrameworkElement element) yield return element;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindVisualChild<T>(child) is { } deep) return deep;
        }
        return null;
    }

    /// <summary>
    /// 行列表所在的滚动宿主。
    /// 先向上找（行列表被放在外部 ScrollViewer 里的结构），找不到再向下找——
    /// 列表虚拟化生效的前提是 **ScrollViewer 由行列表的控件模板生成**（它是行列表的**子孙**，
    /// 不再是祖先），只向上找会一律返回 null，入场动画于是退化成"缓存页里的行也一起播"。
    /// </summary>
    private static ScrollViewer? FindScroller(DependencyObject element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer scroller) return scroller;
        return FindVisualChild<ScrollViewer>(element);
    }

    /// <summary>
    /// 容器是否**落在视口里**（虚拟化会把视口外 ±1 缓存页的容器也实例化，它们不该参与入场）。
    /// </summary>
    /// <remarks>
    /// 判据 = 相对滚动宿主的实测矩形与 <c>ViewportHeight</c> 是否相交（<c>TransformToAncestor</c> 已经
    /// 含滚动偏移，得到的就是视口坐标系）；拿不到滚动宿主或视口高度时回落到 <c>IsVisible</c>
    /// （宁可多播几行，也不要整个动画不生效）。
    /// </remarks>
    private static bool IsInViewport(FrameworkElement row, ScrollViewer? scroller)
    {
        if (scroller is null || double.IsNaN(scroller.ViewportHeight) || scroller.ViewportHeight <= 0)
            return row.IsVisible;
        try
        {
            var top = row.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y;
            return top + row.ActualHeight > 0 && top < scroller.ViewportHeight;
        }
        catch (InvalidOperationException)
        {
            return row.IsVisible;   // 尚未接入可视树：按可见性兜底
        }
    }

}
