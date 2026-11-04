using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
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
/// <c>AllowsTransparency</c>，整窗走软件合成；浮层若画在主窗口里，位置会依赖整窗的合成节奏。
/// 改成独立小窗后，拖动只移动窗口、不重绘主窗口，DWM 直接移动窗口。
/// 必须 <c>WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>：窗口永远在指针下方，
/// 不穿透就会挡住真实拖拽的落点命中；同时不进 Alt-Tab、不抢焦点。</para>
///
/// <para><b>位置口径</b>：宿主给的是**被装饰视图坐标**（<c>ownerPoint</c>），这里换算成屏幕物理像素，
/// 由专用线程按系统光标驱动 visual offset（不做 WPF 布局/变换）。抓取锚点 = 指针在来源行里的相对比例
/// 映射到快照卡片的同一点，于是"抓哪儿、快照的哪儿就在指针下"。指针由系统光标平面绘制，
/// 浮层只追随位置，不接管光标。</para>
///
/// <para>归属 <c>LinkPocket.UIKit</c>：浏览页与回收站两页共用同一份浮层实现（public——页面程序集都要用；
/// 内部可见性后门是架构红线，不开）。</para>
/// </summary>
public sealed class DragVisualOverlay
{
    /// <summary>浮层内容（卡片 + 堆叠背板 + 计数徽标 + 提示条），在主线程渲染成位图后交给专用线程承载。</summary>
    private readonly DragVisualContent _content;

    /// <summary>被装饰视图：用于把视图坐标换算成屏幕物理像素。</summary>
    private readonly FrameworkElement _owner;

    /// <summary>
    /// 抓取点在内容里的位置（内容坐标）：来源元素里按下时指针落在哪个相对比例，
    /// 这里就取快照里的同一比例。窗口左上角 = 屏幕指针 − 它，于是"抓哪儿、快照的哪儿就在指针下"。
    /// </summary>
    private Point _grabAnchor = new(16, 16);

    /// <summary>本次提交给专用线程的位图（保留引用，便于诊断/生命周期）。</summary>
    private System.Windows.Media.Imaging.RenderTargetBitmap? _bitmap;

    /// <summary>
    /// 本次拖拽是否已把内容提交给浮层宿主（**最佳努力，不代表窗口真的可见**）。
    /// 宿主初始化失败时 <c>Show</c> 会在内部直接返回；这里仍为 true，以便后续 hint/position
    /// 调用保持同一条代码路径（宿主的写入口会在 <c>_failed</c> 时 no-op）。
    /// 真正的可见性只由 <see cref="IsShown"/> 读取宿主状态。
    /// </summary>
    private bool _shown;

    private DragVisualOverlay(FrameworkElement owner)
    {
        _owner = owner;
        _content = new DragVisualContent(owner);
    }

    /// <summary>为宿主挂一个浮层（同一宿主只挂一个）。建不出内容时返回 null，拖拽本身不受影响。</summary>
    public static DragVisualOverlay? Attach(FrameworkElement owner)
    {
        try
        {
            var overlay = new DragVisualOverlay(owner);
            GhostWindowHost.Shared.Prewarm();   // 后台预热点：线程冷启动不落在首次拖拽路径上
            return overlay;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // 主题令牌缺失/无窗口系统：如实降级为"没有浮层"，不影响拖拽本身
            return null;
        }
    }

