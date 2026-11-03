using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D3D11Api = Vortice.Direct3D11.D3D11;

namespace LinkPocket.Views.Browser;

/// <summary>
/// TEMP-DRAG-DEBUG（验收后删除）：桌面复制（Desktop Duplication）同帧探针。
///
/// <para>BitBlt 路线有两个无法消除的歧义：①截屏不包含硬件光标，只能截完再 GetCursorPos，
/// 截屏耗时的位移会被算成卡片滞后；②BitBlt 返回哪一帧取决于内部合成时机，实测 4ms/8ms
/// 两种节奏会把同一段轨迹测出 4–6px 的不同结果。桌面复制的每一帧自带同一帧的
/// <c>DXGI_OUTDUPL_FRAME_INFO.PointerPosition</c> 与 <c>LastPresentTime</c>，
/// 图像里的卡片标记与这个指针位置就是用户在同一帧看到的两点。</para>
/// </summary>
internal sealed class DragDuplicationProbe
{
    private readonly string _path;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly long _sessionStart;
    private readonly DragVisualOverlay _overlay;
    private readonly byte _markerB;
    private readonly byte _markerG;
    private readonly byte _markerR;

    private DragDuplicationProbe(string folder, string stem, long sessionStartQpc,
        DragVisualOverlay overlay, System.Windows.Media.Color markerColor)
    {
        _path = Path.Combine(folder, stem + ".dd.csv");
        _sessionStart = sessionStartQpc;
        _overlay = overlay;
        _markerR = markerColor.R;
        _markerG = markerColor.G;
        _markerB = markerColor.B;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "LinkPocket.DragDd",
        };
    }

    /// <summary>启动同帧探针；本机不支持桌面复制时返回 null，调用方退回 BitBlt 探针。</summary>
    public static DragDuplicationProbe? TryStart(string folder, string stem, long sessionStartQpc,
        DragVisualOverlay overlay, System.Windows.Media.Color markerColor)
    {
        try
        {
            var probe = new DragDuplicationProbe(folder, stem, sessionStartQpc, overlay, markerColor);
            probe._thread.Start();
            return probe;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"drag dd probe unavailable: {ex}");
            return null;
        }
    }

    public void Stop()
    {
        _stop.Set();
        try
        {
            if (_thread.IsAlive) _thread.Join(500);
        }
        catch (ThreadStateException)
        {
        }
    }

    private void Run()
    {
        const int CapW = 140;
        const int CapH = 196;
        const int CardMarkerY = 48;
        ID3D11Device? device = null;
        IDXGIOutputDuplication? duplication = null;
        ID3D11Texture2D? staging = null;
        ID3D11Texture2D? hintStaging = null;
        try
        {
            device = CreateDevice();
            var output = FindPrimaryOutput(device, out var outLeft, out var outTop, out var outWidth, out var outHeight);
            if (output == null) return;

            using var context = device.ImmediateContext;
            var stagingFormat = Format.Unknown;

            duplication = output.QueryInterface<IDXGIOutput1>().DuplicateOutput(device);
            var pixels = new byte[CapW * CapH * 4];
            var mask = new bool[CapW * CapH];
            var seen = new bool[CapW * CapH];
            var stack = new int[CapW * CapH];
            // 提示条单独裁一小块（不放大主裁剪，避免回读带宽把自己变成新的干扰源）。
            const int HintCap = 96;
            var hintPixels = new byte[HintCap * HintCap * 4];
            var hintMask = new bool[HintCap * HintCap];
            var hintSeen = new bool[HintCap * HintCap];
            var hintStack = new int[HintCap * HintCap];
            var hintStagingFormat = Format.Unknown;
            var encoder = new UTF8Encoding(false);
            using var writer = new StreamWriter(
                new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024),
                encoder) { AutoFlush = false, NewLine = "\r\n" };
            writer.WriteLine("# tMs,lastPresentMs,lastMouseMs,accFrames,ptrX,ptrY,ptrVisible," +
                             "markerX,markerY,count,regionX,regionY," +
                             "hintMarkerX,hintMarkerY,hintCount,hintDX,hintDY," +
                             "slipX,slipY,cardX,cardY,cardApplyAgeMs,anchorX,anchorY");

            var lastFlush = Stopwatch.GetTimestamp();
            var lastPointerX = int.MinValue;
            var lastPointerY = int.MinValue;
            var accessLost = 0;
            while (!_stop.IsSet)
            {
                var result = duplication.AcquireNextFrame(8, out var frameInfo, out var resource);
                if (result.Failure)
                {
                    var code = result.Code;
                    if (code == Vortice.DXGI.ResultCode.WaitTimeout.Code) continue;
                    if (code == Vortice.DXGI.ResultCode.AccessLost.Code ||
                        code == Vortice.DXGI.ResultCode.InvalidCall.Code)
                    {
                        if (++accessLost > 3) return;
                        duplication.Dispose();
                        duplication = output.QueryInterface<IDXGIOutput1>().DuplicateOutput(device);
                        continue;
                    }
                    return;
                }

                try
                {
                    var pointer = frameInfo.PointerPosition;
                    var visible = pointer.Visible;
                    if (visible)
                    {
                        lastPointerX = pointer.Position.X;
                        lastPointerY = pointer.Position.Y;
                    }
                    if (lastPointerX == int.MinValue) continue;

                    var left = Math.Clamp(lastPointerX - CapW / 2, 0, Math.Max(0, outWidth - CapW));
                    var top = Math.Clamp(lastPointerY - CardMarkerY, 0, Math.Max(0, outHeight - CapH));

                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    var textureFormat = texture.Description.Format;
                    var bgra = textureFormat is Format.B8G8R8A8_UNorm or Format.B8G8R8X8_UNorm;
                    if (staging == null || stagingFormat != textureFormat)
                    {
                        staging?.Dispose();
                        stagingFormat = textureFormat;
                        staging = device.CreateTexture2D(new Texture2DDescription
                        {
                            Width = CapW,
                            Height = CapH,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = textureFormat,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging,
                            BindFlags = BindFlags.None,
                            CPUAccessFlags = CpuAccessFlags.Read,
                            MiscFlags = ResourceOptionFlags.None,
                        });
                    }
                    context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0,
                        new Box(left, top, 0, left + CapW, top + CapH, 1));
                    var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        for (var y = 0; y < CapH; y++)
                        {
                            Marshal.Copy((IntPtr)(map.DataPointer.ToInt64() + y * map.RowPitch),
                                pixels, y * CapW * 4, CapW * 4);
                        }
                    }
                    finally
                    {
                        context.Unmap(staging, 0);
                    }

                    var anchorX = _overlay.DebugAnchorScreenX;
                    var anchorY = _overlay.DebugAnchorScreenY;
                    var cardMarker = DragPresentProbe.FindMarker(mask, seen, stack, pixels, CapW, CapH,
                        bgra ? _markerB : _markerR,
                        _markerG,
                        bgra ? _markerR : _markerB,
                        lastPointerX - left, lastPointerY - top);
                    var count = cardMarker.Count;
                    var markerX = count > 0 ? outLeft + left + cardMarker.X : -1;
                    var markerY = count > 0 ? outTop + top + cardMarker.Y : -1;
                    var hintCount = 0;
                    var hintMarkerX = -1;
                    var hintMarkerY = -1;
                    if (_overlay.DebugHintVisible)
                    {
                        // 提示条位图左上角 = DebugHintScreenX/Y；洋红标记在位图内 20 DIP（=30px @150%）。
                        // 用 96×96 小裁剪包住“预期标记点”：药丸即使晚一帧（≤10px）也不会跑出裁剪。
                        var expectedX = _overlay.DebugHintScreenX - outLeft + 30;
                        var expectedY = _overlay.DebugHintScreenY - outTop + 30;
                        var hx = Math.Clamp(expectedX - HintCap / 2, 0, Math.Max(0, outWidth - HintCap));
                        var hy = Math.Clamp(expectedY - HintCap / 2, 0, Math.Max(0, outHeight - HintCap));
                        if (hintStaging == null || hintStagingFormat != textureFormat)
                        {
                            hintStaging?.Dispose();
                            hintStagingFormat = textureFormat;
                            hintStaging = device.CreateTexture2D(new Texture2DDescription
                            {
                                Width = HintCap,
                                Height = HintCap,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = textureFormat,
                                SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Staging,
                                BindFlags = BindFlags.None,
                                CPUAccessFlags = CpuAccessFlags.Read,
                                MiscFlags = ResourceOptionFlags.None,
                            });
                        }
                        context.CopySubresourceRegion(hintStaging, 0, 0, 0, 0, texture, 0,
                            new Box(hx, hy, 0, hx + HintCap, hy + HintCap, 1));
                        var hmap = context.Map(hintStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            for (var y = 0; y < HintCap; y++)
                            {
                                Marshal.Copy((IntPtr)(hmap.DataPointer.ToInt64() + y * hmap.RowPitch),
                                    hintPixels, y * HintCap * 4, HintCap * 4);
                            }
                        }
                        finally
                        {
                            context.Unmap(hintStaging, 0);
                        }
                        var hintMarker = DragPresentProbe.FindMarker(hintMask, hintSeen, hintStack,
                            hintPixels, HintCap, HintCap, 255, 0, 255,
                            expectedX - hx, expectedY - hy);
                        hintCount = hintMarker.Count;
                        if (hintCount > 0)
                        {
                            hintMarkerX = outLeft + hx + hintMarker.X;
                            hintMarkerY = outTop + hy + hintMarker.Y;
                        }
                    }
                    var hintDX = count > 0 && hintCount > 0 ? hintMarkerX - markerX : -999;
                    var hintDY = count > 0 && hintCount > 0 ? hintMarkerY - markerY : -999;
                    var slipX = count > 0 ? markerX - (outLeft + lastPointerX) : -999;
                    var slipY = count > 0 ? markerY - (outTop + lastPointerY) : -999;

                    var presentQpc = frameInfo.LastPresentTime > 0
                        ? (long)frameInfo.LastPresentTime
                        : Stopwatch.GetTimestamp();
                    var tMs = (presentQpc - _sessionStart) * 1000.0 / Stopwatch.Frequency;
                    var lastPresentMs = frameInfo.LastPresentTime > 0
                        ? (frameInfo.LastPresentTime - _sessionStart) * 1000.0 / Stopwatch.Frequency
                        : -1.0;
                    var lastMouseMs = frameInfo.LastMouseUpdateTime > 0
                        ? (frameInfo.LastMouseUpdateTime - _sessionStart) * 1000.0 / Stopwatch.Frequency
                        : -1.0;
                    var cardX = -1;
                    var cardY = -1;
                    var applyAgeMs = -1.0;
                    if (_overlay.DebugTryGetHostSampleAt(presentQpc,
                            out _, out _, out _, out var actX, out var actY, out var since))
                    {
                        cardX = actX;
                        cardY = actY;
                        if (since > 0) applyAgeMs = (presentQpc - since) * 1000.0 / Stopwatch.Frequency;
                    }

                    writer.Write(tMs.ToString("F1", CultureInfo.InvariantCulture)); writer.Write(',');
                    writer.Write(lastPresentMs.ToString("F1", CultureInfo.InvariantCulture)); writer.Write(',');
                    writer.Write(lastMouseMs.ToString("F1", CultureInfo.InvariantCulture)); writer.Write(',');
                    writer.Write(frameInfo.AccumulatedFrames); writer.Write(',');
                    writer.Write(lastPointerX); writer.Write(',');
                    writer.Write(lastPointerY); writer.Write(',');
                    writer.Write(visible ? 1 : 0); writer.Write(',');
                    writer.Write(markerX); writer.Write(',');
                    writer.Write(markerY); writer.Write(',');
                    writer.Write(count); writer.Write(',');
                    writer.Write(outLeft + left); writer.Write(',');
                    writer.Write(outTop + top); writer.Write(',');
                    writer.Write(hintMarkerX); writer.Write(',');
                    writer.Write(hintMarkerY); writer.Write(',');
                    writer.Write(hintCount); writer.Write(',');
                    writer.Write(hintDX); writer.Write(',');
                    writer.Write(hintDY); writer.Write(',');
                    writer.Write(slipX); writer.Write(',');
                    writer.Write(slipY); writer.Write(',');
                    writer.Write(cardX); writer.Write(',');
                    writer.Write(cardY); writer.Write(',');
                    writer.Write(applyAgeMs.ToString("F3", CultureInfo.InvariantCulture)); writer.Write(',');
                    writer.Write(anchorX.ToString("F2", CultureInfo.InvariantCulture)); writer.Write(',');
                    writer.WriteLine(anchorY.ToString("F2", CultureInfo.InvariantCulture));

                    var now = Stopwatch.GetTimestamp();
                    if (now - lastFlush >= Stopwatch.Frequency / 2)
                    {
                        writer.Flush();
                        lastFlush = now;
                    }
                }
                finally
                {
                    duplication.ReleaseFrame();
                }
            }
            writer.Flush();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"drag dd probe failed: {ex}");
        }
        finally
        {
            hintStaging?.Dispose();
            staging?.Dispose();
            duplication?.Dispose();
            device?.Dispose();
        }
    }

    private static ID3D11Device CreateDevice()
    {
        try
        {
            return D3D11Api.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 });
        }
        catch (Exception)
        {
            return D3D11Api.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 });
        }
    }

    private static IDXGIOutput? FindPrimaryOutput(ID3D11Device device,
        out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        GetCursorPos(out var cursor);
        IDXGIOutput? first = null;
        for (uint i = 0; ; i++)
        {
            var result = adapter.EnumOutputs(i, out var output);
            if (result.Failure || output == null) break;
            var desc = output.Description;
            var bounds = desc.DesktopCoordinates;
            var contains = desc.AttachedToDesktop &&
                           cursor.X >= bounds.Left && cursor.X < bounds.Right &&
                           cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
            if (!contains)
            {
                if (first == null) first = output;
                else output.Dispose();
                continue;
            }
            first?.Dispose();
            if (desc.Rotation != ModeRotation.Identity)
            {
                output.Dispose();
                return null;
            }
            left = bounds.Left;
            top = bounds.Top;
            width = bounds.Right - bounds.Left;
            height = bounds.Bottom - bounds.Top;
            return output;
        }
        if (first == null) return null;
        var fallback = first.Description;
        if (fallback.Rotation != ModeRotation.Identity)
        {
            first.Dispose();
            return null;
        }
        left = fallback.DesktopCoordinates.Left;
        top = fallback.DesktopCoordinates.Top;
        width = fallback.DesktopCoordinates.Right - left;
        height = fallback.DesktopCoordinates.Bottom - top;
        return first;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
}
