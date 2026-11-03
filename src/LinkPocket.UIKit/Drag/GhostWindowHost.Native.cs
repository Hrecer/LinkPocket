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
/// <para><b>为什么不用 DComp visual offset 搬卡片</b>：本机 DComp 只合成窗口首次上屏时的
/// visual 树——后加 visual、对可见 visual 换内容都不合成（实测）。卡片与提示条没法在同一窗口里
/// 用两个 visual 原子更新；两台设备各 Commit 又会被 vblank 拆帧，产生提示条 ±1px 相对抖动。</para>
///
/// <para><b>现在的做法</b>：一张<b>合成交换链</b>铺满虚拟屏幕，每帧把卡片与提示条画进
/// **同一张后台缓冲**，一次 Present 上屏——两者在物理上就是同一帧，不可能再差一帧。
/// 视觉（位图、尺寸、位置、对齐、阴影）与之前完全一致。</para>
/// </summary>
internal sealed class GhostWindowHost
{
    public static GhostWindowHost Shared { get; } = new();

    private readonly object _gate = new();
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

    private ManualResetEventSlim? _showCompletion;
    private int _noGcActive;
    private long _loopCount;

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
            writer.WriteLine("# tMs,curX,curY,tgtX,tgtY,actX,actY,gapUs,setUs,bitmapUs,flags");
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

        // 交换链由 DComp 合成，窗口不再需要 WS_EX_LAYERED（分层窗口与交换链不兼容）。
        _hwnd = CreateWindowEx(
            WsExNoRedirectionBitmap | WsExTransparent | WsExNoActivate | WsExToolWindow | WsExTopmost,
            WindowClassName, string.Empty, WsPopup,
            _virtualLeft, _virtualTop, _virtualWidth, _virtualHeight,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed");

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

    /// <summary>
    /// 按卡片位图尺寸建/换交换链与离屏场景。只在窗口隐藏时调用（换内容发生在 ShowWindow 之前），
    /// 换完必须再 Present 一帧内容才会出现在合成器里。
    /// </summary>
    private bool EnsureScene(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        if (_swapchain != null && _sceneWidth == width && _sceneHeight == height) return true;
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

        _contentVisual.SetContent(swapchain).CheckError();
        _dcomp.Commit().CheckError();

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
        _contentDirty = true;
        _firstFramePresented = false;
        return true;
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
                    }

                    if (_forceTopmost)
                    {
                        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0,
                            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
                        _forceTopmost = false;
                    }

                    var x = Volatile.Read(ref _targetX);
                    var y = Volatile.Read(ref _targetY);

                    // 位置走 visual offset：合成器在合成时读取，和硬件光标同一帧；
                    // 内容走交换链 Present，卡片与提示条始终在同一张缓冲里。
                    // 先把位置提交掉再碰位图：换字上传（CreateBitmap 可能 1–2ms）不挡位置。
                    var cardMoved = _forcePosition ||
                        x != Volatile.Read(ref _lastX) || y != Volatile.Read(ref _lastY);
                    if (cardMoved)
                    {
                        var moveStart = Stopwatch.GetTimestamp();
                        _forcePosition = false;
                        ApplyOffset(x, y);
                        _dcomp?.Commit().CheckError();
                        _lastSetPosUs = (int)Math.Min(int.MaxValue,
                            (Stopwatch.GetTimestamp() - moveStart) * 1_000_000L / Stopwatch.Frequency);
                        _lastAppliedChangeQpc = Stopwatch.GetTimestamp();
                        Volatile.Write(ref _lastX, x);
                        Volatile.Write(ref _lastY, y);
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
                        UploadCard(pending);
                        _lastBitmapApplied = true;
                    }
                    if (pendingHint != null && !hintHide)
                    {
                        var start = Stopwatch.GetTimestamp();
                        UploadHint(pendingHint);
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

                    if ((_contentDirty || !_firstFramePresented) && RenderScene())
                    {
                        _contentDirty = false;
                        _firstFramePresented = true;
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
                    }
                    _firstFramePresented = false;
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
            if (highResolutionTimer != IntPtr.Zero) CloseHandle(highResolutionTimer);
            DisposeGraphics();
        }
    }

    private void UploadCard(GhostBitmap pending)
    {
        if (_d2dContext == null) return;
        var start = Stopwatch.GetTimestamp();
        _cardBitmap?.Dispose();
        _cardBitmap = CreateBitmap(_d2dContext, pending);
        Volatile.Write(ref _cardWidth, pending.Width);
        Volatile.Write(ref _cardHeight, pending.Height);
        EnsureScene(pending.Width, pending.Height);
        _contentDirty = true;
        _lastBitmapUs = (int)Math.Min(int.MaxValue,
            (Stopwatch.GetTimestamp() - start) * 1_000_000L / Stopwatch.Frequency);
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

    /// <summary>
    /// 卡片在屏幕上的落点 = 内容 visual 的 offset。offset 是“合成时读取”的属性：
    /// 宿主每 0.5ms 更新一次，合成器拿到的就是同一帧里最新的指针位置（与硬件光标同帧）。
    /// </summary>
    private void ApplyOffset(int cardX, int cardY)
    {
        _contentVisual?.SetOffsetX(cardX - _virtualLeft);
        _contentVisual?.SetOffsetY(cardY - _virtualTop);
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
        DrawAt(_cardBitmap, 0, 0, _cardWidth, _cardHeight);
        if (_hintVisible && _hintBitmap != null)
        {
            DrawAt(_hintBitmap,
                Volatile.Read(ref _hintOffsetX),
                Volatile.Read(ref _hintOffsetY),
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
        _contentVisual?.Dispose();
        _contentVisual = null;
        _rootVisual?.Dispose();
        _rootVisual = null;
        _target?.Dispose();
        _target = null;
        _dcomp?.Dispose();
        _dcomp = null;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

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
}
