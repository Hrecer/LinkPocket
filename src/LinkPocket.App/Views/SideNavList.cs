using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LinkPocket.Views;

/// <summary>
/// 侧栏导航列表（可复用组件）：工具页左栏与设置页左栏共用。
///
/// <para>ListBox 子类 —— 选中高亮不画在项上，而是一枚垫在项下面的<b>滑动指示器</b>
/// （PrimaryContainer 药丸，模板部件 PART_Indicator / PART_IndicatorTf），
/// 切换时 240ms CubicEase 缓动滑到新选中项的<b>实测几何</b>（TransformToVisual，天然适配边距与滚动）。</para>
///
/// <para>规范：项样式用 App.xaml 的 <c>SideNavItemStyle</c>（悬停零底色反馈——滑动方案下色块像阴影；
/// 选中仅字色加深 + 加粗，底色由指示器承担）；控件样式 <c>SideNavListStyle</c> 必须显式引用。
/// 首帧/几何变化（窗口缩放）无动画贴齐；仅用户切换选择时才播放滑动动画。</para>
/// </summary>
public class SideNavList : ListBox
{
    private Border? _indicator;
    private TranslateTransform? _indicatorTf;
    private Point? _lastPos;
    private Size _lastSize;
    private bool _pendingAnimate;

    public SideNavList()
    {
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

        // 无选中（如设置页每次进入先清选）：指示器隐藏
        if (SelectedItem == null || SelectedIndex < 0)
        {
            _indicator.Visibility = Visibility.Collapsed;
            _lastPos = null;
            return;
        }

        if (ItemContainerGenerator.ContainerFromItem(SelectedItem) is not ListBoxItem container)
            return;
        if (container.ActualHeight <= 0)
            return; // 容器尚未完成布局，等下一轮 LayoutUpdated 再贴

        var pos = container.TransformToVisual(this).Transform(new Point(0, 0));
        var size = new Size(container.ActualWidth, container.ActualHeight);

        var firstShow = _indicator.Visibility != Visibility.Visible;
        var moved = _lastPos != pos || _lastSize != size;
        if (!moved && !firstShow) return; // 几何没变：不打扰进行中的动画

        _indicator.Width = size.Width;
        _indicator.Height = size.Height;

        var animate = _pendingAnimate && !firstShow;
        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(240);
            _indicatorTf.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation { To = pos.X, Duration = dur, EasingFunction = ease });
            _indicatorTf.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation { To = pos.Y, Duration = dur, EasingFunction = ease });
        }
        else
        {
            _indicatorTf.BeginAnimation(TranslateTransform.XProperty, null);
            _indicatorTf.BeginAnimation(TranslateTransform.YProperty, null);
            _indicatorTf.X = pos.X;
            _indicatorTf.Y = pos.Y;
        }

        _lastPos = pos;
        _lastSize = size;
        _pendingAnimate = false;
        _indicator.Visibility = Visibility.Visible;
    }
}
