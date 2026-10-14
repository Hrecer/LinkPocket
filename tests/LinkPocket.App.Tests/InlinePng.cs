using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinkPocket.App.Tests;

/// <summary>
/// 现场编码一张 16×16 PNG 的内联 <c>data:</c> URI（与探针 <c>RunFaviconRenderCheck</c> 同口径：
/// 不硬编码 base64——一有笔误就会把"样本数据坏"误判成"解码坏"）。
/// 需在 STA 线程（<see cref="StaPump"/>）上调用：RenderTargetBitmap / PngBitmapEncoder 是 WPF 成像对象。
/// </summary>
internal static class InlinePng
{
    public static string Sample16x16()
    {
        var source = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        source.Render(new DrawingVisual());
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }
}
