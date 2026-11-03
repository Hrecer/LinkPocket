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
/// <para><b>位置与内容分两条通路</b>：位置走 <c>IDCompositionVisual.SetOffset + Commit</c>
/// （合成时读取，提交截止在 vblank 前约 0.05ms）；内容（卡片 + 提示条 + 指针）画进
/// **同一张交换链**，只在内容变化时 Present——三者在物理上就是同一帧，不可能再差一帧。
/// 内容直接画在交换链的离屏场景里，再整块拷到当前后台缓冲（本机 D2D 只能绑 0 号缓冲）。</para>
///
/// <para><b>真实拖拽时接管系统光标</b>：把系统光标位图按热点画在抓取锚点上（同一张缓冲），
/// 并在 UI 线程（每线程计数）隐藏系统光标。屏幕上指针与卡片来自同一次合成，
/// 相对位置恒为 0——消除了“卡片比硬件光标晚一次采样”的固有抖动。指针离开本进程窗口
/// （拖到别的应用上）时交还系统光标，避免双指针。</para>
/// </summary>
internal sealed class GhostWindowHost
{
    public static GhostWindowHost Shared { get; } = new();

    private readonly object _gate = new();
    private readonly object _commitGate = new();   // 宿主循环与 vblank 前的晚锁存共用 DComp 提交
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

    // 系统光标：真实拖拽时把系统光标画进同一张场景（与卡片/提示条同帧），UI 线程同时隐藏系统光标。
    // 屏幕上指针与卡片来自同一张缓冲，任何时刻相对位置都精确为零——这是消除“卡片追指针”的唯一零采样差路线。
    private volatile bool _dragCursorMode;
    private volatile bool _cursorPainted;
    private bool _cursorModeLocked;
    private GhostCursor? _cursor;
    private volatile bool _pointerOverOwn;
    private volatile bool _systemCursorPresent;
    private uint _ownProcessId;
    private IntPtr _cursorShape;
    private ID2D1Bitmap? _cursorBitmap;
    private bool _cursorBitmapDirty;
    private bool _cursorGeometryDirty;
    private ID2D1Effect? _cursorShadow;
    private int _scenePadLeft;
    private int _scenePadTop;

    private ManualResetEventSlim? _showCompletion;
    private int _noGcActive;
    private long _loopCount;

    // vblank 节拍：内容更新（换提示/换光标形状/重建场景）只放在 vblank 后的安全窗口里做，
    // 避免 1ms 级的上传+渲染把位置提交挤过合成截止（那一拍整组卡片+指针会冻一帧再追平）。
    private Thread? _vblankThread;
    private volatile bool _vblankStop;
    private long _lastVBlankQpc;
    private long _vblankPeriodQpc;
    private int _lastContentPhaseUs;

