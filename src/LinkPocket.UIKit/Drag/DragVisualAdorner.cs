using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LinkPocket.I18n;
using LinkPocket.UIKit;
using System.Windows.Media.Effects;
using LinkPocket.ViewModels;
using Material3.Wpf;

namespace LinkPocket.Views;

/// <summary>
/// 拖拽浮层（复刻 Windows 11 资源管理器的拖拽手感）：
/// <list type="bullet">
/// <item>跟随指针的**半透明行快照**（图标 + 名称）；多选时下方叠出"多层"观感并挂「N 个项目」计数徽标；</item>
/// <item>指针右下方的「移动到「X」」提示（内容随落点实时变化；无落点/非法落点时整条隐藏）。</item>
/// </list>
///
/// <para>为什么自绘而不是调 OLE 原生拖拽图像：原生图像需要 shell <c>IDataObject</c> + <c>IDragSourceHelper</c>
/// （P/Invoke + COM 互操作），观感与环境相关问题都难控制、渲染检查也无法断言；自绘浮层挂在浏览页的 AdornerLayer 上，
/// 主栏 / 左栏树 / 跨栏拖拽天然是**同一套实现**（这正是本功能要的一致性）。</para>
///
/// <para>⚠️ 必须 <c>IsHitTestVisible = false</c>：否则浮层会吃掉指针下方的 DragOver/Drop，
/// 落点判定与光标会全部失效。位置更新走宿主在 <c>GiveFeedback</c> 里读屏幕坐标（拖拽期间 WPF 不再派发 MouseMove）。</para>
///
/// <para><b>逐帧开销</b>：位移**不落在本装饰器自己的变换上**，
/// 而是挂在内部内容层的 <see cref="UIElement.RenderTransform"/> 上。两条理由：
/// ① `AdornerLayer` 每次排列都会把 <see cref="GetDesiredTransform"/> 的结果直接写进装饰器的 `RenderTransform`
///    （`Adorner.AdornerTransform` 就是它的别名），装饰器自持的变换会被当场覆盖（实测：落位恒为 (0,0)）；
/// ② 走 <c>AdornerLayer.Update</c> 刷新自己的变换会 invalidate **measure**，失效沿可视树一路上传到窗口根——
///    指针每移动一次就换来一次**全窗重排 + 浮层（含两处投影模糊）整体重绘**，这才是掉帧的真身。
/// 现在：指针位移 = 一次渲染级变换（不测量、不排列），内容层再套 <see cref="BitmapCache"/>
/// （移动复用已光栅化的位图、不重跑模糊）；只有**内容变化**（换名称 / 换提示 / 多选项数）才经
/// <see cref="RefreshLayer"/> 重排装饰层——拖拽期间每秒至多数次。</para>
///
/// <para>归属 <c>LinkPocket.UIKit</c>：浏览页与回收站
/// 两页共用同一份浮层实现（public——页面程序集都要用；内部可见性后门是架构红线，不开）。</para>
/// </summary>
public sealed class DragVisualAdorner : Adorner
{
    /// <summary>浮层相对指针的偏移（与 Windows 的手感一致：图像落在指针右下，不遮住指针本身）。</summary>
    private const double PointerOffsetX = 12;
    private const double PointerOffsetY = 14;

    /// <summary>浮层视觉（卡片 + 堆叠背板 + 计数徽标 + 提示条）：指针位移也挂在它身上，见类注释。</summary>
    private readonly DragVisualContent _content;

    /// <summary>指针位移的载体（内容层的本地变换）：拖拽期间每次移动只改它，不触发任何测量/排列。</summary>
    private readonly TranslateTransform _position = new();

    private DragVisualAdorner(FrameworkElement owner) : base(owner)
    {
        IsHitTestVisible = false;   // 关键：浮层绝不能参与命中测试
        _content = new DragVisualContent(owner) { RenderTransform = _position };
        AddVisualChild(_content);   // 注册为真正的可视子级（否则变换链/命中链认不到父子关系）
        // 位图缓存（缓式合成）：移动时不再重新光栅化内容（投影模糊是逐帧重绘里最贵的一步）。
        // RenderAtScale 按 DPI 取，避免高 DPI 下缓存被放大后发虚。
        _content.CacheMode = new BitmapCache { RenderAtScale = VisualTreeHelper.GetDpi(owner).DpiScaleX };
    }

    /// <summary>在宿主所在的可视树上挂一个浮层（同一宿主只挂一个）。</summary>
    public static DragVisualAdorner? Attach(FrameworkElement owner)
    {
        var layer = AdornerLayer.GetAdornerLayer(owner);
        if (layer == null) return null;
        var adorner = new DragVisualAdorner(owner);
        layer.Add(adorner);
        return adorner;
    }

