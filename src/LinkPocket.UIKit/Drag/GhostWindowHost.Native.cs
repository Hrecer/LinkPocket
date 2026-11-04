using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Media.Imaging;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice;
using D2D1Api = Vortice.Direct2D1.D2D1;
using D3D11Api = Vortice.Direct3D11.D3D11;
using DCompApi = Vortice.DirectComposition.DComp;
using AlphaMode = Vortice.DCommon.AlphaMode;
using Format = Vortice.DXGI.Format;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace LinkPocket.Views;

/// <summary>
/// 拖拽浮层的**独立原生承载窗口**：窗口、设备与整帧渲染都在专用 Win32 线程上，完全不经过 WPF。
///
/// <para><b>位置与内容分两条通路</b>：位置走 <c>IDCompositionVisual.SetOffset + Commit</c>；
/// 内容（卡片 + 提示条）画进**同一张交换链**，只在内容变化时 Present。
/// 位置每帧在 vblank 后固定提交一次，给下一个合成截止留整帧余量。
/// 内容直接画在交换链的离屏场景里，再整块拷到当前后台缓冲（D2D 只绑交换链 0 号缓冲）。</para>
///
/// <para><b>指针</b>：由系统光标平面独立绘制；浮层只按系统光标驱动 visual offset，
/// 不接管、不隐藏系统光标。</para>
///
/// <para><b>设计边界（以下均为刻意取舍，不是未处理的正常路径缺陷）</b></para>
/// <list type="bullet">
/// <item><b>进程级单例、一次初始化</b>：所有页面共用 <see cref="Shared"/> 的同一窗口/线程/设备，
/// 同一时刻只支持一个拖拽浮层；<c>WndProc</c> 必须是静态的并路由回单例。CreateWindow/InitGraphics/
/// ThreadMain 任一失败会置 <c>_failed</c>，本进程不再重试——浮层是可选视觉能力，拖拽与落点不依赖它；
/// 重试会在每次拖拽重复付初始化/等待成本，失败环境通常也不会自愈。</item>
/// <item><b>物理像素管线</b>：UI 线程交付的位图是<b>已经按宿主 DPI 渲染好的物理像素</b>（Pbgra32，
/// 宽 × 4 即 stride）；本类只处理 <c>PixelWidth/PixelHeight</c> 与 <c>GetCursorPos</c> 的屏幕物理坐标，
/// 不再解释 DIP/DPI 元数据。跨显示器拖拽时卡片保持起手时的 DPI 比例（不做拖拽中重建/缩放），
/// 坐标锚点仍逐像素对齐；逐显示器重新缩放不是当前需求。</item>
/// <item><b>定时器与优先级</b>：<c>BeginTimerPeriod/EndTimerPeriod</c> 用 <c>_timerPeriodHeld</c>
/// 做幂等配对（正常路径一次拖拽 = 一次 Begin/End；Show 早退不会多 End，重复 Show 不会重复 Begin）；
/// Begin 放在失败检查与位图物化之后，失败路径不会抬高进程定时器精度。Windows 10 2004+ 的
/// <c>timeBeginPeriod</c> 是进程级而非全局，不会影响其他应用。宿主线程的 Highest 只用于短时拖拽的
/// 唤醒及时性：循环在 waitable timer / MsgWait 上等待，不是忙等；vblank 线程保持 AboveNormal。</item>
/// <item><b>宽 catch 是降级边界</b>：硬件 D3D→WARP、Raw Input→定时唤醒、NoGC→普通 GC、
/// 高精度定时器→MsgWait、可选浮层预热→无浮层，均为明确降级；不吞业务异常。
/// <see cref="ThreadMain"/> 的失败在 Release 只写调试输出，排查浮层缺失时需要调试器或诊断构建。</item>
/// <item><b>待显示位图与首帧</b>：<c>_pendingCard</c> 显式标记“Show 已放入卡片、宿主尚未消费”，
/// 隐藏分支据此保留卡片，不再依赖 <c>_visible</c> 翻转与分支执行顺序；Hide 会显式丢弃未消费卡片。
/// 首帧等待 1.5s 是“拖拽启动不被浮层卡死”的上限，不是成功判定；超时后宿主仍可能稍后补出，
/// 永久失败由 <c>_failed</c>/窗口状态决定，拖拽本身照常继续。</item>
/// </list>
/// </summary>
internal sealed class GhostWindowHost
{
    /// <summary>进程级唯一宿主：所有页面共用同一原生窗口、线程与图形设备；同一时刻只支持一个拖拽浮层。
    /// 需要并行拖拽/多浮层时应重建一套宿主模型，而不是在这里加第二个实例。</summary>
    public static GhostWindowHost Shared { get; } = new();

    private readonly object _gate = new();
    private readonly object _commitGate = new();   // 宿主循环与 vblank 线程共用 DComp 提交
    private readonly object _timerGate = new();    // TimeBegin/EndPeriod 的幂等配对
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private IntPtr _hwnd;
    private volatile bool _failed;

    private volatile bool _visible;
    private volatile bool _autoFollow;
    private volatile bool _forcePosition;
    private volatile bool _forceTopmost;
    private double _anchorX;
    private double _anchorY;

    private int _targetX;
    private int _targetY;
    private int _lastX;
    private int _lastY;

    private GhostBitmap? _pendingBitmap;
    // Show 已放入卡片、宿主尚未消费；与 _pendingBitmap 一起在 _gate 内读写。
    // 隐藏分支靠它区分“Show 刚放卡但 _visible 尚未翻转”与“旧拖拽遗留的卡”，不再依赖执行顺序。
    private bool _pendingCard;
    private GhostBitmap? _pendingHint;
    private int _pendingHintOffsetX;
    private int _pendingHintOffsetY;
    private bool _pendingHintHide;
    private volatile bool _hintVisible;
    private int _hintOffsetX;
    private int _hintOffsetY;
    private int _hintWidth;
    private int _hintHeight;
    private int _debugHintSetupUs;
    private int _debugHintContentUs;
    private int _debugHintStage;
    private int _debugHintError;

    private int _scenePadLeft;
    private int _scenePadTop;

    private ManualResetEventSlim? _showCompletion;
    private int _noGcActive;
    private long _loopCount;
    // 由 _timerGate 保护；true = 已持有一次 timeBeginPeriod(1)。
    private bool _timerPeriodHeld;

