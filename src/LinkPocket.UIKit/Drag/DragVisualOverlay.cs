using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LinkPocket.I18n;
using LinkPocket.UIKit;
using LinkPocket.ViewModels;
using Material3.Wpf;

namespace LinkPocket.Views;

/// <summary>
/// 拖拽浮层（复刻 Windows 11 资源管理器手感）：
/// <list type="bullet">
/// <item>跟随指针的**半透明行快照**（图标 + 名称）；多选时下方叠出"多层"观感并挂「N 个项目」计数徽标；</item>
/// <item>浮层被指针**按抓取点捏住**（抓在行左侧 = 指针落在快照左侧，抓在中间 = 指针落在快照中间），
/// 不是固定挂在指针右下角——像把这一行真的提起来，而不是给指针配一条标注；</item>
/// <item>浮层下方的「移动到「X」」提示（内容随落点实时变化；无落点/非法落点时整条隐藏）。</item>
/// </list>
///
/// <para><b>为什么用独立小窗，而不是挂在页面的装饰层上</b>：主窗口为了圆角/阴影开启了
/// <c>AllowsTransparency</c>，整窗走软件合成；把浮层画在主窗口里时，指针已经移动、浮层画面却要等
/// 主窗口的合成帧——屏幕实测滞后 2–9px（150% DPI，运动方向上稳定落后、静止归零，位移算法本身零偏差）。
/// 改成独立小窗后，拖动只移动窗口、不重绘主窗口，DWM 直接移动窗口：同一套屏幕取点实测滞后 0px。
/// 必须 <c>WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>：窗口永远在指针下方，
/// 不穿透就会挡住真实拖拽的落点命中；同时不进 Alt-Tab、不抢焦点。</para>
///
/// <para><b>位置口径</b>：宿主给的是**被装饰视图坐标**（<c>ownerPoint</c>），这里换算成屏幕物理像素，
/// 再用 <see cref="SetWindowPos"/> 直接摆窗口（不做 WPF 布局/变换）。抓取锚点 = 指针在来源行里的相对比例
/// 映射到快照卡片的同一点，于是"抓哪儿、快照的哪儿就在指针下"。内容变化（提示条显隐）只改小窗尺寸并
/// 按上次指针重新钉住左上角，卡片不位移。</para>
///
/// <para>归属 <c>LinkPocket.UIKit</c>：浏览页与回收站两页共用同一份浮层实现（public——页面程序集都要用；
/// 内部可见性后门是架构红线，不开）。</para>
/// </summary>
public sealed class DragVisualOverlay
{
    /// <summary>浮层内容（卡片 + 堆叠背板 + 计数徽标 + 提示条），承载在独立小窗里。</summary>
    private readonly DragVisualContent _content;

    /// <summary>被装饰视图：用于把视图坐标换算成屏幕物理像素。</summary>
    private readonly FrameworkElement _owner;

    /// <summary>承载内容的小窗（拖拽期间存在，结束即关闭）。</summary>
    private Window? _overlay;

    /// <summary>
    /// 抓取点在内容里的位置（内容坐标）：来源元素里按下时指针落在哪个相对比例，
    /// 这里就取快照里的同一比例。窗口左上角 = 屏幕指针 − 它，于是"抓哪儿、快照的哪儿就在指针下"。
    /// </summary>
    private Point _grabAnchor = new(4, 4);

    /// <summary>最近一次指针位置（视图坐标）：提示条显隐改尺寸后，按它重新钉住窗口。</summary>
    private Point _lastOwnerPoint;
    private bool _hasLastOwnerPoint;

    /// <summary>最近一次窗口位置（屏幕物理像素）：同一点不重复调窗口 API。</summary>
    private int _lastWindowX = int.MinValue;
    private int _lastWindowY = int.MinValue;

    private DragVisualOverlay(FrameworkElement owner)
    {
        _owner = owner;
        _content = new DragVisualContent(owner);
    }

    /// <summary>为宿主挂一个浮层（同一宿主只挂一个）。建不出小窗时返回 null，拖拽本身不受影响。</summary>
    public static DragVisualOverlay? Attach(FrameworkElement owner)
    {
        var overlay = new DragVisualOverlay(owner);
        return overlay.OpenOverlay() ? overlay : null;
    }