    /// <summary>
    /// 设置本次拖动的内容（Windows 口径）：
    /// <list type="bullet">
    /// <item>单项 = 该项的行快照（类型图标 + 名称）；</item>
    /// <item>多选 = **只显示项数**（「N 个项目」徽标），不再显示"其中某一项"的名称——
    /// 多选时显示哪一个名字都是误导（显示的应是项数而不是某一项的名称）。
    /// 计数口径 = 拖动集合里的实体数（文件夹 / 链接各算一项，**不含**文件夹里的子项），与选中统计一致。</item>
    /// </list>
    /// </summary>
    public void Show(IReadOnlyList<DragItem> items)
    {
        _content.Show(items);
        RefreshLayer();   // 内容与尺寸都变了 → 让装饰层重新量一次
    }

    /// <summary>更新「移动到 X」提示（空串 = 隐藏整条）。
    /// **内容没变就不重排**：拖拽悬停在同一落点上时 <c>DragOver</c> 会按鼠标移动频率反复调用，
    /// 每次都重排装饰层纯属白烧——掉帧的主要来源就在这条逐帧路径上。</summary>
    public void UpdateHint(string hintText)
    {
        if (!_content.UpdateHint(hintText)) return;
        RefreshLayer();
    }

    /// <summary>更新位置（宿主给的坐标须相对被装饰元素）：**只改内容层的本地变换**，绝不碰装饰层——
    /// 本方法在拖拽期间按指针移动频率（每帧）被 <c>GiveFeedback</c> 调用，见类注释的逐帧开销说明。</summary>
    public void UpdatePosition(Point ownerPoint)
    {
        _position.X = ownerPoint.X + PointerOffsetX;
        _position.Y = ownerPoint.Y + PointerOffsetY;
    }

    /// <summary>
    /// 内容/尺寸变化后通知装饰层重排（**只有内容变化走这里**，指针位移不走——见 <see cref="UpdatePosition"/>）。
    /// ⚠️ 只调 <see cref="UIElement.InvalidateArrange"/> **不够**：装饰层在 <c>AdornerLayer.Update</c> 里
    /// 清掉缓存的变换并 invalidate measure，尺寸才会真的按新值落地（渲染检查实测：不 Update 会冻在初次布局处）。</summary>
    private void RefreshLayer()
    {
        if (Parent is AdornerLayer layer) layer.Update(AdornedElement);
        InvalidateArrange();
    }

    /// <summary>从可视树摘掉浮层（拖拽结束 / 页面卸载）。</summary>
    public void Detach() => (Parent as AdornerLayer)?.Remove(this);

    /// <summary>只保留装饰层给的基准变换：指针位移在内容层上（见 <see cref="UpdatePosition"/>）。</summary>
    public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
        => base.GetDesiredTransform(transform);

    protected override Size MeasureOverride(Size constraint)
    {
        _content.Measure(constraint);
        return _content.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _content.Arrange(new Rect(new Point(0, 0), _content.DesiredSize));
        return finalSize;
    }

    protected override Visual GetVisualChild(int index) => _content;

    protected override int VisualChildrenCount => 1;

    /// <summary>
    /// 浮层视觉（卡片 + 堆叠背板 + 计数徽标 + 提示条）：自带 <c>Measure</c>/<c>Arrange</c>，
    /// 尺寸由内容量出；指针位移由宿主的本地变换施加（见 <see cref="DragVisualAdorner"/> 类注释）。
    /// </summary>
    private sealed class DragVisualContent : FrameworkElement
    {
        /// <summary>名称列最大宽度（超出省略号；浮层不该盖住半个屏幕）。</summary>
        private const double MaxNameWidth = 220;

        private readonly Border _card;
        private readonly TextBlock _name;
        private readonly M3Icon _icon;
        private readonly Border _stackBack2;
        private readonly Border _stackBack1;
        private readonly Border _badge;
        private readonly TextBlock _badgeText;
        private readonly Border _hint;
        private readonly TextBlock _hintText;

        internal DragVisualContent(FrameworkElement owner)
        {
            var surface = Brush(owner, "SurfaceContainerHigh");
            var outline = Brush(owner, "SurfaceContainerHighest");
            var onSurface = Brush(owner, "OnSurface");
            var primary = Brush(owner, "Primary");
            var badgeBg = Brush(owner, "SecondaryContainer");
            var badgeFg = Brush(owner, "OnSecondaryContainer");

            // —— 堆叠感（多选时露出的两层"背板"）——
            _stackBack2 = StackLayer(surface, 4);
            _stackBack1 = StackLayer(surface, 2);

            _icon = new M3Icon { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = primary };

            _name = new TextBlock
            {
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Foreground = onSurface,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = MaxNameWidth,
            };

            _badgeText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = badgeFg,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _badge = new Border
            {
                Background = badgeBg,
                CornerRadius = new CornerRadius(9),
                MinHeight = 18,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = _badgeText,
                Visibility = Visibility.Collapsed,
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(_icon);
            content.Children.Add(_name);
            content.Children.Add(_badge);

            _card = new Border
            {
                Background = surface,
                BorderBrush = outline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 5, 10, 5),
                Opacity = 0.85,          // 半透明：与资源管理器一样能看见下面的落点
                Child = content,
                Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.25, Color = Colors.Black },
            };