    /// <summary>提前启动浮层专用线程（页面加载时调用，避免第一次拖拽等待冷启动）。</summary>
    public static void Prewarm()
    {
        GhostWindowHost.Shared.Prewarm();
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
    public void Show(IReadOnlyList<DragItem> items, Point grabFraction, bool autoFollow = false)
    {
        try
        {
            _content.Show(items);
            // 卡片位图整段拖拽只上传一次：部分设备上 DComp surface 可见后换内容不会重新合成，
            // 提示条若还画在卡片位图里，每次换字都会把整张卡片一起带成空白。
            _content.UpdateHint(string.Empty);
            ArrangeContent();
            _grabAnchor = _content.AnchorFor(grabFraction);
#if LINKPOCKET_DRAG_DIAGNOSTICS
            _content.DebugSetMarker(_grabAnchor, DebugMarkerColor);
#endif
            var dpi = VisualTreeHelper.GetDpi(_owner);
            var bitmap = RenderContent(dpi);
            if (bitmap == null) return;

            _bitmap = bitmap;
            GhostWindowHost.Shared.Show(bitmap,
                _grabAnchor.X * dpi.DpiScaleX,
                _grabAnchor.Y * dpi.DpiScaleY,
                autoFollow);
            _shown = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // 内容构造/资源解析失败 = 本次拖拽没有浮层，不是拖拽失败；落点、OLE 与数据传输完全不受影响。
            // 宿主线程的初始化失败在 GhostWindowHost 内部自行降级（no-op），不会以异常形式传到这里。
            _shown = false;
        }
    }

    /// <summary>更新「移动到 X」提示（空串 = 隐藏整条）。</summary>
    public void UpdateHint(string hintText)
    {
        // 提示条是独立的分层小窗，只裁它自己那一块位图；卡片位图在整段拖拽里保持不动，
        // 因此提示换字不会触发 DComp 内容替换，也就不会把卡片一起带成空白。
        if (!_content.UpdateHint(hintText)) return;
        if (!_shown) return;

        try
        {
            if (string.IsNullOrEmpty(hintText))
            {
                GhostWindowHost.Shared.HideHint();
                return;
            }

            ArrangeContent();
#if LINKPOCKET_DRAG_DIAGNOSTICS
            _content.DebugSetHintMarker(DebugMarkerColor);
#endif
            var dpi = VisualTreeHelper.GetDpi(_owner);
            var full = RenderContent(dpi);
            if (full == null) return;
            var hint = CropHint(full, dpi, out var offsetX, out var offsetY);
#if LINKPOCKET_DRAG_DIAGNOSTICS
            if (hint != null)
            {
                // 确认裁剪位图本身不是空的。
                var probePixels = new byte[hint.PixelWidth * hint.PixelHeight * 4];
                hint.CopyPixels(probePixels, hint.PixelWidth * 4, 0);
                var opaque = 0;
                var magenta = 0;
                for (var i = 3; i < probePixels.Length; i += 4)
                {
                    if (probePixels[i] != 0) opaque++;
                    if (probePixels[i] > 180 && probePixels[i + 1] > 180 && probePixels[i + 2] < 90) magenta++;
                }
                DebugHintOpaque = opaque;
                DebugHintMarkerPixels = magenta;
                DebugHintBitmapSize = new Size(hint.PixelWidth, hint.PixelHeight);
            }
#endif
            var hintPixels = hint == null ? null : GhostBitmap.From(hint);
            GhostWindowHost.Shared.SetHint(hintPixels, offsetX, offsetY);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
        }
    }

    /// <summary>提示条投影留白（DIP）：BlurRadius 10 + ShadowDepth 2，再留 2px 余量。</summary>
    private const double HintShadowMargin = 14;

    /// <summary>
    /// 从整张内容位图里裁出提示条（含投影）那一块。偏移 = 裁剪块左上角相对内容原点的物理像素，
    /// 宿主用它把提示小窗贴到卡片下方。
    /// </summary>
    private System.Windows.Media.Imaging.BitmapSource? CropHint(
        System.Windows.Media.Imaging.RenderTargetBitmap full, DpiScale dpi,
        out int offsetX, out int offsetY)
    {
        offsetX = 0;
        offsetY = 0;
        var hint = _content.HintRect;
        if (hint.Width <= 0 || hint.Height <= 0) return null;

        var size = _content.DesiredSize;
        var left = Math.Max(0, hint.Left - HintShadowMargin);
        var top = Math.Max(0, hint.Top - HintShadowMargin);
        var right = Math.Min(size.Width, hint.Right + HintShadowMargin);
        var bottom = Math.Min(size.Height, hint.Bottom + HintShadowMargin);
        if (right <= left || bottom <= top) return null;

        var x = (int)Math.Floor(left * dpi.DpiScaleX);
        var y = (int)Math.Floor(top * dpi.DpiScaleY);
        var width = Math.Min(full.PixelWidth - x, (int)Math.Ceiling((right - left) * dpi.DpiScaleX));
        var height = Math.Min(full.PixelHeight - y, (int)Math.Ceiling((bottom - top) * dpi.DpiScaleY));
        if (width <= 0 || height <= 0) return null;

        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(
            full, new Int32Rect(x, y, width, height));
        cropped.Freeze();
        offsetX = x;
        offsetY = y;
        return cropped;
    }

    /// <summary>更新位置（宿主给的坐标须相对被装饰视图）：换算成屏幕物理像素后交给浮层线程。</summary>
    public void UpdatePosition(Point ownerPoint)
    {
        if (!_shown) return;
        // 自动跟随时窗口由专用线程按系统光标驱动：主线程再摆位只会与专用线程抢方向。
        if (GhostWindowHost.Shared.IsAutoFollowing) return;

        Point screen;
        try
        {
            screen = _owner.PointToScreen(ownerPoint);
        }
        catch (InvalidOperationException)
        {
            return;   // 宿主已脱离可视树（页面正在卸载）：本轮拖拽不再摆位
        }

        // PointToScreen 与宿主线程的 GetCursorPos 都是屏幕物理像素；锚点乘以同一个宿主 DPI，
        // 因此落位公式始终是“指针物理像素 − 卡片锚点物理像素”。DPI 在起手时固定，
        // 拖过不同显示器不会中途重建/缩放卡片（坐标仍逐像素对齐，见 GhostWindowHost 类头说明）。
        var dpi = VisualTreeHelper.GetDpi(_owner);
        var x = (int)Math.Round(screen.X - _grabAnchor.X * dpi.DpiScaleX);
        var y = (int)Math.Round(screen.Y - _grabAnchor.Y * dpi.DpiScaleY);
        GhostWindowHost.Shared.SetExplicitPosition(x, y);
    }

    /// <summary>开启/关闭浮层线程的自动跟随（真实拖拽开启；探针静态预览关闭后用显式摆位）。</summary>
    public void SetAutoFollow(bool enabled) => GhostWindowHost.Shared.SetAutoFollow(enabled);

    /// <summary>浮层当前是否显示（程序集内自检读取）。</summary>
    public bool IsShown => GhostWindowHost.Shared.IsVisible;

    /// <summary>浮层窗口句柄（程序集内自检读取）。</summary>
    public IntPtr HostWindowHandle => GhostWindowHost.Shared.Hwnd;

    /// <summary>浮层最后应用的卡片屏幕坐标（自检读取）。</summary>
    public int WindowX => GhostWindowHost.Shared.LastX;
    public int WindowY => GhostWindowHost.Shared.LastY;

    /// <summary>浮层是否处于自动跟随（真实拖拽期间）。</summary>
    public bool IsAutoFollowing => GhostWindowHost.Shared.IsAutoFollowing;

#if LINKPOCKET_DRAG_DIAGNOSTICS
    /// <summary>自检用显式摆位（屏幕物理像素）。</summary>
    public void DebugSetPosition(int x, int y) => GhostWindowHost.Shared.SetExplicitPosition(x, y);
#endif

    /// <summary>
    /// 关闭浮层（拖拽结束 / 页面卸载）：隐藏窗口，保留专用线程给下一次拖拽复用。
    /// 即使本次 <c>Show</c> 早退（宿主失败/无位图），也照样走 <c>Hide</c> 收尾；宿主侧
    /// 的 End 是幂等配对，没有对应 Begin 时为空操作，正常拖拽路径仍严格一次 Begin/一次 End。
    /// </summary>
    public void Detach()
    {
        _shown = false;
        _bitmap = null;
        GhostWindowHost.Shared.Hide();
    }

    /// <summary>把内容量好、排好，供离屏位图渲染（不依赖窗口/演示源）。</summary>
    private void ArrangeContent()
    {
        // 内容不在可视树里：父子之间的 measure 失效不会自动向上传播，换提示文字后必须显式强制重排，
        // 否则 Border 会复用“空提示”的缓存宽度（实测只剩 18px 内边距，位图里提示文字是空的）。
        _content.InvalidateMeasure();
        _content.InvalidateArrange();
        _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _content.Arrange(new Rect(_content.DesiredSize));
    }

    /// <summary>
    /// 把内容渲染成冻结位图（含投影/圆角；Pbgra32 预乘 alpha 正好贴合分层窗口）。
    /// 位图按被装饰视图的 DPI 渲染，物理尺寸 = DIP × DPI；抓取锚点也按同一比例换算成物理像素。
    /// 产出的 <c>RenderTargetBitmap.PixelWidth/PixelHeight</c> 已经是物理像素，宿主按 1:1 贴图；
    /// 它不需要也不解读 DPI 元数据（Pbgra32、每行 <c>PixelWidth * 4</c> 字节）。
    /// </summary>
    private System.Windows.Media.Imaging.RenderTargetBitmap? RenderContent(DpiScale dpi)
    {
        var size = _content.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0) return null;

        var width = Math.Max(1, (int)Math.Ceiling(size.Width * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(size.Height * dpi.DpiScaleY));
        // 离屏内容第一次 Render 只负责把子可视（图标模板 / 刚显形的提示条）实现出来，
        // 直接把它当结果会得到“缺图标、空提示”的位图；先用一张丢弃的热身位图触发实现，再渲染正式位图。
        var warm = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        warm.Render(_content);

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(_content);
        bitmap.Freeze();
        return bitmap;
    }

#if LINKPOCKET_DRAG_DIAGNOSTICS
    // 只读观测面：供内部资产 LinkPocket.DragDiagnostics 套件取数，不改变任何行为。
    public IntPtr DebugHwnd => GhostWindowHost.Shared.Hwnd;
    public Point DebugAnchor => _grabAnchor;
    public Size DebugWindowSize => _content.DesiredSize;
    public string DebugHintText => _content.DebugHintText;
    public int DebugWindowX => GhostWindowHost.Shared.LastX;
    public int DebugWindowY => GhostWindowHost.Shared.LastY;
    public bool DebugIsVisible => GhostWindowHost.Shared.IsVisible;
    public long DebugHostLoopCount => GhostWindowHost.Shared.DebugLoopCount;
    public double DebugAnchorScreenX => GhostWindowHost.Shared.AnchorX;
    public double DebugAnchorScreenY => GhostWindowHost.Shared.AnchorY;
    public int DebugBitmapWidth => GhostWindowHost.Shared.DebugBitmapWidth;
    public int DebugBitmapHeight => GhostWindowHost.Shared.DebugBitmapHeight;
    public long DebugRawInputCount => GhostWindowHost.Shared.DebugRawInputCount;
    public bool DebugHintVisible => GhostWindowHost.Shared.DebugHintVisible;
    public bool DebugHintWindowShown => GhostWindowHost.Shared.DebugHintWindowShown;
    public IntPtr DebugHintHwnd => GhostWindowHost.Shared.DebugHintHwnd;
    public int DebugHintOpaque { get; private set; }
    public int DebugHintMarkerPixels { get; private set; }
    public Size DebugHintBitmapSize { get; private set; }
    public int DebugHintSetupUs => GhostWindowHost.Shared.DebugHintSetupUs;
    public int DebugHintWindowUs => GhostWindowHost.Shared.DebugHintWindowUs;
    public int DebugHintDeviceUs => GhostWindowHost.Shared.DebugHintDeviceUs;
    public int DebugHintTargetUs => GhostWindowHost.Shared.DebugHintTargetUs;
    public int DebugHintContentUs => GhostWindowHost.Shared.DebugHintContentUs;
    public int DebugHintStage => GhostWindowHost.Shared.DebugHintStage;
    public int DebugHintError => GhostWindowHost.Shared.DebugHintError;
    public int DebugHintScreenX => GhostWindowHost.Shared.DebugHintScreenX;
    public int DebugHintScreenY => GhostWindowHost.Shared.DebugHintScreenY;
    public int DebugHintWidth => GhostWindowHost.Shared.DebugHintWidth;
    public int DebugHintHeight => GhostWindowHost.Shared.DebugHintHeight;
    public bool DebugIsAutoFollowing => GhostWindowHost.Shared.IsAutoFollowing;
    public bool DebugNoGcActive => GhostWindowHost.Shared.DebugNoGcActive;
    public int DebugTargetX => GhostWindowHost.Shared.DebugTargetX;
    public int DebugTargetY => GhostWindowHost.Shared.DebugTargetY;
    /// <summary>屏幕采样标记的精确颜色（测量用原色，不参与主题）。</summary>
    public System.Windows.Media.Color DebugMarkerColor => new() { A = 255, R = 255, G = 0, B = 255 };

    /// <summary>host 线程按 QPC 回查“该帧的光标/已落位窗口位置”。</summary>
    public bool DebugTryGetHostSampleAt(long qpc,
        out int cursorX, out int cursorY, out bool hasCursor,
        out int appliedX, out int appliedY, out long appliedSinceQpc)
        => GhostWindowHost.Shared.TryGetHostSampleAt(qpc,
            out cursorX, out cursorY, out hasCursor,
            out appliedX, out appliedY, out appliedSinceQpc);

    /// <summary>宿主线程逐拍轨迹开关（每拍写内存，拖拽结束一次性落盘）。</summary>
    public void StartHostTrace(string path, long sessionStartQpc)
        => GhostWindowHost.Shared.StartHostTrace(path, sessionStartQpc);
    public void StopHostTrace() => GhostWindowHost.Shared.StopHostTrace();
#endif

    /// <summary>
    /// 浮层视觉（卡片 + 堆叠背板 + 计数徽标 + 提示条）：自带 <c>Measure</c>/<c>Arrange</c>，尺寸由内容量出。
    /// 小窗移动不重绘内容；只有内容变化（换名称 / 换提示 / 多选项数）才重新布局这个轻量可视树。
    /// </summary>
    private sealed class DragVisualContent : FrameworkElement
    {
        /// <summary>名称列最大宽度（超出省略号；浮层不该盖住半个屏幕）。</summary>
        private const double MaxNameWidth = 220;

        /// <summary>提示条预留宽度（DIP）：拖拽开始就按这个宽度预留，之后换提示文字不再改窗口尺寸。
        /// 实测提示条 129–141 DIP；留足余量，超长名称用省略号收尾（浮层不该随落点名伸缩/撑大）。</summary>
        private const double MaxHintWidth = 240;

        /// <summary>提示条预留高度（DIP）：首次测量空提示得到，之后固定不变。</summary>
        private double _hintReserveHeight;

        /// <summary>提示条当前可见宽度（DIP）：**只增不减**——拖拽中换目标只换文字，药丸不左右伸缩。</summary>
        private double _hintBarWidth;

        /// <summary>提示条最近一次排布的位置（内容坐标 DIP），供独立提示小窗裁剪/定位。</summary>
        internal Rect HintRect { get; private set; }

#if LINKPOCKET_DRAG_DIAGNOSTICS
        /// <summary>抓取点屏幕采样标记。</summary>
        private readonly DrawingVisual _debugMarker = new();
        private readonly DrawingVisual _debugHintMarker = new();
#endif

        /// <summary>投影留白（DIP）：窗口四边都要留够，否则卡片/提示条的 DropShadowEffect 会被窗口边界裁断
        /// （用户截图可见提示条底部阴影是一条直线）。取 16 &gt; 卡片 BlurRadius 12 + ShadowDepth 2。</summary>
        private const double ShadowPad = 16;

        /// <summary>卡片在内容层里的落点（与 <see cref="ArrangeOverride"/> 共用，锚点计算必须读同一组值）。</summary>
        private const double CardLeft = ShadowPad;
        private const double CardTop = ShadowPad;

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
            // MaxWidth/省略号：提示条的可见宽度仍随文字走，但被预留区上限约束——窗口尺寸不变。
            _hintText = new TextBlock
            {
                FontSize = 11.5,
                Foreground = onSurface,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = MaxHintWidth - 18,   // 减边框 2 + 左右内边距 16
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
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
#if LINKPOCKET_DRAG_DIAGNOSTICS
            AddVisualChild(_debugMarker);
            AddVisualChild(_debugHintMarker);
#endif
        }

        /// <summary>设置本次拖动的内容（单项 = 图标 + 名称；多选 = 仅项数徽标 + 叠层）。</summary>
        internal void Show(IReadOnlyList<DragItem> items)
        {
            _hintBarWidth = 0;
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
            // 离屏内容没有布局根传播失效：显式让提示条/内容重测，避免沿用空提示的缓存宽度。
            _hintText.InvalidateMeasure();
            _hint.InvalidateMeasure();
            InvalidateMeasure();
            InvalidateArrange();
            return true;
        }

#if LINKPOCKET_DRAG_DIAGNOSTICS
        /// <summary>当前可见的提示文案，空串 = 提示条隐藏。</summary>
        internal string DebugHintText => _hint.Visibility == Visibility.Visible ? _hintText.Text : string.Empty;
#endif

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
            _hint.Measure(new Size(MaxHintWidth, double.PositiveInfinity));
            if (_hint.Visibility == Visibility.Visible && _hint.DesiredSize.Width > _hintBarWidth)
                _hintBarWidth = _hint.DesiredSize.Width;   // 只增不减：换目标不缩回
            // 尺寸只由卡片与**固定预留**的提示区决定，与当前提示文字无关：
            // 显隐/换字不会触发窗口 resize（拖拽中的 resize 会卡住 OLE 循环）。
            // 宽高 = 卡片/提示区 + 四周投影留白（左右/上下各 ShadowPad）
            var w = Math.Max(_card.DesiredSize.Width, MaxHintWidth + 2) + ShadowPad * 2;
            var h = _card.DesiredSize.Height + HintReserveHeight() + 4 + ShadowPad * 2;
            return new Size(w, h);
        }

        /// <summary>提示条高度（与当前是否显示提示无关）：临时显形量一次空提示，再恢复可见性。</summary>
        private double HintReserveHeight()
        {
            if (_hintReserveHeight > 0) return _hintReserveHeight;
            var visibility = _hint.Visibility;
            _hint.Visibility = Visibility.Visible;
            _hint.Measure(new Size(MaxHintWidth, double.PositiveInfinity));
            _hintReserveHeight = _hint.DesiredSize.Height;
            _hint.Visibility = visibility;
            _hint.InvalidateMeasure();   // 空提示的测量结果只用于取高度，不能留给后续复用
            return _hintReserveHeight;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // 卡片在内容层的位置由抓取锚点反推（宿主把"锚点"摆到指针下）；堆叠背板向左上各让 2/4px；
            // 提示条贴在卡片正下方。
            _stackBack2.Arrange(new Rect(CardLeft, ShadowPad - 4, _card.DesiredSize.Width, _card.DesiredSize.Height));
            _stackBack1.Arrange(new Rect(ShadowPad - 2, ShadowPad - 2, _card.DesiredSize.Width, _card.DesiredSize.Height));
            _card.Arrange(new Rect(CardLeft, CardTop, _card.DesiredSize.Width, _card.DesiredSize.Height));
            var hintTop = CardTop + _card.DesiredSize.Height + 4;
            // 宽度只增不减：目标名变短时药丸保持宽度，避免跨行时左右伸缩。
            // 左边与卡片左边缘对齐（不额外右移），提示条和卡片共用同一条视觉基线。
            var hintWidth = Math.Max(_hintBarWidth, _hint.DesiredSize.Width);
            HintRect = new Rect(CardLeft, hintTop, hintWidth, _hint.DesiredSize.Height);
            _hint.Arrange(HintRect);
            return finalSize;
        }

        protected override Visual GetVisualChild(int index)
        {
#if LINKPOCKET_DRAG_DIAGNOSTICS
            return index switch
            {
                0 => _stackBack2,
                1 => _stackBack1,
                2 => _card,
                3 => _hint,
                4 => _debugMarker,
                _ => _debugHintMarker,
            };
#else
            return index switch
            {
                0 => _stackBack2,
                1 => _stackBack1,
                2 => _card,
                _ => _hint,
            };
#endif
        }

        protected override int VisualChildrenCount
        {
            get
            {
#if LINKPOCKET_DRAG_DIAGNOSTICS
                return 6;
#else
                return 4;
#endif
            }
        }

#if LINKPOCKET_DRAG_DIAGNOSTICS
        /// <summary>在抓取点画一个标记方块，供屏幕采样定位。</summary>
        internal void DebugSetMarker(Point anchor, System.Windows.Media.Color color)
        {
            using var dc = _debugMarker.RenderOpen();
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(color), null,
                new Rect(anchor.X - 2, anchor.Y - 2, 4, 4));
        }

        /// <summary>提示条左上角内的标记，供屏幕采样定位。</summary>
        internal void DebugSetHintMarker(System.Windows.Media.Color color)
        {
            using var dc = _debugHintMarker.RenderOpen();
            if (_hint.Visibility == Visibility.Visible)
            {
                var hint = HintRect;
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(color), null,
                    new Rect(hint.Left + 4, hint.Top + 4, 4, 4));
            }
        }
#endif

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
