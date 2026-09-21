using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LinkPocket.Views;

/// <summary>
/// 行错峰入场（MD3E）：前 N 行**淡入 + 轻微上移**（弹簧曲线）。
///
/// <para>**只在"用户发起的刷新"后播放**（导航加载口径：打开文件夹 / 切页 / 返回 / F5）；
/// 事件驱动的后台刷新（写操作后的 300ms 防抖、排序、跳转定位到当前目录…）一律静默——
/// 绝不按"集合有没有变更"来播（否则移动/粘贴后的那次刷新也会重播）。</para>
///
/// <para><b>唯一实现</b>：浏览页与回收站原先各写一份逐字相同的 QueueRowEntrance / PlayRowEntrance，
/// 现收口到本类；搜索页 / 智能列表 / 去重明细在 F5 后也调用它（动画代码不得写两遍，必须提取复用）。</para>
/// </summary>
public static class RowEntrance
{
    /// <summary>最多播多少行（再往后视觉上已看不出错峰，白烧）。</summary>
    private const int MaxRows = 12;
    /// <summary>每行递增的起播延迟（ms）。</summary>
    private const int StaggerMs = 30;
    /// <summary>起始下移量（px；回到 0）。</summary>
    private const double OffsetY = 10;

    /// <summary>
    /// 对表格当前**已实例化**的前若干行播入场动画。延到布局完成后执行——调用点通常在"刷新链结束"，
    /// 此时行容器可能还没生成（工厂模式行是布局期建出来的）。
    /// </summary>
    public static void Play(ItemsControl? rowsList)
    {
        if (rowsList == null) return;
        if (!SystemParameters.ClientAreaAnimation) return;   // 辅助功能：减少动态效果
        rowsList.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var index = 0;
            for (var i = 0; i < rowsList.Items.Count && index < MaxRows; i++)
            {
                if (rowsList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement row)
                {
                    PlayRow(row, index);
                    index++;
                }
            }
        }));
    }

    private static void PlayRow(FrameworkElement row, int index)
    {
        var translate = new TranslateTransform(0, OffsetY);
        row.RenderTransform = translate;
        row.Opacity = 0;
        var begin = TimeSpan.FromMilliseconds(Math.Min(index, MaxRows) * StaggerMs);
        var offsetAnimation = new DoubleAnimation(OffsetY, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut },
            BeginTime = begin
        };
        var opacityAnimation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = begin };

        // 结束后清除动画层并落终值（FillBehavior.Stop 会回退到本地值 0，行会永远透明）
        EventHandler done = (_, _) =>
        {
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