            // —— 「移动到「X」」提示（独立小条，落在浮层下方）——
            _hintText = new TextBlock { FontSize = 11.5, Foreground = onSurface, VerticalAlignment = VerticalAlignment.Center };
            _hint = new Border
            {
                Background = surface,
                BorderBrush = outline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Opacity = 0.95,
                Visibility = Visibility.Collapsed,
                Child = _hintText,
                Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.2, Color = Colors.Black },
            };

            // 注册可视子级（顺序 = 自下而上；与 GetVisualChild 的下标顺序一致）
            AddVisualChild(_stackBack2);
            AddVisualChild(_stackBack1);
            AddVisualChild(_card);
            AddVisualChild(_hint);
        }

        /// <summary>设置本次拖动的内容（单项 = 图标 + 名称；多选 = 仅项数徽标 + 叠层）。</summary>
        internal void Show(IReadOnlyList<DragItem> items)
        {
            var first = items.FirstOrDefault();
            if (first == null) return;

            var multiple = items.Count > 1;
            // 类型图标取"抓住的那一项"（载荷首位由 VM 保证）——多选时它只是图标，不再伴随名字
            _icon.Kind = first.IsFolder ? "folder" : "link-variant";
            _name.Text = first.Name;
            _name.Visibility = multiple ? Visibility.Collapsed : Visibility.Visible;

            _badgeText.SetText(Loc.K("drag.itemCount", items.Count));
            _badge.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
            _stackBack1.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
            _stackBack2.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;

            InvalidateArrange();
        }

        /// <summary>更新「移动到 X」提示；返回是否真的变了（未变 = 宿主无需重排装饰层）。</summary>
        internal bool UpdateHint(string hintText)
        {
            var show = !string.IsNullOrEmpty(hintText);
            var shown = _hint.Visibility == Visibility.Visible;
            if (shown == show && (!show || _hintText.Text == hintText)) return false;
            if (show) _hintText.Text = hintText;
            _hint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return true;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            _card.Measure(constraint);
            _hint.Measure(constraint);
            var w = Math.Max(_card.DesiredSize.Width, _hint.DesiredSize.Width) + 8;
            var h = _card.DesiredSize.Height + _hint.DesiredSize.Height + 6;
            return new Size(w, h);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // 浮层左上角 = 指针 + 偏移（由宿主的本地变换施加）；堆叠背板向左上各让 2/4px；提示条在浮层正下方
            _stackBack2.Arrange(new Rect(4, 0, _card.DesiredSize.Width, _card.DesiredSize.Height));
            _stackBack1.Arrange(new Rect(2, 2, _card.DesiredSize.Width, _card.DesiredSize.Height));
            _card.Arrange(new Rect(4, 4, _card.DesiredSize.Width, _card.DesiredSize.Height));
            var hintTop = 4 + _card.DesiredSize.Height + 4;
            _hint.Arrange(new Rect(6, hintTop, Math.Max(_hint.DesiredSize.Width, 0), _hint.DesiredSize.Height));
            return finalSize;
        }

        protected override Visual GetVisualChild(int index)
            => index switch
            {
                0 => _stackBack2,
                1 => _stackBack1,
                2 => _card,
                _ => _hint,
            };

        protected override int VisualChildrenCount => 4;

        private static Border StackLayer(Brush background, double inset) => new()
        {
            Background = background,
            CornerRadius = new CornerRadius(8),
            Opacity = 0.5,
            Margin = new Thickness(inset, inset, 0, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        /// <summary>
        /// 从宿主（被拖拽元素）身上解析一个主题画刷。
        /// </summary>
        /// <remarks>
        /// <b>为什么取不到就抛、不兜一个写死的颜色</b>：浮层是**每次拖拽重建**的，兜底色会把
        /// "主题令牌未装配"这一真实故障伪装成"一张看起来正常、其实不受主题控制的浮层"——
        /// 观测面纪律禁止静默兜底（与 <c>ColorPickerPopup.TokenColor</c> 同一口径）。
        /// <b>为什么不用 <c>SetResourceReference</c></b>：浮层的三个子级是经 <c>AddVisualChild</c>
        /// 挂进来的裸视觉子级（不参与逻辑树），资源引用在这里解析不到宿主那一侧的资源字典，
        /// 只会静默变成"空画刷"；显式向宿主取才是可靠路径。
        /// </remarks>
        private static Brush Brush(FrameworkElement owner, string key)
            => owner.TryFindResource(key) as Brush
               ?? throw new InvalidOperationException(
                   $"the drag adorner cannot resolve the resource '{key}' -- the theme is not assembled or the key is stale (a fallback colour would hide the failure)");
    }
}
