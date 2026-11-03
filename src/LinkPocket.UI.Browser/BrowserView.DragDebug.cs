using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using LinkPocket.Contracts;
using LinkPocket.ViewModels;

namespace LinkPocket.Views.Browser;

/// <summary>
/// TEMP-DRAG-DEBUG（验收后删除）：拖拽跟随的逐拍诊断。
///
/// <para>只回答三个问题，全部带**时间戳**：</para>
/// <list type="number">
/// <item>指针采样到底多密（渲染帧 / 4ms 定时器 / GiveFeedback 各自的真实间隔）；</item>
/// <item>每一拍里「浮层目标位置 − 指针」是否等于抓取锚点（出现反向位移/错位 = 有东西在拽窗口）；</item>
/// <item>哪些帧出现了「指针动了、浮层没跟上」或「浮层位置与请求位置不一致」，以及当时是否伴随落点/提示条变化。</item>
/// </list>
///
/// <para>开销纪律：逐拍只做两次 Win32 读取 + 栈上格式化 + 缓冲写盘；不截屏、不逐帧串拼字符串。
/// 事件行只在落点/提示真的变化时写。所有异常都吞掉——诊断绝不拖累拖拽本身。</para>
/// </summary>
internal static class DragDiagnostics
{
    private static Session? _current;

    /// <summary>本次拖拽是否正在记录（调用点据此跳过字符串拼接）。</summary>
    public static bool IsActive => _current != null;

    /// <summary>重探针（每帧 BitBlt 3–5ms + 桌面复制）默认关闭：它们自己会抢占 CPU/GPU，
    /// 在验收拖拽手感的场景里必须先排除测量干扰。设 LINKPOCKET_DRAG_PROBES=1 才启用。
    /// 轻量宿主轨迹始终保留（内存环形缓冲，拖拽结束落盘）。</summary>
    public static bool FullProbes => Environment.GetEnvironmentVariable("LINKPOCKET_DRAG_PROBES") == "1";

