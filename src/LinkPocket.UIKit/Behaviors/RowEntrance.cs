using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LinkPocket.Views;

/// <summary>
/// 行错峰入场（MD3E）：**视口内看得见的行**一起**淡入 + 轻微上移**（弹簧曲线）。
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
    /// <summary>起始下移量（px；回到 0）。</summary>
    private const double OffsetY = 10;

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
            for (var i = 0; i < rowsList.Items.Count; i++)
            {
                if (rowsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row) continue;
                if (!IsInViewport(row, scroller)) continue;
                PlayRow(row, index++);
            }
        }));
    }

    /// <summary>行列表所在的滚动宿主（虚拟化前提：行列表是 ScrollViewer 的直接内容）。</summary>
    private static ScrollViewer? FindScroller(DependencyObject element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer scroller) return scroller;
        return null;
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

    private static void PlayRow(FrameworkElement row, int index)
    {
        var translate = new TranslateTransform(0, OffsetY);
        row.RenderTransform = translate;
        row.Opacity = 0;
        var begin = TimeSpan.FromMilliseconds(Math.Min(index, MaxStaggerSteps) * StaggerMs);
        var offsetAnimation = new DoubleAnimation(OffsetY, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut },
            BeginTime = begin
        };
        var opacityAnimation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = begin };

        // 结束后清除动画层并落终值（FillBehavior.Stop 会回退到本地值 0，行会永远透明）。
        // ⚠️ **归属校验**：行容器会被虚拟化复用——上一次播放的已完成回调若迟到（新一次播放已开始），
        // 直接清动画会把**这一次**的动画拆掉（该行当场跳到全不透明，实测"少数行没有入场动画"）。
        // 判据 = 本行当前的位移变换仍是**我这次**挂上去的那个；不是就说明容器已进入下一轮，什么都不做。
        EventHandler done = (_, _) =>
        {
            if (!ReferenceEquals(row.RenderTransform, translate)) return;   // 迟到的回调：本轮不归我管
            row.BeginAnimation(UIElement.OpacityProperty, null);
            row.Opacity = 1;
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
        };
        opacityAnimation.Completed += done;
        offsetAnimation.Completed += done;

        translate.BeginAnimation(TranslateTransform.YProperty, offsetAnimation);
        row.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
    }
}