    // vblank 节拍：内容更新（换提示/换光标形状/重建场景）只放在 vblank 后的安全窗口里做，
    // 避免上传 + 渲染挤占位置提交的时间窗口。
    private Thread? _vblankThread;
    private volatile bool _vblankStop;
    private long _lastVBlankQpc;
    private long _vblankPeriodQpc;
    private int _lastContentPhaseUs;

#if LINKPOCKET_DRAG_DIAGNOSTICS
    // 宿主线程逐拍环形轨迹（诊断构建）。
    private readonly object _traceGate = new();
    private readonly HostTraceEntry[] _traceEntries = new HostTraceEntry[1 << 16];
    private int _traceStart;
    private int _traceCount;
    private volatile bool _traceEnabled;
    private string? _tracePath;
    private long _traceSessionStart;
    private long _lastTraceQpc;
    private long _rawInputCount;
#endif
    private long _lastAppliedChangeQpc;
    private int _lastSetPosUs;
    private int _lastBitmapUs;
    private int _lastRenderUs;
    private int _lastHintUploadUs;
#if LINKPOCKET_DRAG_DIAGNOSTICS
    private bool _lastBitmapApplied;
#endif

    // 图形：全部只在专用线程上用。
    private ID3D11Device? _d3dDevice;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2dContext;
    private IDCompositionDevice? _dcomp;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _rootVisual;
    private IDCompositionVisual? _contentVisual;
    private IDXGISwapChain1? _swapchain;
    private IDXGISwapChain3? _swapchain3;
    private ID3D11Texture2D? _sceneTexture;
    private ID2D1Bitmap1? _sceneTarget;
    private ID3D11DeviceContext? _d3dContext;
    private ID2D1Bitmap? _cardBitmap;
    private ID2D1Bitmap? _hintBitmap;
    private int _virtualLeft;
    private int _virtualTop;
    private int _virtualWidth;
    private int _virtualHeight;
    private int _sceneWidth;
    private int _sceneHeight;
    private int _cardWidth;
    private int _cardHeight;
    private volatile bool _contentDirty;
    private bool _firstFramePresented;
    private bool _windowShown;

    private GhostWindowHost()
    {
    }

    /// <summary>当前浮层 HWND（尚未创建/已隐藏时为 0）。跨线程读取。</summary>
    public IntPtr Hwnd => Interlocked.CompareExchange(ref _hwnd, IntPtr.Zero, IntPtr.Zero);

    /// <summary>最近一次真正渲染的卡片屏幕物理坐标（诊断/探针用）。</summary>
    public int LastX => Volatile.Read(ref _lastX);
    public int LastY => Volatile.Read(ref _lastY);
    public int DebugTargetX => Volatile.Read(ref _targetX);
    public int DebugTargetY => Volatile.Read(ref _targetY);

    internal int TargetX => Volatile.Read(ref _targetX);
    internal int TargetY => Volatile.Read(ref _targetY);

    public bool IsVisible => _visible;
    public bool IsAutoFollowing => _autoFollow;

#if LINKPOCKET_DRAG_DIAGNOSTICS
    /// <summary>拖拽期间是否成功进入 NoGC 区域。</summary>
    public bool DebugNoGcActive => Volatile.Read(ref _noGcActive) != 0;

    public long DebugLoopCount => Volatile.Read(ref _loopCount);

    public double AnchorX => _anchorX;
    public double AnchorY => _anchorY;

    public int DebugBitmapWidth => Volatile.Read(ref _cardWidth);
    public int DebugBitmapHeight => Volatile.Read(ref _cardHeight);

    public bool DebugHintVisible => _hintVisible;
    public bool DebugHintWindowShown => _hintVisible;
    public IntPtr DebugHintHwnd => Hwnd;
    public int DebugHintScreenX => Volatile.Read(ref _lastX) + Volatile.Read(ref _hintOffsetX);
    public int DebugHintScreenY => Volatile.Read(ref _lastY) + Volatile.Read(ref _hintOffsetY);
    public int DebugHintWidth => Volatile.Read(ref _hintWidth);
    public int DebugHintHeight => Volatile.Read(ref _hintHeight);
    public int DebugHintSetupUs => Volatile.Read(ref _debugHintSetupUs);
    public int DebugHintWindowUs => 0;
    public int DebugHintDeviceUs => 0;
    public int DebugHintTargetUs => 0;
    public int DebugHintContentUs => Volatile.Read(ref _debugHintContentUs);
    public int DebugHintStage => Volatile.Read(ref _debugHintStage);
    public int DebugHintError => Volatile.Read(ref _debugHintError);

    public bool TryGetHostSampleAt(long qpc,
        out int cursorX, out int cursorY, out bool hasCursor,
        out int appliedX, out int appliedY, out long appliedSinceQpc)
    {
        cursorX = 0;
        cursorY = 0;
        appliedX = 0;
        appliedY = 0;
        appliedSinceQpc = 0;
        hasCursor = false;
        lock (_traceGate)
        {
            if (_traceCount == 0) return false;
            var entries = _traceEntries;
            var start = _traceStart;
            var count = _traceCount;
            var low = 0;
            var high = count - 1;
            while (low < high)
            {
                var mid = (low + high + 1) >> 1;
                if (entries[(start + mid) % entries.Length].Qpc <= qpc) low = mid;
                else high = mid - 1;
            }

            var entry = entries[(start + low) % entries.Length];
            cursorX = entry.CursorX;
            cursorY = entry.CursorY;
            appliedX = entry.AppliedX;
            appliedY = entry.AppliedY;
            appliedSinceQpc = entry.AppliedSinceQpc;
            hasCursor = (entry.Flags & 1) != 0;
            return true;
        }
    }

    public void StartHostTrace(string path, long sessionStartQpc)
    {
        lock (_traceGate)
        {
            _traceStart = 0;
            _traceCount = 0;
            _lastTraceQpc = 0;
            _lastAppliedChangeQpc = 0;
            _traceSessionStart = sessionStartQpc;
            _tracePath = path;
            _lastSetPosUs = 0;
            _lastBitmapUs = 0;
            _lastBitmapApplied = false;
            _traceEnabled = true;
        }
    }