    public static void Begin(IReadOnlyList<DragItem> items, DragVisualOverlay overlay, FrameworkElement view)
    {
        End();
        try
        {
            _current = Session.Start(items, overlay, view);
        }
        catch (Exception ex)
        {
            _current = null;
            Debug.WriteLine($"drag diagnostics unavailable: {ex}");
            // 诊断自己坏了也要留证据（否则用户只会看到「没有 HUD / 没有日志」）
            try
            {
                var root = ResolveRepositoryRoot() ?? AppContext.BaseDirectory;
                var folder = Path.Combine(root, ".scratch", "drag-debug");
                Directory.CreateDirectory(folder);
                AtomicFile.WriteAllText(
                    Path.Combine(folder, $"diagnostics-error-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),
                    ex.ToString());
            }
            catch (Exception writeError)
            {
                Debug.WriteLine($"drag diagnostics error report failed: {writeError}");
            }
        }
    }

    private static string? ResolveRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LinkPocket.sln"))) return dir.FullName;
        }
        return null;
    }

    /// <summary>记录一拍（在浮层摆位之后调用）。<paramref name="callTicks"/> = 摆位调用自身耗时（QPC tick）。</summary>
    public static void Sample(string source, long callTicks) => _current?.Sample(source, callTicks, null, null);

    /// <summary>记录一拍，并带上**摆位时实际读到的光标**（避免摆位后二次读数造成的滞后假象）。</summary>
    public static void Sample(string source, long callTicks, int cursorX, int cursorY)
        => _current?.Sample(source, callTicks, cursorX, cursorY);

    public static void Event(string kind, string detail) => _current?.Event(kind, detail);

    /// <summary>提示条变化（窗口尺寸/位置可能随之变化）——调用方已量好耗时。</summary>
    public static void HintChanged(DragVisualOverlay? overlay, double setMs, double hintMs, string hint,
        Size sizeBefore, Size sizeAfter, string rectBefore, string targetBefore, string? targetAfter)
        => _current?.HintChanged(overlay, setMs, hintMs, hint, sizeBefore, sizeAfter, rectBefore,
            Describe(overlay), targetBefore, targetAfter);

    /// <summary>浮层当前实际位置与物理尺寸（屏幕物理像素）：x,y,WxH。</summary>
    public static string Describe(DragVisualOverlay? overlay)
    {
        if (overlay == null) return "none";
        return $"{overlay.DebugWindowX},{overlay.DebugWindowY}," +
               $"{overlay.DebugBitmapWidth}x{overlay.DebugBitmapHeight}";
    }

    public static void End()
    {
        var session = _current;
        _current = null;
        session?.Finish();
    }

    private sealed class Session
    {
        private static string? _sessionDir;
        private static int _sequence;

        private readonly DragVisualOverlay _overlay;
        private readonly IntPtr _hwnd;
        private readonly double _anchorPxX;
        private readonly double _anchorPxY;
        private readonly double _dpiScaleX;
        private readonly double _dpiScaleY;
        private readonly long _startTick = Stopwatch.GetTimestamp();
        private readonly string _stem;
        private readonly string _folder;
        private readonly StreamWriter _samples;
        private readonly StreamWriter _events;
        private readonly DragPresentProbe? _present;

        private readonly List<double> _gaps = new();
        private readonly List<double> _callUs = new();
        private readonly List<double> _rIntervals = new();
        private readonly List<double> _tIntervals = new();
        private readonly List<double> _gIntervals = new();
        private readonly List<double> _dIntervals = new();
        private readonly List<double> _moveIntervals = new();
        private readonly List<string> _anomalies = new();

        private long _lastAnyTick = -1;
        private long _lastFlushTick;
        private long _lastRTick = -1;
        private long _lastTTick = -1;
        private long _lastGTick = -1;
        private long _lastDTick = -1;
        private int _lastCurX;
        private int _lastCurY;
        private int _lastTx = int.MinValue;
        private int _lastTy = int.MinValue;
        private int _lastAx;
        private int _lastAy;
        private int _lastHintLen;
        private uint _lastHintHash;
        private bool _hasPrev;

        private int _sampleCount;
        private int _moves;
        private int _hintChanges;
        private int _targetJumps;
        private int _applyErrors;
        private int _stalls;
        private int _rCount;
        private int _tCount;
        private int _gCount;
        private int _dCount;
        private int _refreshHz;
        private int _slowMoves;
        private long _lastMoveTick = -1;
        private bool _skipJumpCheck;
        private double _lastTargetChangeMs = -1;
        private double _maxGap;
        private int _gapsOver8;
        private int _gapsOver12;
        private int _gapsOver16;
        private int _gapsOver33;
        private double _maxCallUs;
        private double _maxTInterval;
        private double _lastGap;
        private double _lastCallUs;
        private double _lastLagX;
        private double _lastLagY;
        private double _maxLag;
        private double _maxApplyErr;
        private int _hintResizes;
        private double _maxHintMs;
        private double _maxSetMs;
        private int _gc0;
        private int _gc1;
        private int _gc2;
        private int _lastGc0;
        private int _lastGc1;
        private int _lastGc2;

        private Session(DragVisualOverlay overlay, FrameworkElement view, string folder, string stem, int itemCount)
        {
            _overlay = overlay;
            _folder = folder;
            _stem = stem;
            _hwnd = overlay.DebugHwnd;
            var dpi = VisualTreeHelper.GetDpi(view);
            _dpiScaleX = dpi.DpiScaleX;
            _dpiScaleY = dpi.DpiScaleY;
            _anchorPxX = overlay.DebugAnchor.X * dpi.DpiScaleX;
            _anchorPxY = overlay.DebugAnchor.Y * dpi.DpiScaleY;

            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var samplePath = Path.Combine(folder, stem + ".samples.csv");
            var eventPath = Path.Combine(folder, stem + ".events.txt");
            _samples = new StreamWriter(
                new FileStream(samplePath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024),
                encoding) { AutoFlush = false, NewLine = "\r\n" };
            _events = new StreamWriter(
                new FileStream(eventPath, FileMode.Create, FileAccess.Write, FileShare.Read, 8 * 1024),
                encoding) { AutoFlush = true, NewLine = "\r\n" };

            _gc0 = GC.CollectionCount(0);
            _gc1 = GC.CollectionCount(1);
            _gc2 = GC.CollectionCount(2);
            _lastGc0 = _gc0;
            _lastGc1 = _gc1;
            _lastGc2 = _gc2;

            var screenDc = GetDC(IntPtr.Zero);
            _refreshHz = screenDc == IntPtr.Zero ? 0 : GetDeviceCaps(screenDc, VRefresh);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            _samples.WriteLine("# LinkPocket drag diagnostics (TEMP-DRAG-DEBUG)");
            _samples.WriteLine(
                $"# drag={_sequence} items={itemCount} refreshHz={_refreshHz}" +
                $" dpi={dpi.DpiScaleX:F2},{dpi.DpiScaleY:F2} anchorPx={_anchorPxX:F2},{_anchorPxY:F2}");
            _samples.WriteLine(
                "tMs,src,curX,curY,tgtX,tgtY,actX,actY,actW,actH,hintLen,hintHash,gapMs,callUs,moved," +
                "lagX,lagY,applyErrX,applyErrY,curStepX,curStepY,tgtStepX,tgtStepY");
            _events.WriteLine("# LinkPocket drag events (TEMP-DRAG-DEBUG)");
            _events.WriteLine("# tMs  event  details    (anchorPx={_anchorPxX:F2},{_anchorPxY:F2})");

            // 不建设实时 HUD：拖拽起手瞬间创建窗口会把 UI 线程占住约 10ms，
            // 浮层已摆好、指针继续移动，第一拍再补正 —— 诊断自身会制造一次起手跳变。

            Event("begin", $"items={itemCount} hwnd=0x{_hwnd.ToInt64():X} " +
                           $"window={DescribeWindow()}");
            // 先开 host 轨迹，再开屏幕探针：探针每帧都要按 QPC 回查 host 的光标/落位记录。
            try { overlay.StartHostTrace(Path.Combine(folder, stem + ".host.csv"), _startTick); }
            catch (Exception ex) { Debug.WriteLine($"drag host trace unavailable: {ex}"); }
            if (FullProbes)
                _present = new DragPresentProbe(folder, stem, _startTick, overlay, overlay.DebugMarkerColor);
        }

        public static Session Start(IReadOnlyList<DragItem> items, DragVisualOverlay overlay, FrameworkElement view)
        {
            var root = ResolveRepositoryRoot();
            if (root == null) throw new IOException("LinkPocket.sln not found above the application directory");
            _sessionDir ??= Path.Combine(root, ".scratch", "drag-debug",
                "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(_sessionDir);

            _sequence++;
            var stem = $"drag-{_sequence:000}-{DateTime.Now:HHmmss}";
            var session = new Session(overlay, view, _sessionDir, stem, items.Count);
            session._samples.Flush();
            return session;
        }

        private double NowMs => (Stopwatch.GetTimestamp() - _startTick) * 1000.0 / Stopwatch.Frequency;

        private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        public void Sample(string source, long callTicks, int? positionedX, int? positionedY)
        {
            try
            {
                var tick = Stopwatch.GetTimestamp();
                var tMs = ToMs(tick - _startTick);
                var gap = _lastAnyTick < 0 ? 0 : ToMs(tick - _lastAnyTick);
                var callUs = callTicks <= 0 ? 0 : callTicks * 1_000_000.0 / Stopwatch.Frequency;

                var gc0 = GC.CollectionCount(0);
                var gc1 = GC.CollectionCount(1);
                var gc2 = GC.CollectionCount(2);
                if (gc0 != _lastGc0 || gc1 != _lastGc1 || gc2 != _lastGc2)
                {
                    Event("gc", $"0:{_lastGc0}->{gc0} 1:{_lastGc1}->{gc1} 2:{_lastGc2}->{gc2} " +
                                $"gapMs={gap:F1} prevCallUs={_lastCallUs:F0}");
                    _lastGc0 = gc0;
                    _lastGc1 = gc1;
                    _lastGc2 = gc2;
                }

                NativePoint cursor;
                if (positionedX is { } px && positionedY is { } py)
                {
                    // 定位样本：用摆位时实际读到的光标，lag/target-jump 才是真实对齐误差
                    cursor = new NativePoint { X = px, Y = py };
                }
                else
                {
                    GetCursorPos(out cursor);   // 非定位样本（提示变化后补采）：读当前光标
                }
                var actW = (int)Math.Ceiling(_overlay.DebugWindowSize.Width * _dpiScaleX);
                var actH = (int)Math.Ceiling(_overlay.DebugWindowSize.Height * _dpiScaleY);
                var hint = _overlay.DebugHintText;
                var hintHash = Hash(hint);
                var hintLen = hint.Length;

                var tx = _overlay.DebugWindowX;
                var ty = _overlay.DebugWindowY;
                var moved = _hasPrev ? tx != _lastTx || ty != _lastTy : true;
                var lagX = cursor.X - (tx + _anchorPxX);
                var lagY = cursor.Y - (ty + _anchorPxY);
                var applyErrX = 0;
                var applyErrY = 0;
                var curStepX = _hasPrev ? cursor.X - _lastCurX : 0;
                var curStepY = _hasPrev ? cursor.Y - _lastCurY : 0;
                var tgtStepX = _hasPrev && _lastTx != int.MinValue ? tx - _lastTx : 0;
                var tgtStepY = _hasPrev && _lastTy != int.MinValue ? ty - _lastTy : 0;

                AppendSample(tMs, source, cursor.X, cursor.Y, tx, ty,
                    tx, ty, actW, actH,
                    hintLen, hintHash, gap, callUs, moved, lagX, lagY, applyErrX, applyErrY,
                    curStepX, curStepY, tgtStepX, tgtStepY);

                _samples.WriteLine(_line.AsSpan(0, _lineLength));
                if (tick - _lastFlushTick >= Stopwatch.Frequency / 2)   // 每 500ms 刷一次盘：逐拍 flush 自己会制造停顿
                {
                    _samples.Flush();
                    _lastFlushTick = tick;
                }

                if (_hasPrev)
                {
                    _gaps.Add(gap);
                    if (gap > 8) _gapsOver8++;
                    if (gap > 12) _gapsOver12++;
                    if (gap > 16) _gapsOver16++;
                    if (gap > 33) _gapsOver33++;
                    if (gap > _maxGap) _maxGap = gap;

                    var cursorStep = Math.Sqrt(curStepX * curStepX + curStepY * curStepY);
                    if (gap > 10 && cursorStep >= 1)
                    {
                        _stalls++;
                        AddAnomaly($"!! stall t={tMs:F1}ms src={source} gap={gap:F1}ms cursorStep={cursorStep:F1}px " +
                                   $"dCur=({curStepX},{curStepY}) hintLen={hintLen}");
                    }

                    // 浮层目标与指针步进不一致（超过采样相位/排版取整）＝ 有一次位移不是指针驱动的。
                    // 主线程采样和宿主 Raw Input 更新并不同相：起手/快速段 3–5px 的步进差是采样相位，
                    // 不是用户可见位移（实测卡片落位 act=cur−anchor 恒定，present 标记也同点）。
                    // 阈值取 8px：只有达到肉眼可见量级的步进差才计为 target jump。
                    // E/C 是"落点/提示变化后补采"的样本（本身不摆位），会让下一拍看起来像跳变——
                    // 这两类样本及其后一拍不参与该判定，避免诊断自己制造假阳性。
                    var positioning = source is "R" or "T" or "D" or "S";
                    if (!positioning)
                    {
                        _skipJumpCheck = true;
                    }
                    else if (_skipJumpCheck)
                    {
                        _skipJumpCheck = false;
                    }
                    else if (Math.Abs(tgtStepX - curStepX) >= 8 || Math.Abs(tgtStepY - curStepY) >= 8)
                    {
                        _targetJumps++;
                        AddAnomaly($"!! target-jump t={tMs:F1}ms dCur=({curStepX},{curStepY}) " +
                                   $"dTgt=({tgtStepX},{tgtStepY}) hintLen={hintLen}");
                    }

                    if (Math.Abs(applyErrX) >= 2 || Math.Abs(applyErrY) >= 2)
                    {
                        _applyErrors++;
                        AddAnomaly($"!! apply-error t={tMs:F1}ms target=({tx},{ty}) actual=({tx},{ty})");
                    }
                }

                if (callUs > _maxCallUs) _maxCallUs = callUs;
                if (moved)
                {
                    if (_lastMoveTick >= 0) _moveIntervals.Add(ToMs(tick - _lastMoveTick));
                    _lastMoveTick = tick;
                    if (callUs > 1500)
                    {
                        _slowMoves++;
                        var sinceTarget = _lastTargetChangeMs < 0 ? -1 : tMs - _lastTargetChangeMs;
                        AddAnomaly($"!! slow-move t={tMs:F1}ms src={source} call={callUs:F1}us gap={gap:F1}ms " +
                                   $"sinceTarget={sinceTarget:F1}ms dCur=({curStepX},{curStepY}) hintLen={hintLen}");
                    }
                }
                if (Math.Abs(lagX) > _maxLag) _maxLag = Math.Abs(lagX);
                if (Math.Abs(lagY) > _maxLag) _maxLag = Math.Abs(lagY);
                var applyErr = Math.Sqrt(applyErrX * applyErrX + applyErrY * applyErrY);
                if (applyErr > _maxApplyErr) _maxApplyErr = applyErr;
                if (moved) _moves++;

                if (_lastHintHash != hintHash || _lastHintLen != hintLen) _hintChanges++;

                _sampleCount++;
                _lastAnyTick = tick;
                _lastCurX = cursor.X;
                _lastCurY = cursor.Y;
                _lastTx = tx;
                _lastTy = ty;
                _lastAx = tx;
                _lastAy = ty;
                _lastHintLen = hintLen;
                _lastHintHash = hintHash;
                _lastGap = gap;
                _lastCallUs = callUs;
                _lastLagX = lagX;
                _lastLagY = lagY;
                _hasPrev = true;

                switch (source)
                {
                    case "R":
                        _rCount++;
                        if (_lastRTick >= 0) _rIntervals.Add(ToMs(tick - _lastRTick));
                        _lastRTick = tick;
                        break;
                    case "T":
                        _tCount++;
                        if (_lastTTick >= 0)
                        {
                            var delta = ToMs(tick - _lastTTick);
                            _tIntervals.Add(delta);
                            if (delta > _maxTInterval) _maxTInterval = delta;
                        }
                        _lastTTick = tick;
                        break;
                    case "G":
                        _gCount++;
                        if (_lastGTick >= 0) _gIntervals.Add(ToMs(tick - _lastGTick));
                        _lastGTick = tick;
                        break;
                    case "D":
                        _dCount++;
                        if (_lastDTick >= 0) _dIntervals.Add(ToMs(tick - _lastDTick));
                        _lastDTick = tick;
                        break;
                }
            }
            catch (Exception ex)
            {
                AddAnomaly($"!! sample-exception {ex.GetType().Name}: {ex}");
            }
        }

        private readonly char[] _line = new char[512];

        private void AppendSample(double tMs, string source, int curX, int curY, int tx, int ty,
            int ax, int ay, int aw, int ah, int hintLen, uint hintHash, double gap, double callUs,
            bool moved, double lagX, double lagY, double applyErrX, double applyErrY,
            int curStepX, int curStepY, int tgtStepX, int tgtStepY)
        {
            var span = _line.AsSpan();
            var i = 0;
            i += WriteF(span[i..], tMs, "F3"); span[i++] = ',';
            span[i++] = string.IsNullOrEmpty(source) ? '?' : source[0]; span[i++] = ',';
            i += WriteI(span[i..], curX); span[i++] = ',';
            i += WriteI(span[i..], curY); span[i++] = ',';
            i += WriteI(span[i..], tx); span[i++] = ',';
            i += WriteI(span[i..], ty); span[i++] = ',';
            i += WriteI(span[i..], ax); span[i++] = ',';
            i += WriteI(span[i..], ay); span[i++] = ',';
            i += WriteI(span[i..], aw); span[i++] = ',';
            i += WriteI(span[i..], ah); span[i++] = ',';
            i += WriteI(span[i..], hintLen); span[i++] = ',';
            i += WriteI(span[i..], unchecked((int)hintHash)); span[i++] = ',';
            i += WriteF(span[i..], gap, "F3"); span[i++] = ',';
            i += WriteF(span[i..], callUs, "F1"); span[i++] = ',';
            span[i++] = moved ? '1' : '0'; span[i++] = ',';
            i += WriteF(span[i..], lagX, "F2"); span[i++] = ',';
            i += WriteF(span[i..], lagY, "F2"); span[i++] = ',';
            i += WriteF(span[i..], applyErrX, "F1"); span[i++] = ',';
            i += WriteF(span[i..], applyErrY, "F1"); span[i++] = ',';
            i += WriteI(span[i..], curStepX); span[i++] = ',';
            i += WriteI(span[i..], curStepY); span[i++] = ',';
            i += WriteI(span[i..], tgtStepX); span[i++] = ',';
            i += WriteI(span[i..], tgtStepY);
            _lineLength = i;
        }

        private int _lineLength;

        private static int WriteI(Span<char> dst, int value) =>
            value.TryFormat(dst, out var written, default, CultureInfo.InvariantCulture) ? written : 0;

        private static int WriteF(Span<char> dst, double value, string format) =>
            value.TryFormat(dst, out var written, format, CultureInfo.InvariantCulture) ? written : 0;

        private static uint Hash(string text)
        {
            var hash = 2166136261u;
            foreach (var ch in text) hash = unchecked((hash ^ ch) * 16777619u);
            return hash;
        }

        public void Event(string kind, string detail)
        {
            try
            {
                _events.WriteLine($"{NowMs,10:F3}  {kind,-12} {detail}");
            }
            catch (IOException)
            {
                // 诊断收尾刷盘失败不改变拖拽结果，异常在 Debug 输出里可见
            }
        }

        /// <summary>提示条变化（含窗口尺寸变化）——调用方已量好耗时。</summary>
        public void HintChanged(DragVisualOverlay? overlay, double setMs, double hintMs, string hint,
            Size sizeBefore, Size sizeAfter, string rectBefore, string rectAfter,
            string targetBefore, string? targetAfter)
        {
            _lastTargetChangeMs = NowMs;
            if (setMs > _maxSetMs) _maxSetMs = setMs;
            if (hintMs > _maxHintMs) _maxHintMs = hintMs;
            if (sizeBefore != sizeAfter) _hintResizes++;
            Event("hint",
                $"setMs={setMs:F2} hintMs={hintMs:F2} hintLen={hint.Length} " +
                $"size={sizeBefore.Width:F0}x{sizeBefore.Height:F0}->{sizeAfter.Width:F0}x{sizeAfter.Height:F0} " +
                $"rect={rectBefore}->{rectAfter} tgt={targetBefore}->{targetAfter ?? "-"} " +
                $"tgtY={overlay?.DebugWindowY ?? 0}");
        }

        private void AddAnomaly(string line)
        {
            if (_anomalies.Count < 80) _anomalies.Add(line);
            Event("anomaly", line);
        }

        private string DescribeWindow()
        {
            if (_hwnd == IntPtr.Zero) return "none";
            return $"{_overlay.DebugWindowX},{_overlay.DebugWindowY}," +
                   $"{_overlay.DebugBitmapWidth}x{_overlay.DebugBitmapHeight}";
        }

        public void Finish()
        {
            // 必须在写总结前停掉截屏线程/宿主轨迹：它们和下一次拖拽并行会互相拖慢、制造假偏移。
            try { _present?.Stop(); } catch (Exception) { /* best effort: stop the probe thread */ }
            try { _overlay.StopHostTrace(); } catch (Exception) { /* best effort: flush the host trace */ }
            try
            {
                var duration = NowMs;
                _gc0 = GC.CollectionCount(0) - _gc0;
                _gc1 = GC.CollectionCount(1) - _gc1;
                _gc2 = GC.CollectionCount(2) - _gc2;

                var summaryPath = Path.Combine(_folder, _stem + ".summary.txt");
                var sb = new StringBuilder();
                sb.AppendLine("LinkPocket drag diagnostics summary (TEMP-DRAG-DEBUG)");
                sb.AppendLine($"duration {duration:F1}ms   samples {_sampleCount}   moves {_moves}   hintChanges {_hintChanges}   hintResizes {_hintResizes}");
                sb.AppendLine($"sample interval p50={Pct(_gaps, 0.50):F2} p90={Pct(_gaps, 0.90):F2} p99={Pct(_gaps, 0.99):F2} max={_maxGap:F2}ms");
                sb.AppendLine($"gaps>8ms {_gapsOver8}  >12ms {_gapsOver12}  >16ms {_gapsOver16}  >33ms {_gapsOver33}");
                sb.AppendLine($"R render frame interval p50={Pct(_rIntervals, 0.50):F2} p90={Pct(_rIntervals, 0.90):F2} max={Max(_rIntervals):F2}ms (n={_rIntervals.Count})");
                sb.AppendLine($"T timer interval p50={Pct(_tIntervals, 0.50):F2} p90={Pct(_tIntervals, 0.90):F2} max={_maxTInterval:F2}ms (n={_tIntervals.Count})");
                sb.AppendLine($"G feedback interval p50={Pct(_gIntervals, 0.50):F2} p90={Pct(_gIntervals, 0.90):F2} max={Max(_gIntervals):F2}ms (n={_gIntervals.Count})");
                sb.AppendLine($"D delayed feedback interval p50={Pct(_dIntervals, 0.50):F2} p90={Pct(_dIntervals, 0.90):F2} max={Max(_dIntervals):F2}ms (n={_dIntervals.Count})");
                sb.AppendLine($"position call p50={Pct(_callUs, 0.50):F1} p90={Pct(_callUs, 0.90):F1} p99={Pct(_callUs, 0.99):F1} max={_maxCallUs:F1}us   slow(>1.5ms) {_slowMoves}");
                sb.AppendLine($"window move interval p50={Pct(_moveIntervals, 0.50):F2} p90={Pct(_moveIntervals, 0.90):F2} p99={Pct(_moveIntervals, 0.99):F2} max={Max(_moveIntervals):F2}ms (n={_moveIntervals.Count})");
                sb.AppendLine($"apply error max={_maxApplyErr:F1}px   count(|err|>=2px) {_applyErrors}   cursor lag max={_maxLag:F1}px");
                sb.AppendLine($"target jumps {_targetJumps} (>=8px step mismatch)   main-thread sample gaps {_stalls}   " +
                              $"max drop handling {_maxSetMs:F2}ms   max hint relayout {_maxHintMs:F2}ms");
                sb.AppendLine($"sample rate {(_sampleCount * 1000.0 / Math.Max(duration, 1)):F1}/s   R={_rCount} T={_tCount} G={_gCount} D={_dCount}   screen {_refreshHz}Hz");
                sb.AppendLine($"GC 0/1/2 = {_gc0}/{_gc1}/{_gc2}");
                sb.AppendLine("anomalies (max 80):");
                if (_anomalies.Count == 0) sb.AppendLine("  (none)");
                foreach (var line in _anomalies) sb.AppendLine("  " + line);
                AtomicFile.WriteAllText(summaryPath, sb.ToString());

                Event("finish", $"samples={_sampleCount} maxGap={_maxGap:F2} stalls={_stalls} jumps={_targetJumps} " +
                                $"applyErrors={_applyErrors} tMax={_maxTInterval:F2} noGc={_overlay.DebugNoGcActive}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"drag diagnostics finish failed: {ex}");
            }
            finally
            {
                try { _present?.Stop(); } catch (Exception) { /* best effort: stop the probe thread */ }   // 幂等：异常路径也保证截屏线程退出
                try { _overlay.StopHostTrace(); } catch (Exception) { /* best effort: flush the host trace */ }
                try { _samples.Flush(); _samples.Dispose(); } catch (IOException) { /* best effort: close the sample log */ }
                try { _events.Flush(); _events.Dispose(); } catch (IOException) { /* best effort: close the event log */ }
            }
        }

        private static double Pct(List<double> values, double p)
        {
            if (values.Count == 0) return 0;
            var copy = new List<double>(values);
            copy.Sort();
            var index = (int)Math.Clamp(Math.Round((copy.Count - 1) * p), 0, copy.Count - 1);
            return copy[index];
        }

        private static double Max(List<double> values)
        {
            var max = 0.0;
            foreach (var value in values) if (value > max) max = value;
            return max;
        }

    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hDc, int index);

    private const int VRefresh = 116;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
