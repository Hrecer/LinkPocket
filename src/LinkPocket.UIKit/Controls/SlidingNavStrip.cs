using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LinkPocket.Views;

/// <summary>
/// 顶部导航药丸条（可复用组件，全站唯一实现）：ListBox 基座 + **滑动指示器**。
///
/// <para>选中高亮不画在项上，而是一枚垫在项下的 `PrimaryContainer` 药丸
/// （模板部件 `PART_Indicator` / `PART_IndicatorTf`），选择切换时 300ms BackEase 弹簧
/// 滑到新选中项的**实测几何**（TransformToVisual，天然适配内距/缩放）。与左栏 `SideNavList`
/// 同一模式——几何贴齐与滑动动画全部由组件内部负责，宿主（Shell）只做 XAML 装配。</para>
///
/// <para>规范：项容器样式 `SlidingNavStripItemStyle`、控件样式 `SlidingNavStripStyle`（UIKit.xaml）。
/// 首帧 / 几何变化（窗口缩放、最大化）无动画贴齐；仅用户切换选择播放滑动动画；
/// 「客户端动画已禁用」时退化为直接贴齐（清钟再写值——HoldEnd 动画钟会压过本地赋值，见 WARNINGS）。</para>
/// </summary>
public class SlidingNavStrip : ListBox
{
    private Border? _indicator;
    private TranslateTransform? _indicatorTf;
    private Point? _lastPos;
    private Size _lastSize;
    private bool _pendingAnimate;

    public SlidingNavStrip()
    {
        // 水平排布由组件自持（非虚拟化 StackPanel：项少且指示器要按容器实测几何贴齐）
        var panel = (ItemsPanelTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<StackPanel Orientation=\"Horizontal\"/></ItemsPanelTemplate>");
        ItemsPanel = panel;

        SelectionChanged += (_, _) => { _pendingAnimate = true; UpdateIndicator(); };
        Loaded += (_, _) => UpdateIndicator();
        LayoutUpdated += (_, _) => UpdateIndicator();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _indicator = GetTemplateChild("PART_Indicator") as Border;
        _indicatorTf = GetTemplateChild("PART_IndicatorTf") as TranslateTransform;
    }

    /// <summary>把指示器贴齐当前选中项：动画与否由状态决定（首帧贴齐 / 选择切换时滑动）。</summary>
    private void UpdateIndicator()
    {
        if (_indicator == null || _indicatorTf == null) return;

        // 无选中：指示器隐藏
        if (SelectedItem == null || SelectedIndex < 0)
        {
            _indicator.Visibility = Visibility.Collapsed;
            _lastPos = null;
            return;
        }

        if (ItemContainerGenerator.ContainerFromItem(SelectedItem) is not ListBoxItem container)
            return;
        if (container.ActualWidth <= 0)
            return; // 容器尚未完成布局，等下一轮 LayoutUpdated 再贴

        // 坐标基准 = 指示器的父容器（与项同在模板内同一坐标空间；TransformToVisual(this) 会被外层内距偏移）
        if (VisualTreeHelper.GetParent(_indicator) is not UIElement host) return;
        var pos = container.TransformToVisual(host).Transform(new Point(0, 0));
        var size = new Size(container.ActualWidth, container.ActualHeight);

        var firstShow = _indicator.Visibility != Visibility.Visible;
        var moved = _lastPos != pos || _lastSize != size;
        if (!moved && !firstShow) return; // 几何没变：不打扰进行中的动画

        var animate = _pendingAnimate && !firstShow && SystemParameters.ClientAreaAnimation;
        if (animate)
        {
            var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(300);
            _indicatorTf.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation { To = pos.X, Duration = dur, EasingFunction = ease });
            _indicator.BeginAnimation(WidthProperty,
                new DoubleAnimation { To = size.Width, Duration = dur, EasingFunction = ease });
        }
        else
        {
            // ⚠️ 先清钟再写值：DoubleAnimation 默认 FillBehavior=HoldEnd，动画结束后仍持续压过本地赋值
            _indicatorTf.BeginAnimation(TranslateTransform.XProperty, null);
            _indicator.BeginAnimation(WidthProperty, null);
            _indicatorTf.X = pos.X;
            _indicator.Width = size.Width;
        }
        _indicator.Height = size.Height;

        _lastPos = pos;
        _lastSize = size;
        _pendingAnimate = false;
        _indicator.Visibility = Visibility.Visible;
    }
}