    public void StopHostTrace()
    {
        string? path;
        HostTraceEntry[] entries;
        int start;
        int count;
        long sessionStart;
        lock (_traceGate)
        {
            _traceEnabled = false;
            path = _tracePath;
            entries = _traceEntries;
            start = _traceStart;
            count = _traceCount;
            sessionStart = _traceSessionStart;
            _tracePath = null;
            _traceStart = 0;
            _traceCount = 0;
        }
        if (path == null) return;

        try
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\r\n" };
            writer.WriteLine("# tMs,curX,curY,tgtX,tgtY,actX,actY,gapUs,setUs,bitmapUs,renderUs,hintUs,shape,vbPhaseUs,flags");
            for (var i = 0; i < count; i++)
            {
                var e = entries[(start + i) % entries.Length];
                var tMs = (e.Qpc - sessionStart) * 1000.0 / Stopwatch.Frequency;
                writer.Write(tMs.ToString("F3", CultureInfo.InvariantCulture)); writer.Write(',');
                writer.Write(e.CursorX); writer.Write(',');
                writer.Write(e.CursorY); writer.Write(',');
                writer.Write(e.TargetX); writer.Write(',');
                writer.Write(e.TargetY); writer.Write(',');
                writer.Write(e.AppliedX); writer.Write(',');
                writer.Write(e.AppliedY); writer.Write(',');
                writer.Write(e.LoopGapUs); writer.Write(',');
                writer.Write(e.SetPosUs); writer.Write(',');
                writer.Write(e.BitmapUs); writer.Write(',');
                writer.Write(e.RenderUs); writer.Write(',');
                writer.Write(e.HintUs); writer.Write(',');
                writer.Write(0); writer.Write(',');
                writer.Write(e.ContentPhaseUs); writer.Write(',');
                writer.WriteLine(e.Flags);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"drag host trace write failed: {ex}");
        }
    }