    /// <summary>
    /// 建独立小窗并把内容移进去（先不显示：等 <see cref="Show"/> 量好尺寸、宿主紧接着摆位后再显示）。
    /// </summary>
    private bool OpenOverlay()
    {
        try
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = _content,
                IsHitTestVisible = false,
                Left = -20000,
                Top = -20000,
                Width = 1,
                Height = 1,
            };
            window.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                var exStyle = GetWindowLong(hwnd, GwlExStyle);
                SetWindowLong(hwnd, GwlExStyle, exStyle | WsExTransparent | WsExNoActivate | WsExToolWindow);
            };
            _overlay = window;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // 无窗口系统 / 窗口创建被拒：如实降级为"没有浮层"，不影响拖拽本身
            return false;
        }
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
    /// <param name="grabFraction">
    /// 抓取点在源元素里的相对位置（0..1，宿主按按下时刻的指针坐标算好）：
    /// 浮层按同一比例被指针"捏住"。来源尺寸不可得时传 (0.5, 0.5)（指针落在快照中心）。
    /// </param>
    public void Show(IReadOnlyList<DragItem> items, Point grabFraction)
    {
        _content.Show(items);
        // 量一次才拿得到卡片的真实宽高；只量不排，目的是算抓取锚点与小窗尺寸。
        _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _grabAnchor = _content.AnchorFor(grabFraction);
        ResizeOverlay();
        _overlay?.Show();   // 随后的 UpdatePosition 在同一条调用链里就位，首帧不会闪
    }

    /// <summary>更新「移动到 X」提示（空串 = 隐藏整条）。</summary>
    public void UpdateHint(string hintText)
    {
        if (!_content.UpdateHint(hintText)) return;
        ResizeOverlay();
        // 提示条显隐会改变小窗尺寸；立刻按上次指针重新钉住左上角，卡片相对指针不位移。
        if (_hasLastOwnerPoint) UpdatePosition(_lastOwnerPoint);
    }

    /// <summary>更新位置（宿主给的坐标须相对被装饰视图）：换算成屏幕物理像素后直接移动小窗。</summary>
    public void UpdatePosition(Point ownerPoint)
    {
        _lastOwnerPoint = ownerPoint;
        _hasLastOwnerPoint = true;
        if (_overlay == null || !_owner.IsLoaded) return;
        var hwnd = new WindowInteropHelper(_overlay).Handle;
        if (hwnd == IntPtr.Zero) return;

        Point screen;
        try
        {
            screen = _owner.PointToScreen(ownerPoint);
        }
        catch (InvalidOperationException)
        {
            return;   // 宿主已脱离可视树（页面正在卸载）：本轮拖拽不再摆位
        }

        var dpi = VisualTreeHelper.GetDpi(_owner);
        var x = (int)Math.Round(screen.X - _grabAnchor.X * dpi.DpiScaleX);
        var y = (int)Math.Round(screen.Y - _grabAnchor.Y * dpi.DpiScaleY);
        if (x == _lastWindowX && y == _lastWindowY) return;   // 同一点不重复调窗口 API
        _lastWindowX = x;
        _lastWindowY = y;
        SetWindowPos(hwnd, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    /// <summary>按内容尺寸调整小窗（提示条显隐只改下方的提示区，卡片仍在内容原点 + 抓取锚点）。</summary>
    private void ResizeOverlay()
    {
        _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (_overlay == null) return;
        var size = _content.DesiredSize;
        _overlay.Width = Math.Max(1, size.Width);
        _overlay.Height = Math.Max(1, size.Height);
        _overlay.UpdateLayout();
    }

    /// <summary>关闭浮层小窗（拖拽结束 / 页面卸载）。</summary>
    public void Detach()
    {
        try { _overlay?.Close(); }
        catch (InvalidOperationException) { /* 窗口已关闭：收尾幂等 */ }
        _overlay = null;
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// 浮层视觉（卡片 + 堆叠背板 + 计数徽标 + 提示条）：自带 <c>Measure</c>/<c>Arrange</c>，尺寸由内容量出。
    /// 小窗移动不重绘内容；只有内容变化（换名称 / 换提示 / 多选项数）才重新布局这个轻量可视树。
    /// </summary>
    private sealed class DragVisualContent : FrameworkElement
    {
        /// <summary>名称列最大宽度（超出省略号；浮层不该盖住半个屏幕）。</summary>
        private const double MaxNameWidth = 220;

        /// <summary>卡片在内容层里的落点（与 <see cref="ArrangeOverride"/> 共用，锚点计算必须读同一组值）。</summary>
        private const double CardLeft = 4;
        private const double CardTop = 4;

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

        /// <summary>更新「移动到 X」提示；返回是否真的变了（未变 = 宿主无需重排/改窗口尺寸）。</summary>
        internal bool UpdateHint(string hintText)
        {
            var show = !string.IsNullOrEmpty(hintText);
            var shown = _hint.Visibility == Visibility.Visible;
            if (shown == show && (!show || _hintText.Text == hintText)) return false;
            if (show) _hintText.Text = hintText;
            _hint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return true;
        }

        /// <summary>
        /// 抓取比例 → 内容坐标：指针按同一比例落在卡片上（抓在行左侧 = 落在快照左侧）。
        /// 卡片宽度/高度来自最近一次测量；比例为 0..1，宿主已保证来源尺寸有效。
        /// </summary>
        internal Point AnchorFor(Point fraction)
        {
            var card = _card.DesiredSize;
            return new Point(
                CardLeft + Math.Clamp(fraction.X, 0, 1) * card.Width,
                CardTop + Math.Clamp(fraction.Y, 0, 1) * card.Height);
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
            // 卡片在内容层的位置由抓取锚点反推（宿主把"锚点"摆到指针下）；堆叠背板向左上各让 2/4px；
            // 提示条贴在卡片正下方。
            _stackBack2.Arrange(new Rect(CardLeft, 0, _card.DesiredSize.Width, _card.DesiredSize.Height));
            _stackBack1.Arrange(new Rect(2, 2, _card.DesiredSize.Width, _card.DesiredSize.Height));
            _card.Arrange(new Rect(CardLeft, CardTop, _card.DesiredSize.Width, _card.DesiredSize.Height));
            var hintTop = CardTop + _card.DesiredSize.Height + 4;
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
        /// <b>为什么不用 <c>SetResourceReference</c></b>：浮层的子级是经 <c>AddVisualChild</c>
        /// 挂进来的裸视觉子级（不参与逻辑树），资源引用在这里解析不到宿主那一侧的资源字典，
        /// 只会静默变成"空画刷"；显式向宿主取才是可靠路径。
        /// </remarks>
        private static Brush Brush(FrameworkElement owner, string key)
            => owner.TryFindResource(key) as Brush
               ?? throw new InvalidOperationException(
                   $"the drag overlay cannot resolve the resource '{key}' -- the theme is not assembled or the key is stale (a fallback colour would hide the failure)");
    }
}
