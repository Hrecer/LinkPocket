using System.IO;
using System.Windows;
using System.Windows.Media;
using LinkPocket.Contracts;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LinkPocket.Services;

/// <summary>
/// SVG 图标光栅化（WebView2）：把站点声明的 <c>&lt;link rel="icon" type="image/svg+xml"&gt;</c> 画成 PNG 字节，
/// 交给 <see cref="FaviconCache.SvgRasterizer"/>。WPF 的 <c>BitmapImage</c> 解不了 SVG，而现代文档站/组件库站
/// 常常**只**给一个 SVG 图标（站点根 <c>/favicon.ico</c> 反而是 404）——不画这一下，那些站在列表里就没有图标。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用浏览器内核而不是引一个 SVG 库</b>：宿主已经带着 WebView2（元数据抓取兜底在用），
/// 多引一个 SVG 解析/渲染包只为画一张 128px 的图，不划算；浏览器内核画的还就是站点作者预期的样子。
/// </para>
/// <para>
/// <b>纪律</b>：串行（浏览器实例不可并发）、自带超时、失败一律返回 <c>null</c>
/// （调用方随即回落到站点根 / 聚合源，绝不把 SVG 原文当位图落盘）；用**独立 profile**，
/// 不与元数据渲染共用——同一个 user-data 目录同时开两个环境会互相抢锁。
/// </para>
/// </remarks>
internal sealed class SvgIconRasterizer : IDisposable
{
    /// <summary>画布边长：够 <c>DecodePixelWidth = 48</c> 的界面用，也不至于把 SVG 画糊。</summary>
    private const int CanvasSize = 128;

    /// <summary>单张图的上限：SVG 通常几百字节到几 KB，画出来也就几 KB。</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<CoreWebView2Environment?>? _environment;
    private bool _unavailable;

    public async Task<byte[]?> RasterizeAsync(string url, CancellationToken ct)
    {
        if (_unavailable || !Uri.TryCreate(url, UriKind.Absolute, out _)) return null;

        // 串行：一个 UI 线程同时只能伺候一个浏览器实例；等不到就放弃（调用方有回落）
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(Budget.TotalSeconds), ct).ConfigureAwait(false)) return null;
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return null;

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(Budget);
            return await dispatcher.InvokeAsync(() => RenderOnUiThreadAsync(url, budget.Token)).Task.Unwrap()
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LpLog.Warn($"svg icon rasterize failed: {Short(url)}", ex, category: "app.favicon");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]?> RenderOnUiThreadAsync(string url, CancellationToken ct)
    {
        Window? window = null;
        WebView2? view = null;
        try
        {
            var environment = await GetEnvironmentAsync().ConfigureAwait(true);
            if (environment is null) return null;

            window = new Window
            {
                Width = CanvasSize,
                Height = CanvasSize,
                Left = -4000,
                Top = -4000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Background = Brushes.Transparent,
                Title = "LinkPocket icon raster",
            };
            view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
            window.Content = view;
            window.Show();

            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.Navigate(url);

            // 导航失败（站点把 .svg 也按反爬拦掉、回一张错误页）就不能截：
            // 否则截到的是"错误页的整屏"，界面上就是一张带滚动条的黑色方块。
            if (!await loaded.Task.WaitAsync(Budget, ct).ConfigureAwait(true)) return null;

            // 再确认一次根元素真的是 <svg>：错误页 / 登录页的根是 <html>，一律不算图标
            var tag = await core.ExecuteScriptAsync("document.documentElement.tagName").ConfigureAwait(true);
            if (!tag.Contains("svg", StringComparison.OrdinalIgnoreCase)) return null;

            // 视图里不留滚动条与边距：只截图形本身
            await core.ExecuteScriptAsync(
                "document.documentElement.style.margin='0';document.documentElement.style.overflow='hidden';")
                .ConfigureAwait(true);

            using var png = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
            return png.Length > 0 ? png.ToArray() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            try { view?.Dispose(); } catch { /* 实例已关闭时再释放会抛，与结果无关 */ }
            try { window?.Close(); } catch { /* 窗口已关闭时再关会抛，与结果无关 */ }
        }
    }

    private Task<CoreWebView2Environment?> GetEnvironmentAsync()
        => _environment ??= CreateEnvironmentAsync();

    private async Task<CoreWebView2Environment?> CreateEnvironmentAsync()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            _unavailable = true;
            LpLog.Warn("WebView2 runtime not found; svg icon rasterizing disabled", ex, category: "app.favicon");
            return null;
        }

        try
        {
            return await CoreWebView2Environment.CreateAsync(null, ProfileDirectory).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _unavailable = true;
            LpLog.Warn("WebView2 environment creation failed; svg icon rasterizing disabled", ex, category: "app.favicon");
            return null;
        }
    }

    /// <summary>独立 profile：不与元数据渲染共用（同一 user-data 目录开两个环境会抢锁）。</summary>
    private static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkPocket", "icon-profile");

    private static string Short(string url) => url.Length > 90 ? url[..90] : url;

    public void Dispose() => _gate.Dispose();
}
