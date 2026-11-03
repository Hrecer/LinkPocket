using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LinkPocket.Views.Browser;

/// <summary>
/// TEMP-DRAG-DEBUG（验收后删除）：拖拽浮层的**屏幕合成像素采样**。
///
/// <para>主线程的采样只能看到“最后一次摆位请求”和窗口矩形，看不见 DWM 合成后用户真正看到的画面；
/// 这里在抓取点画了洋红标记，按 ~500Hz 截取光标周围 120×120 的屏幕区域，检测标记质心。
/// 标记（浮层）与光标在同一帧的屏幕坐标差，就是用户感知到的延迟/抖动。</para>
/// </summary>
internal sealed class DragPresentProbe
{
    // 全局唯一存活探针：上一段拖拽若因异常没走到 Finish，下一段开始时也必须先把它停掉，
    // 否则两个 120×120 的屏幕 BitBlt 并行会把单次截屏从 4ms 拉到 8ms、采样率减半（测量混叠）。
    private static DragPresentProbe? _active;

    private readonly string _path;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly long _sessionStart;
    private readonly DragVisualOverlay _overlay;
    private readonly byte _markerB;
    private readonly byte _markerG;
    private readonly byte _markerR;
    private readonly DragDuplicationProbe? _duplication;

    public DragPresentProbe(string folder, string stem, long sessionStartQpc,
        DragVisualOverlay overlay, System.Windows.Media.Color markerColor)
    {
        _path = Path.Combine(folder, stem + ".present.csv");
        _sessionStart = sessionStartQpc;
        _overlay = overlay;
        _markerR = markerColor.R;
        _markerG = markerColor.G;
        _markerB = markerColor.B;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "LinkPocket.DragPresent",
        };
        // 先挂出唯一实例，再启动；上一段若还活着，这里等它退出，绝不允许两个探针并行。
        Interlocked.Exchange(ref _active, this)?.Stop();
        _thread.Start();
        // 并行再跑一份桌面复制同帧探针（.dd.csv）：它的每一帧自带同一帧的指针位置，
        // 用来校准 BitBlt 那一路"截屏时刻不可知"的系统误差；初始化失败不影响主探针。
        _duplication = DragDuplicationProbe.TryStart(folder, stem, sessionStartQpc, overlay, markerColor);
    }

    public void Stop()
    {
        _stop.Set();
        _duplication?.Stop();
        try
        {
            if (_thread.IsAlive) _thread.Join(500);
        }
        catch (ThreadStateException)
        {
            // 线程还没来得及 Start：_stop 已置位，它启动后会立刻退出。
        }
    }

    private void Run()
    {
        const int CapW = 140;
        const int CapH = 196;
        const int CardMarkerY = 48;
        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            memoryDc = CreateCompatibleDC(screenDc);
            var info = new BitmapInfo
            {
                bmiHeader = new BitmapInfoHeader
                {
                    biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    biWidth = CapW,
                    biHeight = -CapH,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                    biSizeImage = CapW * CapH * 4,
                },
            };
            dib = CreateDIBSection(memoryDc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return;
            SelectObject(memoryDc, dib);

            var pixels = new byte[CapW * CapH * 4];
            var mask = new bool[CapW * CapH];
            var seen = new bool[CapW * CapH];
            var stack = new int[CapW * CapH];
            var encoder = new UTF8Encoding(false);
            using var writer = new StreamWriter(
                new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024),
                encoder) { AutoFlush = false, NewLine = "\r\n" };
            writer.WriteLine("# tMs,curX,curY,markerX,markerY,count,captureUs,regionX,regionY," +
                             "curBeforeX,curBeforeY,capStartMs,capEndMs," +
                             "dwmComposeMs,dwmVBlankMs,dwmFrame,dwmLate,dwmMissed,dwmPeriodMs," +
                             "dwmFrameDisplayed,displayFrameMs," +
                             "cardX,cardY,curFrameX,curFrameY,applyAgeMs,anchorX,anchorY," +
                             "hintMarkerX,hintMarkerY,hintCount,hintDX,hintDY");

            var screenWidth = GetSystemMetrics(0);
            var screenHeight = GetSystemMetrics(1);
            var startTick = _sessionStart;
            var lastFlush = startTick;
            TimeBeginPeriod(1);
            try
            {
                while (!_stop.IsSet)
                {
                    var now = Stopwatch.GetTimestamp();
                    GetCursorPos(out var beforeCursor);
                    var left = Math.Clamp(beforeCursor.X - CapW / 2, 0, Math.Max(0, screenWidth - CapW));
                    var top = Math.Clamp(beforeCursor.Y - CardMarkerY, 0, Math.Max(0, screenHeight - CapH));
                    var cursorInCapX = beforeCursor.X - left;
                    var cursorInCapY = beforeCursor.Y - top;

                    var captureStart = Stopwatch.GetTimestamp();
                    BitBlt(memoryDc, 0, 0, CapW, CapH, screenDc, left, top, SrcCopy);
                    var captureEnd = Stopwatch.GetTimestamp();
                    // 整屏 BitBlt 会阻塞约 4–6ms（等合成）；截完立刻读光标，才能把标记和“同一时刻的指针”比较。
                    // 注意：BitBlt 偶尔会返回上一帧的前台缓冲（实测快速移动时约每百帧一次），此时 marker
                    // 会比 card/curFrame 晚一帧；这类孤立尖峰要用 host.csv 的 act=cur−anchor 复核，不能直接判卡片跳动。
                    GetCursorPos(out var cursor);
                    var t = (captureEnd - startTick) * 1000.0 / Stopwatch.Frequency;
                    var capStartMs = (captureStart - startTick) * 1000.0 / Stopwatch.Frequency;
                    var capEndMs = (captureEnd - startTick) * 1000.0 / Stopwatch.Frequency;
                    var dwmInfo = new DwmTimingInfo { cbSize = (uint)Marshal.SizeOf<DwmTimingInfo>() };
                    var dwmOk = DwmGetCompositionTimingInfo(IntPtr.Zero, ref dwmInfo) == 0;

                    // 同一帧的两种“位置”要放在同一条记录里：
                    // - displayQpc：BitBlt 拿到的这一帧的上屏时刻 = 最近一次 vblank；
                    // - cardQpc：这一帧里实际显示的卡片位置。DComp 的 visual 提交截止在 vblank 前约 0.05ms，
                    //   所以这一帧显示的就是 displayQpc 时刻宿主最后落位/提交的窗口位置；
                    // - curFrameX/Y：displayQpc 时刻的光标；
                    // - markerX/Y：截屏里卡片抓取锚点的实际像素（应 ≈ card + anchor）。
                    // 三者同帧相减才是用户眼里的“卡片 vs 光标”，不能用 BitBlt 结束后再读的光标。
                    var period = dwmOk ? (long)dwmInfo.qpcRefreshPeriod : 0;
                    var displayQpc = dwmOk ? (long)dwmInfo.qpcVBlank - period : 0;
                    var cardQpc = displayQpc;
                    if (dwmOk && displayQpc <= 0)
                    {
                        displayQpc = (long)dwmInfo.qpcFrameDisplayed;
                        if (displayQpc <= 0) displayQpc = (long)dwmInfo.qpcCompose - period;
                        cardQpc = displayQpc;
                    }
                    var anchorX = _overlay.DebugAnchorScreenX;
                    var anchorY = _overlay.DebugAnchorScreenY;
                    // BitBlt 可能截到 displayQpc 这一帧，也可能截到上一帧；两帧的候选都取出来，
                    // 等标记算出来后，用“标记 − 锚点”去匹配哪一帧的卡片落位，再用同帧光标。
                    (long Qpc, int CurX, int CurY, bool HasCur, int ActX, int ActY, long Since) cand0 = default;
                    (long Qpc, int CurX, int CurY, bool HasCur, int ActX, int ActY, long Since) cand1 = default;
                    var hasCand0 = false;
                    var hasCand1 = false;
                    if (dwmOk && cardQpc > 0)
                    {
                        hasCand0 = _overlay.DebugTryGetHostSampleAt(cardQpc,
                            out var c0x, out var c0y, out var h0, out var a0x, out var a0y, out var s0);
                        if (hasCand0) cand0 = (cardQpc, c0x, c0y, h0, a0x, a0y, s0);
                        if (period > 0 && cardQpc - period > 0)
                        {
                            hasCand1 = _overlay.DebugTryGetHostSampleAt(cardQpc - period,
                                out var c1x, out var c1y, out var h1, out var a1x, out var a1y, out var s1);
                            if (hasCand1) cand1 = (cardQpc - period, c1x, c1y, h1, a1x, a1y, s1);
                        }
                    }
                    Marshal.Copy(bits, pixels, 0, pixels.Length);
                    // 卡片标记（蓝）：抓取锚点。提示标记（洋红）：提示条左上角内 4pt。
                    // 两个标记在同一次 BitBlt 里，delta 的变化就是“药丸相对卡片”的真实位移。
                    var cardMarker = FindMarker(mask, seen, stack, pixels, CapW, CapH,
                        _markerB, _markerG, _markerR, cursorInCapX, cursorInCapY);
                    var count = cardMarker.Count;
                    var markerX = count > 0 ? left + cardMarker.X : -1;
                    var markerY = count > 0 ? top + cardMarker.Y : -1;
                    var hintMarker = count > 0
                        ? FindMarker(mask, seen, stack, pixels, CapW, CapH,
                            255, 0, 255, cardMarker.X - 30.0, cardMarker.Y + 52.0)
                        : default;
                    var hintCount = hintMarker.Count;
                    var hintMarkerX = hintCount > 0 ? left + hintMarker.X : -1;
                    var hintMarkerY = hintCount > 0 ? top + hintMarker.Y : -1;
                    var hintDX = count > 0 && hintCount > 0 ? hintMarkerX - markerX : -999;
                    var hintDY = count > 0 && hintCount > 0 ? hintMarkerY - markerY : -999;
                    var captureUs = (Stopwatch.GetTimestamp() - captureStart) * 1_000_000.0 / Stopwatch.Frequency;

                    var chosen = hasCand0 ? cand0 : cand1;
                    var chosenValid = hasCand0 || hasCand1;
                    if (hasCand0 && hasCand1 && count > 0)
                    {
                        var err0 = Math.Max(Math.Abs(markerX - (cand0.ActX + anchorX)),
                                            Math.Abs(markerY - (cand0.ActY + anchorY)));
                        var err1 = Math.Max(Math.Abs(markerX - (cand1.ActX + anchorX)),
                                            Math.Abs(markerY - (cand1.ActY + anchorY)));
                        // 只有当前帧明显对不上（>3px 栅格）且上一帧能对上时才回退，避免 1px 差把光标带到旧帧。
                        if (err0 > 3 && err1 <= 3) chosen = cand1;
                    }
                    var cardX = chosenValid ? chosen.ActX : -1;
                    var cardY = chosenValid ? chosen.ActY : -1;
                    var curFrameX = chosenValid && chosen.HasCur ? chosen.CurX : -1;
                    var curFrameY = chosenValid && chosen.HasCur ? chosen.CurY : -1;
                    var applyAgeMs = chosenValid && chosen.Since > 0
                        ? (chosen.Qpc - chosen.Since) * 1000.0 / Stopwatch.Frequency
                        : -1.0;
                    var chosenQpc = chosenValid ? chosen.Qpc : displayQpc;

                    writer.Write(t.ToString("F1", CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(cursor.X);
                    writer.Write(',');
                    writer.Write(cursor.Y);
                    writer.Write(',');
                    writer.Write(markerX);
                    writer.Write(',');
                    writer.Write(markerY);
                    writer.Write(',');
                    writer.Write(count);
                    writer.Write(',');
                    writer.Write(captureUs.ToString("F1", CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(left);
                    writer.Write(',');
                    writer.Write(top);
                    writer.Write(',');
                    writer.Write(beforeCursor.X);
                    writer.Write(',');
                    writer.Write(beforeCursor.Y);
                    writer.Write(',');
                    writer.Write(capStartMs.ToString("F3", CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(capEndMs.ToString("F3", CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(dwmOk
                        ? (((long)dwmInfo.qpcCompose - startTick) * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture)
                        : "-1");
                    writer.Write(',');
                    writer.Write(dwmOk
                        ? (((long)dwmInfo.qpcVBlank - startTick) * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture)
                        : "-1");
                    writer.Write(',');
                    writer.Write(dwmOk ? dwmInfo.cFrame : 0UL);
                    writer.Write(',');
                    writer.Write(dwmOk ? dwmInfo.cFramesLate : 0UL);
                    writer.Write(',');
                    writer.Write(dwmOk ? dwmInfo.cFramesMissed : 0UL);
                    writer.Write(',');
                    writer.Write(dwmOk
                        ? (dwmInfo.qpcRefreshPeriod * 1000.0 / Stopwatch.Frequency).ToString("F4", CultureInfo.InvariantCulture)
                        : "-1");
                    writer.Write(',');
                    writer.Write(dwmOk ? dwmInfo.cFrameDisplayed : 0UL);
                    writer.Write(',');
                    writer.Write(dwmOk && chosenQpc > 0
                        ? ((chosenQpc - startTick) * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture)
                        : "-1");
                    writer.Write(',');
                    writer.Write(cardX); writer.Write(',');
                    writer.Write(cardY); writer.Write(',');
                    writer.Write(curFrameX); writer.Write(',');
                    writer.Write(curFrameY); writer.Write(',');
                    writer.Write(applyAgeMs.ToString("F3", CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(anchorX.ToString("F2", CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(anchorY.ToString("F2", CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(hintMarkerX); writer.Write(',');
                    writer.Write(hintMarkerY); writer.Write(',');
                    writer.Write(hintCount); writer.Write(',');
                    writer.Write(hintDX); writer.Write(',');
                    writer.WriteLine(hintDY);

                    if (now - lastFlush >= Stopwatch.Frequency / 2)
                    {
                        writer.Flush();
                        lastFlush = now;
                    }

                    var elapsedMs = (Stopwatch.GetTimestamp() - now) * 1000.0 / Stopwatch.Frequency;
                    var sleep = (int)Math.Floor(2.0 - elapsedMs);
                    if (sleep > 0) Thread.Sleep(sleep);
                }
            }
            finally
            {
                TimeEndPeriod(1);
                writer.Flush();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"present probe failed: {ex}");
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // TEMP-DRAG-DEBUG（验收后删除）：dwmapi.h 使用 pshpack1，DWM_TIMING_INFO 必须是 Pack=1、cbSize=292。
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DwmTimingInfo
    {
        public uint cbSize;
        public uint rateRefreshNumerator;
        public uint rateRefreshDenominator;
        public ulong qpcRefreshPeriod;
        public uint rateComposeNumerator;
        public uint rateComposeDenominator;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose;
        public ulong cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cRefreshFrameDisplayed;
        public ulong cFrameComplete;
        public ulong qpcFrameComplete;
        public ulong cFramePending;
        public ulong qpcFramePending;
        public ulong cFramesDisplayed;
        public ulong cFramesComplete;
        public ulong cFramesPending;
        public ulong cFramesAvailable;
        public ulong cFramesDropped;
        public ulong cFramesMissed;
        public ulong cRefreshNextDisplayed;
        public ulong cRefreshNextPresented;
        public ulong cRefreshesDisplayed;
        public ulong cRefreshesPresented;
        public ulong cRefreshStarted;
        public ulong cPixelsReceived;
        public ulong cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hWnd, ref DwmTimingInfo timingInfo);

    private const uint SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public int biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
        public uint bmiColors;
    }


    internal static (int X, int Y, int Count) FindMarker(bool[] mask, bool[] seen, int[] stack,
        byte[] pixels, int w, int h, byte b, byte g, byte r, double targetX, double targetY)
    {
        Array.Clear(seen);
        for (var i = 0; i < mask.Length; i++)
        {
            var p = i * 4;
            mask[i] = Math.Abs(pixels[p] - b) <= 48
                      && Math.Abs(pixels[p + 1] - g) <= 48
                      && Math.Abs(pixels[p + 2] - r) <= 48;
        }

        long sumX = 0, sumY = 0;
        var count = 0;
        var bestDist = double.MaxValue;
        for (var seed = 0; seed < mask.Length; seed++)
        {
            if (!mask[seed] || seen[seed]) continue;
            var topOfStack = 0;
            stack[topOfStack++] = seed;
            seen[seed] = true;
            long sx = 0, sy = 0;
            var area = 0;
            int minX = w, maxX = -1, minY = h, maxY = -1;
            while (topOfStack > 0)
            {
                var p = stack[--topOfStack];
                var px = p % w;
                var py = p / w;
                area++;
                sx += px;
                sy += py;
                minX = Math.Min(minX, px);
                maxX = Math.Max(maxX, px);
                minY = Math.Min(minY, py);
                maxY = Math.Max(maxY, py);
                for (var oy = -1; oy <= 1; oy++)
                {
                    var ny = py + oy;
                    if (ny < 0 || ny >= h) continue;
                    for (var ox = -1; ox <= 1; ox++)
                    {
                        var nx = px + ox;
                        if (nx < 0 || nx >= w) continue;
                        var np = ny * w + nx;
                        if (!mask[np] || seen[np]) continue;
                        seen[np] = true;
                        stack[topOfStack++] = np;
                    }
                }
            }

            var bw = maxX - minX + 1;
            var bh = maxY - minY + 1;
            if (area < 9 || area > 160 || bw > 14 || bh > 14 || bw < 3 || bh < 3) continue;
            var cx = sx / (double)area;
            var cy = sy / (double)area;
            var dist = (cx - targetX) * (cx - targetX) + (cy - targetY) * (cy - targetY);
            if (dist >= bestDist) continue;
            bestDist = dist;
            sumX = sx;
            sumY = sy;
            count = area;
        }

        return count > 0
            ? ((int)Math.Round(sumX / (double)count), (int)Math.Round(sumY / (double)count), count)
            : default;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hDc, ref BitmapInfo info,
        uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
}
