using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LinkPocket.Composition;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 桌面能力桥的接线：无头宿主（CLI / MCP）发现桌面宿主后，必须装配同一组
/// render / verify / SVG 钩子；桌面宿主不在时保持 null（命令结果如实上报能力不可用）。
/// 这里用一条假管道钉住握手与钩子生命周期，不启动真实浏览器。
/// </summary>
public class CapabilityBridgeTests
{
    [Fact]
    public async Task 无头宿主_连接桌面能力桥后装配同一组钩子_释放即还原()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lp-bridge-{Guid.NewGuid():N}", "linkpocket.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var pipeName = HostCapabilityBridge.PipeNameFor(dbPath);
        var listening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var server = Task.Run(async () =>
        {
            using var pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            listening.SetResult(true);
            await pipe.WaitForConnectionAsync(cts.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(cts.Token);
            Assert.NotNull(line);
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new HostCapabilityBridge.Response(
                    Ok: true, Error: null, Reason: null, Title: null, Description: null,
                    FaviconUrl: null, PngBase64: null, Render: true, Verify: true, Svg: true, ElapsedMs: 1),
                HostCapabilityBridge.JsonOptions));
        });

        await listening.Task;
        try
        {
            using var host = new EngineHost(dbPath);
            Assert.NotNull(PageRenderHook.Renderer);
            Assert.NotNull(PageRenderHook.InteractiveRenderer);
            Assert.NotNull(FaviconCache.SvgRasterizer);

            host.Dispose();
            Assert.Null(PageRenderHook.Renderer);
            Assert.Null(PageRenderHook.InteractiveRenderer);
            Assert.Null(FaviconCache.SvgRasterizer);
        }
        finally
        {
            PageRenderHook.Renderer = null;
            PageRenderHook.InteractiveRenderer = null;
            FaviconCache.SvgRasterizer = null;
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task 部分能力_只装配真实可用的钩子()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lp-bridge-{Guid.NewGuid():N}", "linkpocket.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var pipeName = HostCapabilityBridge.PipeNameFor(dbPath);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = StartFakeServerAsync(pipeName, render: false, verify: true, svg: false, cts.Token);
        await server.Ready;


        try
        {
            using var host = new EngineHost(dbPath);
            Assert.Null(PageRenderHook.Renderer);
            Assert.NotNull(PageRenderHook.InteractiveRenderer);
            Assert.Null(FaviconCache.SvgRasterizer);

            host.Dispose();
            Assert.Null(PageRenderHook.InteractiveRenderer);
        }
        finally
        {
            PageRenderHook.Renderer = null;
            PageRenderHook.InteractiveRenderer = null;
            FaviconCache.SvgRasterizer = null;
            await server.Server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task 提供者稍后启动_首次真正需要时才连接并装配()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lp-bridge-{Guid.NewGuid():N}", "linkpocket.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var pipeName = HostCapabilityBridge.PipeNameFor(dbPath);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var host = new EngineHost(dbPath);
        Assert.Null(PageRenderHook.Renderer);   // 启动时没有提供者

        var server = StartFakeServerAsync(pipeName, render: true, verify: true, svg: true, cts.Token);
        await server.Ready;

        try
        {
            Assert.True(await PageRenderHook.EnsureAsync(CancellationToken.None));
            Assert.NotNull(PageRenderHook.Renderer);
            Assert.NotNull(PageRenderHook.InteractiveRenderer);
            Assert.NotNull(FaviconCache.SvgRasterizer);
        }
        finally
        {
            PageRenderHook.Renderer = null;
            PageRenderHook.InteractiveRenderer = null;
            FaviconCache.SvgRasterizer = null;
            await server.Server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static (Task Server, Task Ready) StartFakeServerAsync(
        string pipeName, bool render, bool verify, bool svg, CancellationToken ct)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            ready.SetResult(true);
            await pipe.WaitForConnectionAsync(ct);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(ct);
            Assert.NotNull(line);
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new HostCapabilityBridge.Response(
                    Ok: true, Error: null, Reason: null, Title: null, Description: null,
                    FaviconUrl: null, PngBase64: null, Render: render, Verify: verify, Svg: svg, ElapsedMs: 1),
                HostCapabilityBridge.JsonOptions));
        });

        return (server, ready.Task);
    }
}