#endif

    public void Prewarm()
    {
        try { EnsureThreadStarted(); }
        catch (Exception)
        {
            /* 预热失败不影响拖拽：浮层只是可选视觉能力，拖拽与落点仍走原路径。
               注意：这里**不是**“首次 Show 时再建一次线程”——_thread 一旦创建就保留；
               CreateWindow/InitGraphics/ThreadMain 失败会置 _failed，本进程不再尝试（见类头边界说明）。 */
        }
    }

    private void EnsureStarted()
    {
        EnsureThreadStarted();
        if (!_ready.IsSet) _ready.Wait(3000);
    }

    private void EnsureThreadStarted()
    {
        // _thread 是“已尝试过”标记，不是“仍存活”标记：线程退出/初始化失败后也不清空。
        // 这是刻意的单次初始化（避免每次拖拽重复 3s 等待与失败初始化）；失败后 _failed 会让 Show 立即返回。
        if (_thread != null) return;
        lock (_gate)
        {
            if (_thread != null) return;
            var thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "LinkPocket.DragGhost",
            };
            _thread = thread;
            thread.Start();
        }
    }

    private void ThreadMain()
    {
        try
        {
            // Highest 是拖拽手感的一部分：宿主只做每帧一次 offset 提交与按需 Present，
            // 且循环在 waitable timer/MsgWait 上等待（不是忙等）。未经新的 UiBench/诊断对比，
            // 不要为了“看起来更温和”降到 AboveNormal——唤醒及时性会变，拖拽跟手性基线也会变。
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            CreateWindow();
            InitGraphics();
            StartVBlankThread();
            _ready.Set();
            RunLoop();
        }
        catch (Exception ex)
        {
            // 宽 catch 是可选浮层的最后降级边界：窗口/图形/窗口循环任一初始化失败，
            // 只关闭浮层，绝不把拖拽或主窗口带崩。失败一次性，不重试（见 EnsureThreadStarted）。
            // Release 下 Debug.WriteLine 通常不可见；排查“浮层不见了”请用调试器或诊断构建。
            _failed = true;
            _ready.Set();
            Debug.WriteLine($"drag ghost host unavailable: {ex}");
        }
    }

    private void CreateWindow()
    {
        var instance = GetModuleHandle(null);
        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = instance,
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = WindowClassName,
            hIconSm = IntPtr.Zero,
        };
        RegisterClassEx(ref windowClass);

        _virtualLeft = GetSystemMetrics(SmXVirtualScreen);
        _virtualTop = GetSystemMetrics(SmYVirtualScreen);
        _virtualWidth = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        _virtualHeight = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));

        // 必须同时带 WS_EX_LAYERED：非分层的全屏 DComp 窗口会被 WindowFromPoint 命中，
        // 而 OLE 拖拽用它找落点目标——浮层没有 IDropTarget，整块屏幕的落点会全部失效。
        // 分层属性让 WindowFromPoint 跳过这个窗口，DComp 内容仍照常合成（alpha 保持 255）。
        var exStyle = WsExNoRedirectionBitmap | WsExTransparent | WsExNoActivate |
                      WsExToolWindow | WsExTopmost | WsExLayered;
        _hwnd = CreateWindowEx(
            exStyle, WindowClassName, string.Empty, WsPopup,
            _virtualLeft, _virtualTop, _virtualWidth, _virtualHeight,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed");
        SetLayeredWindowAttributes(_hwnd, 0, 255, LwaAlpha);

        var device = new RawInputDevice
        {
            usUsagePage = 1,
            usUsage = 2,
            dwFlags = RidevInputSink,
            hwndTarget = _hwnd,
        };
        try
        {
            RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RawInputDevice>());
        }
        catch (Exception)
        {
            // Raw Input 只用于唤醒，不是拖拽/落点的前置条件；注册失败时高精度定时器仍按 0.5ms
            // 驱动宿主循环。宽 catch 覆盖 P/Invoke 可用性差异（缺 DLL/入口点等），不影响拖拽本身。
        }
    }

    private void InitGraphics()
    {
        try
        {
            _d3dDevice = D3D11Api.D3D11CreateDevice(
                DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0 });
        }
        catch (Exception)
        {
            // 硬件 D3D 不可用 → 有意的 WARP 软件回退。两次都失败时异常交给 ThreadMain 的
            // 降级边界：只关闭浮层，拖拽/落点继续走 WPF/OLE 原路径。
            _d3dDevice = D3D11Api.D3D11CreateDevice(
                DriverType.Warp, DeviceCreationFlags.BgraSupport,
                new[] { Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0 });
        }

        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        _d2dFactory = D2D1Api.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded, DebugLevel.None);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _d2dContext.SetDpi(96, 96);
        _d2dContext.AntialiasMode = AntialiasMode.Aliased;
        _d3dContext = _d3dDevice.ImmediateContext;

        _dcomp = DCompApi.DCompositionCreateDevice2<IDCompositionDevice>(_d2dDevice);
        _dcomp.CreateTargetForHwnd(_hwnd, true, out var target).CheckError();
        _target = target;
        _rootVisual = _dcomp.CreateVisual();
        _target.SetRoot(_rootVisual).CheckError();
        // 内容 visual 常驻、只在初始化时挂一次：位置走 visual offset（合成时拉取，
        // 指针与卡片同帧），内容走交换链 Present（卡片与提示条在同一张缓冲里）。
        _contentVisual = _dcomp.CreateVisual();
        _rootVisual.AddVisual(_contentVisual, true, null);
        _dcomp.Commit().CheckError();
    }

    /// <summary>
    /// 按卡片位图重建场景（场景原点 = 卡片原点）。
    /// 返回是否真的重建（重建后位置 offset 必须重新提交）。
    /// </summary>
    private bool EnsureScene(int contentWidth, int contentHeight,
        int padLeft, int padTop, int padRight, int padBottom)
    {
        var width = contentWidth + padLeft + padRight;
        var height = contentHeight + padTop + padBottom;
        if (width <= 0 || height <= 0) return false;
        if (_swapchain != null && _sceneWidth == width && _sceneHeight == height &&
            _scenePadLeft == padLeft && _scenePadTop == padTop)
            return false;
        if (_d3dDevice == null || _d2dContext == null || _d3dContext == null ||
            _contentVisual == null || _dcomp == null) return false;

        // 先建新、再切、最后释放旧：D2D target 不能指向已释放的纹理。
        using var dxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        var desc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Vortice.DXGI.Usage.RenderTargetOutput,
            BufferCount = 2,
            // Stretch 只影响缩放方式：本地坐标 1:1 拷贝，不缩放。
            Scaling = Vortice.DXGI.Scaling.Stretch,
            SwapEffect = Vortice.DXGI.SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
            Flags = Vortice.DXGI.SwapChainFlags.None,
        };
        var swapchain = dxgiFactory.CreateSwapChainForComposition(_d3dDevice, desc, null);
        var swapchain3 = swapchain.QueryInterface<IDXGISwapChain3>();
        var sceneTexture = _d3dDevice.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        // D2D 只能把交换链 0 号缓冲绑成 target，1 号会 E_INVALIDARG；
        // 因此所有绘制进一张常驻离屏纹理，再整块拷到当前后台缓冲。
        ID2D1Bitmap1 sceneTarget;
        using (var sceneSurface = sceneTexture.QueryInterface<IDXGISurface>())
        {
            sceneTarget = _d2dContext.CreateBitmapFromDxgiSurface(sceneSurface,
                new BitmapProperties1(
                    new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
                    96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        }
        _d2dContext.Target = sceneTarget;
        _d2dContext.BeginDraw();
        _d2dContext.Clear(new Color4(0, 0, 0, 0));
        _d2dContext.EndDraw();
        for (uint i = 0; i < 2; i++)
        {
            using var buffer = swapchain.GetBuffer<ID3D11Texture2D>(i);
            _d3dContext.CopySubresourceRegion(buffer, 0, 0, 0, 0, sceneTexture, 0,
                new Box(0, 0, 0, width, height, 1));
        }

        lock (_commitGate)
        {
            _contentVisual.SetContent(swapchain).CheckError();
            _dcomp.Commit().CheckError();
        }

        _sceneTarget?.Dispose();
        _sceneTexture?.Dispose();
        _swapchain3?.Dispose();
        _swapchain?.Dispose();
        _sceneTarget = sceneTarget;
        _sceneTexture = sceneTexture;
        _swapchain3 = swapchain3;
        _swapchain = swapchain;
        _sceneWidth = width;
        _sceneHeight = height;
        _scenePadLeft = padLeft;
        _scenePadTop = padTop;
        _contentDirty = true;
        _firstFramePresented = false;
        return true;
    }

    /// <summary>按卡片位图重建场景。</summary>
    private bool UpdateScene()
    {
        if (_cardWidth <= 0 || _cardHeight <= 0) return false;
        return EnsureScene(_cardWidth, _cardHeight, 0, 0, 0, 0);
    }

    public void Show(BitmapSource bitmap, double anchorX, double anchorY, bool autoFollow = false)
    {
        EnsureStarted();
        if (_failed || Hwnd == IntPtr.Zero) return;
        _anchorX = anchorX;
        _anchorY = anchorY;
        _autoFollow = autoFollow;
        BeginNoGcRegion();

        if (GetCursorPos(out var cursor))
        {
            Volatile.Write(ref _targetX, (int)Math.Round(cursor.X - anchorX));
            Volatile.Write(ref _targetY, (int)Math.Round(cursor.Y - anchorY));
        }
        var ghost = GhostBitmap.From(bitmap);
        if (ghost == null) return;
        lock (_gate)
        {
            _pendingBitmap = ghost;
            _pendingCard = true;   // 显式标记：即使隐藏分支先于 _visible 翻转执行，也不能丢掉这张卡
            _pendingHint = null;
            _pendingHintHide = true;
            _hintVisible = false;
        }
        _firstFramePresented = false;
        _contentDirty = true;

        // 1.5s 是“拖拽启动不被可选浮层卡死”的上限，不是成功判定：超时只表示首帧没在期限内出现，
        // 宿主稍后补出仍有效；永久失败由 _failed/窗口状态决定。调用方不等待浮层真正可见，
        // 拖拽/落点不依赖它（_shown 仍为 true，后续 hint/position 在失败宿主上是无害 no-op）。
        var completion = new ManualResetEventSlim(false);
        _showCompletion = completion;
        _visible = true;
        _forcePosition = true;
        _forceTopmost = true;

        // 正常路径一次拖拽 = Show 一次 Begin、Hide 一次 End。BeginTimerPeriod 用 _timerPeriodHeld
        // 做幂等配对：Show 早退不会留下未配对的 End，重复 Show 也不会重复提升精度。
        // Begin 放在 _failed/位图检查之后：这些失败路径不会抬高进程定时器精度。
        BeginTimerPeriod();
        try
        {
            PostWake();
            completion.Wait(1500);
        }
        catch
        {
            // Show 自身异常后未必有人再调 Hide；在这里释放，保证 Begin/End 不悬空。
            EndTimerPeriod();
            throw;
        }
    }

    public void SetHint(GhostBitmap? bitmap, int offsetX, int offsetY)
    {
        if (_failed) return;
        var start = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _pendingHint = bitmap;
            _pendingHintOffsetX = offsetX;
            _pendingHintOffsetY = offsetY;
            _pendingHintHide = bitmap == null;
        }
        Volatile.Write(ref _debugHintSetupUs,
            (int)Math.Min(int.MaxValue,
                (Stopwatch.GetTimestamp() - start) * 1_000_000L / Stopwatch.Frequency));
        PostWake();
    }

    public void HideHint() => SetHint(null, 0, 0);

    public void SetAutoFollow(bool enabled)
    {
        if (_failed) return;
        _autoFollow = enabled;
        PostWake();
    }

    public void SetExplicitPosition(int x, int y)
    {
        if (_failed) return;
        Volatile.Write(ref _targetX, x);
        Volatile.Write(ref _targetY, y);
        // 常规跟随由 vblank 线程每帧提交；显式摆位没有自动跟随，必须让宿主循环立即提交一次。
        _forcePosition = true;
        PostWake();
    }

    public void Hide()
    {
        _autoFollow = false;
        _visible = false;
        _forcePosition = true;
        // 未消费的卡片属于本次拖拽；显式 Hide 后不再需要，释放位图并清掉待显示标记。
        lock (_gate)
        {
            _pendingBitmap = null;
            _pendingCard = false;
        }
        // 与 Show 的 Begin 幂等配对；Show 早退（_failed/无位图）时没有持有精度，这里不会多 End。
        // End 不能放进 Show 的 finally：定时器精度必须覆盖整个拖拽，直到 Hide/Detach。
        EndTimerPeriod();
        EndNoGcRegion();
        PostWake();
    }

    /// <summary>幂等 Begin：重复 Show 不会叠加；与 <see cref="EndTimerPeriod"/> 成对。</summary>
    private void BeginTimerPeriod()
    {
        lock (_timerGate)
        {
            if (_timerPeriodHeld) return;
            TimeBeginPeriod(1);
            _timerPeriodHeld = true;
        }
    }

    /// <summary>幂等 End：没有对应 Begin 时为空操作；Show 早退/重复 Hide 不会造成反向失衡。</summary>
    private void EndTimerPeriod()
    {
        lock (_timerGate)
        {
            if (!_timerPeriodHeld) return;
            TimeEndPeriod(1);
            _timerPeriodHeld = false;
        }
    }

    private void BeginNoGcRegion()
    {
        if (Interlocked.CompareExchange(ref _noGcActive, 1, 0) != 0) return;
        try
        {
            if (!GC.TryStartNoGCRegion(192L * 1024 * 1024)) Volatile.Write(ref _noGcActive, 0);
        }
        catch (Exception)
        {
            // NoGC 只是降低拖拽期间 GC 停顿的可选项；进入失败就退回普通 GC，不影响拖拽/落点。
            Volatile.Write(ref _noGcActive, 0);
        }
    }

    private void EndNoGcRegion()
    {
        if (Interlocked.Exchange(ref _noGcActive, 0) == 0) return;
        try { GC.EndNoGCRegion(); }
        // 未成功进入 NoGC 时 EndNoGCRegion 会抛，属正常收尾。
        catch (Exception) { /* 正常收尾：没有需要结束的 NoGC 区域 */ }
    }

    private void PostWake()
    {
        var hwnd = Hwnd;
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WmAppWake, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>跟踪 DWM 合成节拍（DwmFlush 每帧唤醒），给内容更新提供“vblank 后多久”的相位。</summary>
    private void StartVBlankThread()
    {
        if (_vblankThread != null) return;
        var thread = new Thread(VBlankLoop)
        {
            IsBackground = true,
            Name = "LinkPocket.DragVBlank",
            Priority = ThreadPriority.AboveNormal,
        };
        _vblankThread = thread;
        thread.Start();
    }

    private void VBlankLoop()
    {
        var last = 0L;
        while (!_vblankStop)
        {
            if (DwmFlush() != 0)
            {
                Thread.Sleep(2);   // DWM 未开/不可用：退化成轮询
                continue;
            }
            var now = Stopwatch.GetTimestamp();
            var period = 0L;
            if (last != 0)
            {
                var delta = now - last;
                if (delta > 0)
                {
                    Volatile.Write(ref _vblankPeriodQpc, delta);
                    period = delta;
                }
            }
            last = now;
            Volatile.Write(ref _lastVBlankQpc, now);

            if (!_visible || !_firstFramePresented || period <= 0) continue;

            // 位置每帧只提交一次，固定在 vblank 之后：这次提交到下一个合成截止还有整整一帧余量。
            LatchPosition();
        }
    }

    /// <summary>
    /// 每帧固定相位的位置提交：vblank 后读一次系统光标并提交 visual offset（与宿主循环串行化提交）。
    /// </summary>
    private void LatchPosition()
    {
        // 只服务真实拖拽的自动跟随；显式摆位（探针/自检）不能被系统光标覆盖。
        if (_vblankStop || !_visible || !_firstFramePresented || !_autoFollow) return;
        if (!GetCursorPos(out var cursor)) return;
        var sampleQpc = Stopwatch.GetTimestamp();
        var x = (int)Math.Round(cursor.X - _anchorX);
        var y = (int)Math.Round(cursor.Y - _anchorY);
        Volatile.Write(ref _targetX, x);
        Volatile.Write(ref _targetY, y);
        lock (_commitGate)
        {
            if (_vblankStop || !_visible || !_firstFramePresented || _dcomp == null || _contentVisual == null) return;
            ApplyOffset(x, y);
            _dcomp.Commit().CheckError();
            Volatile.Write(ref _lastX, x);
            Volatile.Write(ref _lastY, y);
            _lastAppliedChangeQpc = sampleQpc;
        }
    }

    /// <summary>
    /// 内容更新（提示条/光标形状/场景重建）只允许在 vblank 后的一段窗口内做：
    /// 这段工作有 0.3–1.5ms，放在帧首能在下一个合成截止前完成。
    /// </summary>
    private bool ContentWindowOpen()
    {
        var last = Volatile.Read(ref _lastVBlankQpc);
        if (last == 0) return true;   // 相位未知（刚启动）：不阻塞首帧
        var since = Stopwatch.GetTimestamp() - last;
        if (since < 0) return false;
        var phaseUs = since * 1_000_000L / Stopwatch.Frequency;
        var period = Volatile.Read(ref _vblankPeriodQpc);
        var periodUs = period > 0 ? period * 1_000_000L / Stopwatch.Frequency : 4167;
        var windowUs = Math.Clamp(periodUs / 3, 300, 1500);
        return phaseUs <= windowUs;
    }

    private int CurrentContentPhaseUs()
    {
        var last = Volatile.Read(ref _lastVBlankQpc);
        if (last == 0) return -1;
        var since = Stopwatch.GetTimestamp() - last;
        return since < 0 ? -1 : (int)Math.Min(int.MaxValue, since * 1_000_000L / Stopwatch.Frequency);
    }

    private void OnRawInput()
    {
        if (!_visible || !_autoFollow) return;
#if LINKPOCKET_DRAG_DIAGNOSTICS
        Interlocked.Increment(ref _rawInputCount);
#endif
        if (GetCursorPos(out var cursor))
        {
            Volatile.Write(ref _targetX, (int)Math.Round(cursor.X - _anchorX));
            Volatile.Write(ref _targetY, (int)Math.Round(cursor.Y - _anchorY));
        }
    }

    /// <summary>
    /// 窗口类回调必须是静态方法，并由静态委托长期持有（RegisterClassEx 不接管托管回调的生命周期）。
    /// 进程只有一个 <see cref="Shared"/> 宿主，因此直接路由到它的原始输入处理；
    /// 多浮层/并行拖拽不在当前模型内——那需要每个宿主独立建窗并拥有自己的回调上下文。
    /// </summary>
    private static IntPtr RawInputWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmInput)
        {
            Shared.OnRawInput();
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }
        if (msg == WmNcHitTest) return new IntPtr(-1);
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void RunLoop()
    {
        IntPtr highResolutionTimer = IntPtr.Zero;
        try
        {
            highResolutionTimer = CreateWaitableTimerEx(IntPtr.Zero, null,
                CreateWaitableTimerHighResolution, TimerAllAccess);
        }
        catch (Exception)
        {
            // 拿不到高精度 waitable timer 时退化为带超时的 MsgWait；拖拽仍可用，只是唤醒精度下降。
        }

        try
        {
            while (true)
            {
                if (highResolutionTimer != IntPtr.Zero && _visible)
                {
                    var due = -5000L;
                    SetWaitableTimer(highResolutionTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                    var timerHandle = highResolutionTimer;
                    MsgWaitForMultipleObjects(1, ref timerHandle, false, Infinite, QsAllInput);
                }
                else
                {
                    MsgWaitForMultipleObjects(0, IntPtr.Zero, false, _visible ? 1u : 10u, QsAllInput);
                }

                Interlocked.Increment(ref _loopCount);
                while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
                {
                    if (message.message == WmQuit) return;
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }

                if (_visible)
                {
                    _lastSetPosUs = 0;
                    _lastBitmapUs = 0;
                    _lastRenderUs = 0;
                    _lastHintUploadUs = 0;
                    _lastContentPhaseUs = -1;
#if LINKPOCKET_DRAG_DIAGNOSTICS
                    _lastBitmapApplied = false;
#endif
#if LINKPOCKET_DRAG_DIAGNOSTICS
                    var traceCursorX = 0;
                    var traceCursorY = 0;
                    var traceHasCursor = false;
#endif

                    if (_autoFollow && GetCursorPos(out var cursor))
                    {
#if LINKPOCKET_DRAG_DIAGNOSTICS
                        traceCursorX = cursor.X;
                        traceCursorY = cursor.Y;
                        traceHasCursor = true;
#endif
                        Volatile.Write(ref _targetX, (int)Math.Round(cursor.X - _anchorX));
                        Volatile.Write(ref _targetY, (int)Math.Round(cursor.Y - _anchorY));
                    }

                    // 内容工作（提示条、场景重建、Present）只在 vblank 后的安全窗口里做：
                    // 位置提交每拍照常，与位图上传/渲染互不阻塞。
                    var contentWindow = ContentWindowOpen();
                    if (contentWindow) _lastContentPhaseUs = CurrentContentPhaseUs();
                    var sceneRebuilt = false;

                    if (_forceTopmost)
                    {
                        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0,
                            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
                        _forceTopmost = false;
                    }

                    var x = Volatile.Read(ref _targetX);
                    var y = Volatile.Read(ref _targetY);

                    // 常规跟随由 VBlank 线程每帧固定提交一次；宿主这里只处理显式摆位（首帧/场景重建）。
                    if (_forcePosition) CommitPosition(x, y);

                    if (contentWindow)
                    {
                        GhostBitmap? pending;
                        GhostBitmap? pendingHint;
                        bool hintHide;
                        int hintOffX, hintOffY;
                        lock (_gate)
                        {
                            pending = _pendingBitmap;
                            _pendingBitmap = null;
                            _pendingCard = false;   // 宿主已消费：隐藏分支/后续 Show 不再把它当待显示
                            pendingHint = _pendingHint;
                            _pendingHint = null;
                            hintOffX = _pendingHintOffsetX;
                            hintOffY = _pendingHintOffsetY;
                            hintHide = _pendingHintHide;
                        }
                        if (pending != null)
                        {
                            if (UploadCard(pending)) sceneRebuilt = true;
#if LINKPOCKET_DRAG_DIAGNOSTICS
                            _lastBitmapApplied = true;
#endif
                        }
                        if (pendingHint != null && !hintHide)
                        {
                            var start = Stopwatch.GetTimestamp();
                            UploadHint(pendingHint);
                            _lastHintUploadUs = (int)Math.Min(int.MaxValue,
                                (Stopwatch.GetTimestamp() - start) * 1_000_000L / Stopwatch.Frequency);
                            Volatile.Write(ref _hintOffsetX, hintOffX);
                            Volatile.Write(ref _hintOffsetY, hintOffY);
                            _hintVisible = true;
                            Volatile.Write(ref _debugHintContentUs,
                                (int)Math.Min(int.MaxValue,
                                    (Stopwatch.GetTimestamp() - start) * 1_000_000L / Stopwatch.Frequency));
                            Volatile.Write(ref _debugHintStage, 6);
                            Volatile.Write(ref _debugHintError, 0);
                        }
                        else if (hintHide && _hintVisible)
                        {
                            _hintVisible = false;
                            _contentDirty = true;
                        }

                        // 场景重建改了视觉原点（卡片在场景里的落白），offset 必须在同一拍按新留白重算。
                        if (sceneRebuilt) CommitPosition(x, y);

                        var renderStart = Stopwatch.GetTimestamp();
                        var rendered = (_contentDirty || !_firstFramePresented) && RenderScene();
                        _lastRenderUs = (int)Math.Min(int.MaxValue,
                            (Stopwatch.GetTimestamp() - renderStart) * 1_000_000L / Stopwatch.Frequency);
                        if (rendered)
                        {
                            _contentDirty = false;
                            _firstFramePresented = true;
                        }
                    }

                    if (!_windowShown && _firstFramePresented)
                    {
                        ShowWindow(_hwnd, SwShowNoActivate);
                        _windowShown = true;
                        Interlocked.Exchange(ref _showCompletion, null)?.Set();
                    }

#if LINKPOCKET_DRAG_DIAGNOSTICS
                    TraceTick(Stopwatch.GetTimestamp(), traceCursorX, traceCursorY, traceHasCursor);
#endif
                }
                else
                {
                    if (_windowShown)
                    {
                        ShowWindow(_hwnd, SwHide);
                        _windowShown = false;
                    }
                    _firstFramePresented = false;
                    _contentDirty = true;
                    lock (_gate)
                    {
                        _pendingHint = null;
                        _pendingHintHide = true;
                        _hintVisible = false;
                        // 提示条随隐藏清掉；卡片只在“没有待显示请求”时释放。
                        // _pendingCard 显式表达 Show 已放卡、_visible 尚未翻转的窗口，
                        // 不再依赖本分支与 Show 的执行顺序（旧的隐式约定是重构雷区）。
                        if (!_pendingCard) _pendingBitmap = null;
                    }
                }
            }
        }
        finally
        {
            _vblankStop = true;
            if (highResolutionTimer != IntPtr.Zero) CloseHandle(highResolutionTimer);
            DisposeGraphics();
        }
    }

    private bool UploadCard(GhostBitmap pending)
    {
        if (_d2dContext == null) return false;
        var start = Stopwatch.GetTimestamp();
        _cardBitmap?.Dispose();
        _cardBitmap = CreateBitmap(_d2dContext, pending);
        Volatile.Write(ref _cardWidth, pending.Width);
        Volatile.Write(ref _cardHeight, pending.Height);
        var rebuilt = UpdateScene();
        _contentDirty = true;
        _lastBitmapUs = (int)Math.Min(int.MaxValue,
            (Stopwatch.GetTimestamp() - start) * 1_000_000L / Stopwatch.Frequency);
        return rebuilt;
    }

    private void UploadHint(GhostBitmap pending)
    {
        if (_d2dContext == null) return;
        _hintBitmap?.Dispose();
        _hintBitmap = CreateBitmap(_d2dContext, pending);
        Volatile.Write(ref _hintWidth, pending.Width);
        Volatile.Write(ref _hintHeight, pending.Height);
        _contentDirty = true;
    }

    private static ID2D1Bitmap CreateBitmap(ID2D1DeviceContext context, GhostBitmap pending)
    {
        var handle = GCHandle.Alloc(pending.Pixels, GCHandleType.Pinned);
        try
        {
            return context.CreateBitmap(
                new SizeI(pending.Width, pending.Height),
                handle.AddrOfPinnedObject(),
                (uint)(pending.Width * 4),
                new BitmapProperties1(
                    new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
                    96, 96, BitmapOptions.None));
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>提交一次卡片位置（卡片屏幕坐标）：offset = 卡片位置 − 场景留白 − 虚拟屏原点。</summary>
    private void CommitPosition(int x, int y)
    {
        var moveStart = Stopwatch.GetTimestamp();
        _forcePosition = false;
        lock (_commitGate)
        {
            ApplyOffset(x, y);
            _dcomp?.Commit().CheckError();
            Volatile.Write(ref _lastX, x);
            Volatile.Write(ref _lastY, y);
            _lastAppliedChangeQpc = Stopwatch.GetTimestamp();
        }
        _lastSetPosUs = (int)Math.Min(int.MaxValue,
            (Stopwatch.GetTimestamp() - moveStart) * 1_000_000L / Stopwatch.Frequency);
    }

    /// <summary>
    /// 卡片在屏幕上的落点 = 内容 visual 的 offset。offset 是“合成时读取”的属性：
    /// 位置由 vblank 线程每帧提交一次，合成器读取当拍位置。
    /// </summary>
    private void ApplyOffset(int cardX, int cardY)
    {
        _contentVisual?.SetOffsetX(cardX - _scenePadLeft - _virtualLeft);
        _contentVisual?.SetOffsetY(cardY - _scenePadTop - _virtualTop);
    }

    /// <summary>
    /// 把卡片与提示条画进同一张后台缓冲并 Present（本地坐标，原点 = 卡片左上角）。
    /// Present 只是“内容”通路：位置不依赖它，所以 DoNotWait 失败只需下一拍重试，不会丢位置。
    /// </summary>
    private bool RenderScene()
    {
        if (_d2dContext == null || _swapchain == null || _swapchain3 == null || _cardBitmap == null)
            return false;
        if (_sceneTarget == null || _sceneTexture == null || _d3dContext == null) return false;

        var bufferIndex = (int)_swapchain3.CurrentBackBufferIndex;

        _d2dContext.BeginDraw();
        _d2dContext.Target = _sceneTarget;
        _d2dContext.Clear(new Color4(0, 0, 0, 0));
        DrawAt(_cardBitmap, _scenePadLeft, _scenePadTop, _cardWidth, _cardHeight);
        if (_hintVisible && _hintBitmap != null)
        {
            DrawAt(_hintBitmap,
                _scenePadLeft + Volatile.Read(ref _hintOffsetX),
                _scenePadTop + Volatile.Read(ref _hintOffsetY),
                _hintWidth, _hintHeight);
        }
        _d2dContext.EndDraw().CheckError();

        // 离屏纹理整块拷到当前后台缓冲（D2D 不直接碰交换链缓冲）。
        using (var buffer = _swapchain3.GetBuffer<ID3D11Texture2D>((uint)bufferIndex))
        {
            _d3dContext.CopySubresourceRegion(buffer, 0, 0, 0, 0, _sceneTexture, 0,
                new Box(0, 0, 0, _sceneWidth, _sceneHeight, 1));
        }

        var result = _swapchain3.Present(0, Vortice.DXGI.PresentFlags.DoNotWait);
        if (result.Failure) return false;
        // 前台队列满时 Present 返回成功状态但不提交，必须下一拍重试，否则内容会永远停在旧帧。
        if (result.Code == Vortice.DXGI.ResultCode.WasStillDrawing.Code) return false;
        return true;
    }

    private void DrawAt(ID2D1Bitmap bitmap, int left, int top, int width, int height)
    {
        if (_d2dContext == null) return;
        _d2dContext.DrawBitmap(bitmap,
            new RawRectF(left, top, left + width, top + height),
            1f,
            Vortice.Direct2D1.InterpolationMode.NearestNeighbor,
            new RawRectF(0, 0, width, height),
            null);
    }

    private void DisposeGraphics()
    {
        _vblankStop = true;
        _cardBitmap?.Dispose();
        _cardBitmap = null;
        _hintBitmap?.Dispose();
        _hintBitmap = null;
        _sceneTarget?.Dispose();
        _sceneTarget = null;
        _sceneTexture?.Dispose();
        _sceneTexture = null;
        _d3dContext = null;
        _swapchain3?.Dispose();
        _swapchain3 = null;
        _swapchain?.Dispose();
        _swapchain = null;
        _d2dContext?.Dispose();
        _d2dContext = null;
        lock (_commitGate)
        {
            _contentVisual?.Dispose();
            _contentVisual = null;
            _rootVisual?.Dispose();
            _rootVisual = null;
            _target?.Dispose();
            _target = null;
            _dcomp?.Dispose();
            _dcomp = null;
        }
        _d2dDevice?.Dispose();
        _d2dDevice = null;
        _d2dFactory?.Dispose();
        _d2dFactory = null;
        _d3dDevice?.Dispose();
        _d3dDevice = null;
    }

#if LINKPOCKET_DRAG_DIAGNOSTICS
    public long DebugRawInputCount => Volatile.Read(ref _rawInputCount);

    private void TraceTick(long now, int cursorX, int cursorY, bool hasCursor)
    {
        if (!_traceEnabled) return;
        lock (_traceGate)
        {
            if (!_traceEnabled) return;
            var entries = _traceEntries;
            var gapUs = _lastTraceQpc == 0
                ? 0
                : (int)Math.Min(int.MaxValue,
                    (now - _lastTraceQpc) * 1_000_000L / Stopwatch.Frequency);
            _lastTraceQpc = now;
            var entry = new HostTraceEntry
            {
                Qpc = now,
                CursorX = cursorX,
                CursorY = cursorY,
                TargetX = Volatile.Read(ref _targetX),
                TargetY = Volatile.Read(ref _targetY),
                AppliedX = Volatile.Read(ref _lastX),
                AppliedY = Volatile.Read(ref _lastY),
                AppliedSinceQpc = Volatile.Read(ref _lastAppliedChangeQpc),
                LoopGapUs = gapUs,
                SetPosUs = _lastSetPosUs,
                BitmapUs = _lastBitmapUs,
                RenderUs = _lastRenderUs,
                HintUs = _lastHintUploadUs,
                ContentPhaseUs = _lastContentPhaseUs,
                Flags = (byte)((hasCursor ? 1 : 0) | (_autoFollow ? 2 : 0) | (_lastBitmapApplied ? 4 : 0) | 8),
            };
            if (_traceCount < entries.Length)
            {
                entries[(_traceStart + _traceCount) % entries.Length] = entry;
                _traceCount++;
            }
            else
            {
                entries[_traceStart] = entry;
                _traceStart = (_traceStart + 1) % entries.Length;
            }
        }
        _lastSetPosUs = 0;
        _lastBitmapUs = 0;
        _lastRenderUs = 0;
        _lastHintUploadUs = 0;
        _lastBitmapApplied = false;
    }
#endif

    private const string WindowClassName = "LinkPocket.DragGhost";
    private static readonly WndProcHandler WndProcDelegate = RawInputWndProc;
    private const int WmQuit = 0x0012;
    private const int WmInput = 0x00FF;
    private const int WmNcHitTest = 0x0084;
    private const uint RidevInputSink = 0x00000100;
    private const int WmAppWake = 0x8000 + 1;
    private const uint PmRemove = 0x0001;
    private const uint QsAllInput = 0x04FF;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZorder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    private delegate IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

#if LINKPOCKET_DRAG_DIAGNOSTICS
    private struct HostTraceEntry
    {
        public long Qpc;
        public long AppliedSinceQpc;
        public int CursorX;
        public int CursorY;
        public int TargetX;
        public int TargetY;
        public int AppliedX;
        public int AppliedY;
        public int LoopGapUs;
        public int SetPosUs;
        public int BitmapUs;
        public int RenderUs;
        public int HintUs;
        public int ContentPhaseUs;
        public byte Flags;
    }
#endif

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public NativePoint pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(ref RawInputDevice devices, uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hWnd,
        uint filterMin, uint filterMax, uint removeMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr handles,
        bool waitAll, uint milliseconds, uint wakeMask);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjects(uint count, ref IntPtr handles,
        bool waitAll, uint milliseconds, uint wakeMask);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name,
        uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime,
        int period, IntPtr completionRoutine, IntPtr argument, bool resume);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
}

/// <summary>
/// UI 线程渲染好的冻结位图（Pbgra32 像素，**物理尺寸**）。
/// <para>
/// 这里是整个浮层唯一的位图入口，调用方固定是 <c>DragVisualOverlay.RenderContent</c>：
/// 它用 <c>RenderTargetBitmap</c> 按宿主 DPI 把 DIP 内容渲成物理像素，因此本类只读
/// <c>PixelWidth/PixelHeight</c>，不再看 DPI 元数据；<c>PixelWidth * 4</c> 是 32bpp
/// 位图每行的字节 stride（不是像素数）。显示侧 DComp 按 1:1 物理像素贴图，不做缩放。
/// </para>
/// </summary>
internal sealed class GhostBitmap
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    private GhostBitmap(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public static GhostBitmap? From(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;
        // 固定 Pbgra32 + 32bpp：stride = 宽 × 4 字节。调用方不是公共 API，别把任意格式
        // 的 BitmapSource 直接传进来（灰阶/16bpp 会得到错误步长）。
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return new GhostBitmap(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    public static GhostBitmap Create(int width, int height, byte[] pixels)
        => new(width, height, pixels);
}