    // TEMP-DRAG-DEBUG（验收后删除）：宿主线程逐拍环形轨迹。
    private readonly object _traceGate = new();
    private readonly HostTraceEntry[] _traceEntries = new HostTraceEntry[1 << 16];
    private int _traceStart;
    private int _traceCount;
    private volatile bool _traceEnabled;
    private string? _tracePath;
    private long _traceSessionStart;
    private long _lastTraceQpc;
    private long _lastAppliedChangeQpc;
    private long _rawInputCount;
    private int _lastSetPosUs;
    private int _lastBitmapUs;
    private int _lastRenderUs;
    private int _lastHintUploadUs;
    private bool _lastBitmapApplied;

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
        _ownProcessId = (uint)Environment.ProcessId;
    }

    /// <summary>指针下的窗口是否属于本进程（拖到别的应用上时光标交还系统线程显示）。</summary>
    private bool IsOwnWindowAt(int x, int y)
    {
        var hwnd = WindowFromPoint(new NativePoint { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == _ownProcessId;
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

    /// <summary>TEMP-DRAG-DEBUG（验收后删除）：拖拽期间是否成功进入 NoGC 区域。</summary>
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

    /// <summary>本次拖拽是否已把系统光标画进场景（UI 线程据此决定隐藏系统光标）。</summary>
    public bool IsCursorPainted => _cursorPainted && _cursorBitmap != null && _firstFramePresented;

    public int DebugCursorWidth => _cursor?.Width ?? 0;
    public int DebugCursorHeight => _cursor?.Height ?? 0;
    public int DebugCursorHotspotX => _cursor?.HotspotX ?? 0;
    public int DebugCursorHotspotY => _cursor?.HotspotY ?? 0;

    /// <summary>画进场景的光标热点在屏幕上的位置（= 卡片位置 + 抓取锚点 − 热点）。</summary>
    public int DebugCursorScreenX => Volatile.Read(ref _lastX) +
        (_cursor == null ? 0 : (int)Math.Round(_anchorX - _cursor.HotspotX));
    public int DebugCursorScreenY => Volatile.Read(ref _lastY) +
        (_cursor == null ? 0 : (int)Math.Round(_anchorY - _cursor.HotspotY));

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
                writer.Write(e.ShapeHash); writer.Write(',');
                writer.Write(e.ContentPhaseUs); writer.Write(',');
                writer.WriteLine(e.Flags);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"drag host trace write failed: {ex}");
        }
    }

    public void Prewarm()
    {
        try { EnsureThreadStarted(); }
        catch (Exception)
        {
        }
    }

    private void EnsureStarted()
    {
        EnsureThreadStarted();
        if (!_ready.IsSet) _ready.Wait(3000);
    }

    private void EnsureThreadStarted()
    {
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
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            CreateWindow();
            InitGraphics();
            StartVBlankThread();
            _ready.Set();
            RunLoop();
        }
        catch (Exception ex)
        {
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

        // 必须同时带 WS_EX_LAYERED：本机实测 WindowFromPoint 会命中非分层的全屏 DComp 窗口，
        // 而 OLE 拖拽就是用它找落点目标——浮层没有 IDropTarget，整块屏幕的落点会全部失效。
        // 分层属性让 WindowFromPoint 跳过这个窗口，DComp 内容仍照常合成（alpha 保持 255）。
        // LINKPOCKET_NO_LAYERED=1 仅用于对照实验（此时落点会失效）。
        var layered = Environment.GetEnvironmentVariable("LINKPOCKET_NO_LAYERED") != "1";
        var exStyle = WsExNoRedirectionBitmap | WsExTransparent | WsExNoActivate | WsExToolWindow | WsExTopmost;
        if (layered) exStyle |= WsExLayered;
        _hwnd = CreateWindowEx(
            exStyle, WindowClassName, string.Empty, WsPopup,
            _virtualLeft, _virtualTop, _virtualWidth, _virtualHeight,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed");
        if (layered) SetLayeredWindowAttributes(_hwnd, 0, 255, LwaAlpha);

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

    /// <summary>光标投影/模糊留白（物理像素）：集合区域与场景边缘之间要留够，否则指针阴影被裁成直线。</summary>
    private const int CursorShadowPad = 8;

    /// <summary>光标场景的固定对称预留（物理像素）：指针始终在抓取锚点上，四周留够一个光标 + 阴影，
    /// 拖拽中在“移动/禁止投放”等形状之间切换就不必重建交换链（重建会多花 1–3ms，还可能闪一帧）。</summary>
    private const int CursorReserve = 64;

    /// <summary>
    /// 按内容位图 + 光标占位重建场景。场景原点 = 卡片位图原点向左上扩展 (padLeft, padTop)；
    /// visual offset 相应减去同一组留白，卡片在屏幕上的落点不变。
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

        // 本机 D2D 只能把交换链 0 号缓冲绑成 target，1 号会 E_INVALIDARG；
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

    /// <summary>
    /// 计算光标在内容坐标里的占位，并据此重建场景。指针始终画在抓取锚点上，
    /// 四周按“一个光标 + 阴影”固定对称预留：形状切换不再改场景尺寸，也就不会在拖拽中重建交换链。
    /// </summary>
    private bool UpdateScene()
    {
        if (_cardWidth <= 0 || _cardHeight <= 0) return false;
        var padLeft = 0;
        var padTop = 0;
        var padRight = 0;
        var padBottom = 0;
        if (_dragCursorMode && _cursor != null)
        {
            var reserve = Math.Max(CursorReserve,
                Math.Max(_cursor.Width, _cursor.Height) + CursorShadowPad * 2);
            padLeft = padTop = padRight = padBottom = reserve;
        }
        return EnsureScene(_cardWidth, _cardHeight, padLeft, padTop, padRight, padBottom);
    }

    /// <summary>
    /// 跟随系统光标形状（拖拽中 OLE 会把指针切换成移动/禁止投放）：句柄变了就立刻捕获像素。
    /// 必须在每拍“清空硬件光标”之前读——SetCursor(NULL) 之后 GetCursorInfo 就只剩 0 了。
    /// 像素捕获只是 CPU 工作；场景几何/重绘留到 vblank 后的内容窗口。
    /// </summary>
    private void TrackCursorShape()
    {
        if (!_dragCursorMode) return;
        if (!TryGetCursorInfo(out var info)) return;
        // 系统又设置过光标（OLE 换形状/窗口 WM_SETCURSOR）：记下来，宿主节拍据此再清一次。
        _systemCursorPresent = info.hCursor != IntPtr.Zero;
        if (info.hCursor == IntPtr.Zero || info.hCursor == _cursorShape) return;
        var captured = GhostCursor.Capture(info.hCursor);
        if (captured == null) return;
        _cursorShape = info.hCursor;
        _cursor = captured;
        _cursorBitmapDirty = true;
        _cursorGeometryDirty = true;
        _contentDirty = true;
    }

    public void Show(BitmapSource bitmap, double anchorX, double anchorY, bool autoFollow = false)
    {
        EnsureStarted();
        if (_failed || Hwnd == IntPtr.Zero) return;
        _anchorX = anchorX;
        _anchorY = anchorY;
        _autoFollow = autoFollow;
        // 2026-11-03 回退：软件光标（与卡片同缓冲）能保证指针/卡片完全一体，但 DWM 合成粒度会带来
        // 偶发单帧 1px 级停顿，用户选择回退硬件光标——指针由系统独立平面绘制，卡片继续用 visual offset 追随。
        _dragCursorMode = false;
        _cursorPainted = false;
        _cursorModeLocked = false;
        _pointerOverOwn = autoFollow;   // 起手在应用内；首拍用 WindowFromPoint 校正
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
            _pendingHint = null;
            _pendingHintHide = true;
            _hintVisible = false;
        }
        _firstFramePresented = false;
        _contentDirty = true;

        var completion = new ManualResetEventSlim(false);
        _showCompletion = completion;
        _visible = true;
        _forcePosition = true;
        _forceTopmost = true;
        TimeBeginPeriod(1);
        PostWake();
        completion.Wait(1500);
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
        PostWake();
    }

    public void Hide()
    {
        _autoFollow = false;
        _visible = false;
        _forcePosition = true;
        TimeEndPeriod(1);
        EndNoGcRegion();
        PostWake();
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
            Volatile.Write(ref _noGcActive, 0);
        }
    }

    private void EndNoGcRegion()
    {
        if (Interlocked.Exchange(ref _noGcActive, 0) == 0) return;
        try { GC.EndNoGCRegion(); }
        catch (Exception) { }
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

            // 位置每帧只提交一次，固定在 vblank 之后：这次事务到下一个合成截止还有整整一帧余量，
            // 不会像“贴着截止抢帧”那样偶发错过、整帧停在原地再跳（快速横移时最明显）。
            // 代价是卡片稳定晚一帧——正是独立小窗 SetWindowPos 版的手感（2538779）。
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
        // 每帧最后一个可能的上屏点：如果系统又设置过光标，再清一次，避免这一帧看到它。
        if (_cursorPainted && _pointerOverOwn && _systemCursorPresent)
        {
            SetCursor(IntPtr.Zero);
            _systemCursorPresent = false;
        }
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
    /// 这段工作有 0.3–1.5ms，放在帧首还能在下一个合成截止前完成；放在帧尾会把位置提交挤过去，
    /// 整组卡片+指针就会冻一帧再追平（用户看到的一次性抖动）。
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
        Interlocked.Increment(ref _rawInputCount);
        if (GetCursorPos(out var cursor))
        {
            Volatile.Write(ref _targetX, (int)Math.Round(cursor.X - _anchorX));
            Volatile.Write(ref _targetY, (int)Math.Round(cursor.Y - _anchorY));
        }
    }

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
                    _lastBitmapApplied = false;
                    var traceCursorX = 0;
                    var traceCursorY = 0;
                    var traceHasCursor = false;

                    if (_autoFollow && GetCursorPos(out var cursor))
                    {
                        traceCursorX = cursor.X;
                        traceCursorY = cursor.Y;
                        traceHasCursor = true;
                        Volatile.Write(ref _targetX, (int)Math.Round(cursor.X - _anchorX));
                        Volatile.Write(ref _targetY, (int)Math.Round(cursor.Y - _anchorY));
                        // 指针在自己窗口上时由我们接管；拖到其它应用的窗口上时光标由那个线程显示，
                        // 再画一个就是双指针——此时交还系统光标。
                        // 每 25ms 查一次即可：WindowFromPoint 会跨线程给窗口发 WM_NCHITTEST，
                        // 按 0.5ms 的节拍查等于给拖拽主线程灌 2000 次/秒的命中测试（会拖慢甚至干扰 OLE 落点判定）。
                        if ((_loopCount & 0x3F) == 0)
                        {
                            var overOwn = IsOwnWindowAt(cursor.X, cursor.Y);
                            if (overOwn != _pointerOverOwn)
                            {
                                _pointerOverOwn = overOwn;
                                _contentDirty = true;
                            }
                        }
                    }

                    // 内容工作（光标形状、提示条、场景重建、Present）只在 vblank 后的安全窗口里做：
                    // 位置提交每拍照常，绝不因为 1ms 级的位图上传/渲染而错过合成截止。
                    var contentWindow = ContentWindowOpen();
                    if (contentWindow) _lastContentPhaseUs = CurrentContentPhaseUs();
                    // 每拍读一次光标句柄：必须发生在“清空硬件光标”之前，否则 GetCursorInfo 只剩 0。
                    TrackCursorShape();
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
                        // 光标形状可能变了：重建场景留白（多数形状同尺寸，复用现有交换链）。
                        if (_cursorGeometryDirty)
                        {
                            _cursorGeometryDirty = false;
                            if (UpdateScene()) sceneRebuilt = true;
                        }

                        GhostBitmap? pending;
                        GhostBitmap? pendingHint;
                        bool hintHide;
                        int hintOffX, hintOffY;
                        lock (_gate)
                        {
                            pending = _pendingBitmap;
                            _pendingBitmap = null;
                            pendingHint = _pendingHint;
                            _pendingHint = null;
                            hintOffX = _pendingHintOffsetX;
                            hintOffY = _pendingHintOffsetY;
                            hintHide = _pendingHintHide;
                        }
                        if (pending != null)
                        {
                            if (UploadCard(pending)) sceneRebuilt = true;
                            _lastBitmapApplied = true;
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

                        // 首帧锁存“本次拖拽由浮层接管光标”：之后系统光标已被隐藏，
                        // 中途捕获状态变化也不能切换（否则可能两个指针同时出现或一个都没有）。
                        if (!_cursorModeLocked) _cursorPainted = _dragCursorMode && _cursor != null;
                        var renderStart = Stopwatch.GetTimestamp();
                        var rendered = (_contentDirty || !_firstFramePresented) && RenderScene();
                        _lastRenderUs = (int)Math.Min(int.MaxValue,
                            (Stopwatch.GetTimestamp() - renderStart) * 1_000_000L / Stopwatch.Frequency);
                        if (rendered)
                        {
                            _contentDirty = false;
                            _firstFramePresented = true;
                            _cursorModeLocked = true;
                        }
                    }

                    // ShowCursor 的每线程计数在这台机器上不生效（硬件指针实测仍可见）；
                    // 用 SetCursor(NULL) 直接清空硬件光标——只在本进程窗口上、且已经画出替代指针之后。
                    // 只在系统真的重新设置过光标时清（避免每秒上千次冗余 SetCursor 干扰合成）。
                    if (_cursorPainted && _firstFramePresented && _pointerOverOwn && _systemCursorPresent)
                    {
                        SetCursor(IntPtr.Zero);
                        _systemCursorPresent = false;
                    }

                    if (!_windowShown && _firstFramePresented)
                    {
                        ShowWindow(_hwnd, SwShowNoActivate);
                        _windowShown = true;
                        Interlocked.Exchange(ref _showCompletion, null)?.Set();
                    }

                    TraceTick(Stopwatch.GetTimestamp(), traceCursorX, traceCursorY, traceHasCursor);
                }
                else
                {
                    if (_windowShown)
                    {
                        ShowWindow(_hwnd, SwHide);
                        _windowShown = false;
                        // 拖拽结束恢复系统光标；之后鼠标一移动，各窗口的 WM_SETCURSOR 会换回正确形状。
                        if (_dragCursorMode) SetCursor(LoadCursor(IntPtr.Zero, IdcArrow));
                    }
                    _firstFramePresented = false;
                    _cursorPainted = false;
                    _cursorModeLocked = false;
                    _contentDirty = true;
                    lock (_gate)
                    {
                        _pendingHint = null;
                        _pendingHintHide = true;
                        _hintVisible = false;
                    }
                    // 注意：_pendingBitmap 不能丢（Show 的顺序是“放位图 → 置 visible”）。
                }
            }
        }
        finally
        {
            _vblankStop = true;
            if (_cursorPainted) SetCursor(LoadCursor(IntPtr.Zero, IdcArrow));
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
    /// 宿主每 0.5ms 更新一次，合成器拿到的就是同一帧里最新的指针位置（与画进场景的指针同帧）。
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
        if (_cursorPainted && _dragCursorMode && _pointerOverOwn && _cursor != null)
        {
            EnsureCursorBitmap();
            if (_cursorBitmap != null) DrawCursor();
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

    /// <summary>把捕获的像素做成 D2D 位图（形状变化时才重建）。</summary>
    private void EnsureCursorBitmap()
    {
        if (_cursorBitmap != null && !_cursorBitmapDirty) return;
        if (_cursor == null || _d2dContext == null) return;
        _cursorBitmap?.Dispose();
        _cursorBitmap = CreateBitmap(_d2dContext,
            GhostBitmap.Create(_cursor.Width, _cursor.Height, _cursor.Pixels));
        _cursorBitmapDirty = false;
        _cursorShadow?.Dispose();   // 阴影效果持有旧位图输入
        _cursorShadow = null;
    }

    /// <summary>
    /// 画指针：热点落在抓取锚点上，与卡片/提示条同一张缓冲。
    /// 系统指针的投影是 DWM 画的、不在光标位图里，这里用模糊阴影近似（偏移/模糊随光标尺寸缩放）。
    /// </summary>
    private void DrawCursor()
    {
        if (_cursor == null || _cursorBitmap == null || _d2dContext == null) return;
        var x = _scenePadLeft + (int)Math.Round(_anchorX - _cursor.HotspotX);
        var y = _scenePadTop + (int)Math.Round(_anchorY - _cursor.HotspotY);

        _cursorShadow ??= new Vortice.Direct2D1.Effects.Shadow(_d2dContext)
        {
            BlurStandardDeviation = Math.Max(1f, _cursor.Width * 0.03f),
            Color = new System.Numerics.Vector4(0, 0, 0, 0.45f),
        };
        _cursorShadow.SetInput(0, _cursorBitmap, false);
        var offset = Math.Max(1f, _cursor.Width / 32f * 1.2f);
        _d2dContext.DrawImage(_cursorShadow, new System.Numerics.Vector2(x + offset, y + offset),
            Vortice.Direct2D1.InterpolationMode.Linear,
            Vortice.Direct2D1.CompositeMode.SourceOver);

        _d2dContext.DrawBitmap(_cursorBitmap,
            new RawRectF(x, y, x + _cursor.Width, y + _cursor.Height),
            1f,
            Vortice.Direct2D1.InterpolationMode.NearestNeighbor,
            new RawRectF(0, 0, _cursor.Width, _cursor.Height),
            null);
    }

    private void DisposeGraphics()
    {
        _vblankStop = true;
        _cardBitmap?.Dispose();
        _cardBitmap = null;
        _hintBitmap?.Dispose();
        _hintBitmap = null;
        _cursorBitmap?.Dispose();
        _cursorBitmap = null;
        _cursorShadow?.Dispose();
        _cursorShadow = null;
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
                ShapeHash = (int)(_cursorShape.ToInt64() & 0xFFFF),
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
        public int ShapeHash;
        public int ContentPhaseUs;
        public byte Flags;
    }

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
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    private static readonly IntPtr IdcArrow = new(32512);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    /// <summary>CURSORINFO 必须先填 cbSize；包装一层避免忘记初始化。</summary>
    private static bool TryGetCursorInfo(out CursorInfo info)
    {
        info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfoNative(out info);
    }

    [DllImport("user32.dll", EntryPoint = "GetCursorInfo", SetLastError = true)]
    private static extern bool GetCursorInfoNative(out CursorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr hCursor;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
}

/// <summary>UI 线程渲染好的冻结位图（Pbgra32 像素，物理尺寸）。</summary>
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
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return new GhostBitmap(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    public static GhostBitmap Create(int width, int height, byte[] pixels)
        => new(width, height, pixels);
}

/// <summary>
/// 一份系统光标快照（预乘 BGRA，物理像素大小）与热点。拖拽时画进浮层场景，
/// 使屏幕上的指针与卡片来自同一张后台缓冲。
/// </summary>
internal sealed class GhostCursor
{
    public int Width { get; }
    public int Height { get; }
    public int HotspotX { get; }
    public int HotspotY { get; }
    public byte[] Pixels { get; }

    private GhostCursor(int width, int height, int hotspotX, int hotspotY, byte[] pixels)
    {
        Width = width;
        Height = height;
        HotspotX = hotspotX;
        HotspotY = hotspotY;
        Pixels = pixels;
    }

    /// <summary>抓取光标像素：优先用 32 位 alpha；没有 alpha 的旧式光标退回 AND 掩码/单色掩码。</summary>
    public static GhostCursor? Capture(IntPtr hcursor)
    {
        if (!GetIconInfo(hcursor, out var info)) return null;
        try
        {
            var hasColor = info.hbmColor != IntPtr.Zero;
            var color = default(NativeBitmap);
            var mask = default(NativeBitmap);
            if (hasColor && GetObject(info.hbmColor, Marshal.SizeOf<NativeBitmap>(), ref color) == 0) return null;
            if (GetObject(info.hbmMask, Marshal.SizeOf<NativeBitmap>(), ref mask) == 0) return null;

            var monochrome = !hasColor;
            var width = hasColor ? color.Width : mask.Width;
            var height = hasColor ? color.Height : mask.Height / 2;
            if (width <= 0 || height <= 0 || width > 512 || height > 512) return null;

            var hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;
            try
            {
                var colorBits = new byte[width * height * 4];
                if (hasColor)
                {
                    var bmi = new BitmapInfo { Header = Header(width, -height, 32) };
                    if (GetDIBits(hdc, info.hbmColor, 0, (uint)height, colorBits, ref bmi, 0) == 0) return null;
                }

                var maskRows = monochrome ? height * 2 : height;
                var maskStride = (width + 31) / 32 * 4;
                var maskBits = new byte[maskStride * maskRows];
                var maskBmi = new BitmapInfo { Header = Header(width, -maskRows, 1) };
                if (GetDIBits(hdc, info.hbmMask, 0, (uint)maskRows, maskBits, ref maskBmi, 0) == 0) return null;

                var anyAlpha = false;
                if (hasColor)
                {
                    for (var i = 3; i < colorBits.Length; i += 4)
                    {
                        if (colorBits[i] == 0) continue;
                        anyAlpha = true;
                        break;
                    }
                }

                var pixels = new byte[width * height * 4];
                var xorRow = height * maskStride;
                for (var y = 0; y < height; y++)
                {
                    var maskRow = y * maskStride;
                    for (var x = 0; x < width; x++)
                    {
                        byte b, g, r, a;
                        var bit = maskBits[maskRow + (x >> 3)] & (0x80 >> (x & 7));
                        if (hasColor)
                        {
                            var si = (y * width + x) * 4;
                            b = colorBits[si];
                            g = colorBits[si + 1];
                            r = colorBits[si + 2];
                            a = anyAlpha ? colorBits[si + 3] : bit != 0 ? (byte)0 : (byte)255;
                        }
                        else
                        {
                            var xorBit = maskBits[xorRow + maskRow + (x >> 3)] & (0x80 >> (x & 7));
                            if (bit != 0 && xorBit == 0) { b = g = r = a = 0; }
                            else if (bit == 0 && xorBit == 0) { b = g = r = 0; a = 255; }
                            else if (bit == 0 && xorBit != 0) { b = g = r = 255; a = 255; }
                            else { b = g = r = a = 0; }
                        }
                        if (a == 0) continue;
                        var di = (y * width + x) * 4;
                        pixels[di] = (byte)(b * a / 255);
                        pixels[di + 1] = (byte)(g * a / 255);
                        pixels[di + 2] = (byte)(r * a / 255);
                        pixels[di + 3] = a;
                    }
                }

                return new GhostCursor(width, height, (int)info.HotspotX, (int)info.HotspotY, pixels);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    private static BitmapInfoHeader Header(int width, int height, short bitCount) => new()
    {
        Size = 40,
        Width = width,
        Height = height,
        Planes = 1,
        BitCount = bitCount,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RgbQuad
    {
        public byte Blue;
        public byte Green;
        public byte Red;
        public byte Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public RgbQuad C0;
        public RgbQuad C1;
        public RgbQuad C2;
        public RgbQuad C3;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out IconInfo info);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", CharSet = CharSet.Unicode)]
    private static extern int GetObject(IntPtr handle, int size, ref NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines,
        byte[] bits, ref BitmapInfo info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
}
