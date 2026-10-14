using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LinkPocket.Contracts;
using LinkPocket.Services;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 更新服务：更新源解析 / 资产挑选 / digest 校验 / 解包结构校验 / 暂存区布局 / 上次结果消费——
/// 网络以注入的假 handler 替代（不真连 GitHub），文件全部落临时安装根（结束清理）。
/// </summary>
public class UpdateServiceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(Respond(request));
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lp-update-svc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* 尽力而为 */ }
        try { Directory.Delete(UpdateStaging.StagingRootFor(root), recursive: true); } catch { /* 尽力而为 */ }
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string FeedJson(string tag, bool prerelease, bool draft, params (string Name, string Url, string? Digest)[] assets)
    {
        var assetArray = string.Join(",", Array.ConvertAll(assets, a =>
            $"{{\"name\":\"{a.Name}\",\"browser_download_url\":\"{a.Url}\",\"size\":1234"
            + (a.Digest is null ? "" : $",\"digest\":\"{a.Digest}\"") + "}"));
        return $"{{ \"tag_name\": \"{tag}\", \"prerelease\": {(prerelease ? "true" : "false")}, "
               + $"\"draft\": {(draft ? "true" : "false")}, \"body\": \"notes\", \"assets\": [{assetArray}] }}";
    }

    private static string CreatePackageZip(byte[]? stamp = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lp-update-zip-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        Add("LinkPocket.exe", "new-app");
        Add("LinkPocket-Update.exe", "updater-binary");
        Add("update/skip-install.txt", "");
        if (stamp is not null)
        {
            var entry = zip.CreateEntry("stamp.bin");
            using var stream = entry.Open();
            stream.Write(stamp, 0, stamp.Length);
        }

        return path;
    }

    // —— 检查解析 ——

    [Fact]
    public async Task 检查_发现新版本_解析版本资产与digest()
    {
        var root = NewRoot();
        try
        {
            var handler = new StubHandler
            {
                Respond = _ => Json(FeedJson("v4.0.0", false, false,
                    ("LinkPocket_v4.0.0.zip", "https://example.invalid/pkg.zip", "sha256:abcd"))),
            };
            var service = new UpdateService(root, "3.2.4", new HttpClient(handler));

            var result = await service.CheckAsync();

            Assert.Equal(UpdateCheckStatus.Available, result.Status);
            Assert.Equal("4.0.0", result.LatestVersion);
            Assert.Equal("https://example.invalid/pkg.zip", result.DownloadUrl);
            Assert.Equal("abcd", result.Sha256Digest);   // "sha256:" 前缀剥掉
            Assert.Equal("notes", result.Notes);
            Assert.Equal(result, service.LastCheck);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 检查_预发布与草稿跳过()
    {
        var root = NewRoot();
        try
        {
            var handler = new StubHandler { Respond = _ => Json(FeedJson("v9.9.9", true, false)) };
            var service = new UpdateService(root, "3.2.4", new HttpClient(handler));

            Assert.Equal(UpdateCheckStatus.UpToDate, (await service.CheckAsync()).Status);

            handler.Respond = _ => Json(FeedJson("v9.9.9", false, true));
            Assert.Equal(UpdateCheckStatus.UpToDate, (await service.CheckAsync()).Status);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 检查_旧tag视为已是最新()
    {
        var root = NewRoot();
        try
        {
            var handler = new StubHandler { Respond = _ => Json(FeedJson("v1.0.0", false, false)) };
            var service = new UpdateService(root, "3.2.4", new HttpClient(handler));

            var result = await service.CheckAsync();

            Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
            Assert.Equal("1.0.0", result.LatestVersion);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 检查_缺资产即失败()
    {
        var root = NewRoot();
        try
        {
            var handler = new StubHandler { Respond = _ => Json(FeedJson("v4.0.0", false, false, ("other.zip", "https://example.invalid/o.zip", null))) };
            var service = new UpdateService(root, "3.2.4", new HttpClient(handler));

            var result = await service.CheckAsync();

            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
            Assert.Equal(UpdateErrorCodes.Release, result.Error);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 检查_HTTP错误即失败()
    {
        var root = NewRoot();
        try
        {
            var handler = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
            var service = new UpdateService(root, "3.2.4", new HttpClient(handler));

            var result = await service.CheckAsync();

            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
            Assert.Equal(UpdateErrorCodes.Http, result.Error);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 检查_网络异常即失败()
    {
        var root = NewRoot();
        try
        {
            var handler = new StubHandler { Respond = _ => throw new HttpRequestException("connection refused") };
            var service = new UpdateService(root, "3.2.4", new HttpClient(handler));

            var result = await service.CheckAsync();

            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
            Assert.Equal(UpdateErrorCodes.Network, result.Error);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 检查_更新源可被环境变量覆盖()
    {
        var root = NewRoot();
        var previous = Environment.GetEnvironmentVariable(UpdateService.FeedEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateService.FeedEnvironmentVariable, "https://feed.example/latest");
            var handler = new StubHandler { Respond = _ => Json(FeedJson("v1.0.0", false, false)) };
            var service = new UpdateService(root, "3.2.4", new HttpClient(handler));

            await service.CheckAsync();

            Assert.Equal("https://feed.example/latest", handler.LastRequestUri?.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateService.FeedEnvironmentVariable, previous);
            Cleanup(root);
        }
    }

    // —— 下载 / 校验 / 解包就位 ——

    [Fact]
    public async Task 下载就位_校验通过_暂存区布局正确()
    {
        var root = NewRoot();
        var zipPath = CreatePackageZip(stamp: new byte[] { 1, 2, 3 });
        try
        {
            var zipBytes = File.ReadAllBytes(zipPath);
            var digest = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
            var handler = new StubHandler
            {
                Respond = request => request.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) }
                    : Json(FeedJson("v4.0.0", false, false, ("LinkPocket_v4.0.0.zip", "https://example.invalid/pkg.zip", "sha256:" + digest))),
            };
            var service = new UpdateService(root, "3.2.4", new HttpClient(handler));

            Assert.Equal(UpdateCheckStatus.Available, (await service.CheckAsync()).Status);
            var prepared = await service.PrepareAsync();

            Assert.Equal(UpdatePrepareStatus.Ready, prepared.Status);
            var stagingRoot = UpdateStaging.StagingRootFor(root);
            Assert.True(File.Exists(Path.Combine(stagingRoot, "LinkPocket-Update.exe")));
            Assert.True(File.Exists(Path.Combine(stagingRoot, "package", "LinkPocket.exe")));
            Assert.True(Directory.Exists(Path.Combine(stagingRoot, "package", "update")));
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* 尽力而为 */ }
            Cleanup(root);
        }
    }

    [Fact]
    public async Task 下载就位_摘要不符即失败()
    {
        var root = NewRoot();
        var zipPath = CreatePackageZip();
        try
        {
            var zipBytes = File.ReadAllBytes(zipPath);
            var handler = new StubHandler
            {
                Respond = request => request.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) }
                    : Json(FeedJson("v4.0.0", false, false,
                        ("LinkPocket_v4.0.0.zip", "https://example.invalid/pkg.zip", "sha256:" + new string('0', 64)))),
            };
            var service = new UpdateService(root, "3.2.4", new HttpClient(handler));
            await service.CheckAsync();

            var prepared = await service.PrepareAsync();

            Assert.Equal(UpdatePrepareStatus.Failed, prepared.Status);
            Assert.Equal(UpdateErrorCodes.Digest, prepared.Error);
            Assert.False(File.Exists(Path.Combine(UpdateStaging.StagingRootFor(root), "package", "LinkPocket.exe")));
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* 尽力而为 */ }
            Cleanup(root);
        }
    }

    [Fact]
    public async Task 未检查就下载_拒绝()
    {
        var root = NewRoot();
        try
        {
            var service = new UpdateService(root, "3.2.4", new HttpClient(new StubHandler()));
            var prepared = await service.PrepareAsync();
            Assert.Equal(UpdatePrepareStatus.Failed, prepared.Status);
            Assert.Equal(UpdateErrorCodes.NotPrepared, prepared.Error);
        }
        finally { Cleanup(root); }
    }

    // —— 并发 / 单飞（WARNINGS 172 的回归钉：启动自动预下载 × 手动检查曾并发写同一个 package.zip） ——

    /// <summary>首读前等待闸门的响应内容：把"下载在飞"的窗口变成确定性的（两次调用必然重叠）。</summary>
    private sealed class GatedContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly Task _gate;

        public GatedContent(byte[] data, Task gate)
        {
            _data = data;
            _gate = gate;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await _gate.ConfigureAwait(false);
            await stream.WriteAsync(_data).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _data.Length;
            return true;
        }
    }

    /// <summary>同步收集进度读数（不走 Progress 的上下文封送，测试里可即读）。</summary>
    private sealed class CollectingProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value)
        {
            lock (Values) Values.Add(value);
        }
    }

    [Fact]
    public async Task 并发两次就位_复用同一次下载_均就绪且只下载一次()
    {
        var root = NewRoot();
        var zipPath = CreatePackageZip();
        try
        {
            var zipBytes = File.ReadAllBytes(zipPath);
            var digest = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var assetRequests = 0;
            var handler = new StubHandler
            {
                Respond = request =>
                {
                    if (request.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref assetRequests);
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new GatedContent(zipBytes, gate.Task) };
                    }

                    return Json(FeedJson("v4.0.0", false, false, ("LinkPocket_v4.0.0.zip", "https://example.invalid/pkg.zip", "sha256:" + digest)));
                },
            };
            var service = new UpdateService(root, "3.2.4", new HttpClient(handler));
            await service.CheckAsync();

            var firstProgress = new CollectingProgress();
            var secondProgress = new CollectingProgress();
            var first = service.PrepareAsync(firstProgress);
            for (var i = 0; i < 300 && Volatile.Read(ref assetRequests) == 0; i++) await Task.Delay(10);
            Assert.True(Volatile.Read(ref assetRequests) >= 1, "第一次下载应已发出资产请求");
            var second = service.PrepareAsync(secondProgress);   // 下载在飞：复用同一次，而不是再开一次
            gate.SetResult();

            var results = await Task.WhenAll(first, second);

            Assert.Equal(1, Volatile.Read(ref assetRequests));   // 只发出一次资产请求（旧实现 = 两次 → 抢 package.zip 抛 IOException → "下载失败"）
            Assert.All(results, r => Assert.Equal(UpdatePrepareStatus.Ready, r.Status));
            Assert.NotEmpty(firstProgress.Values);
            Assert.NotEmpty(secondProgress.Values);              // 并行等待者拿到同一份进度（扇出）
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* 尽力而为 */ }
            Cleanup(root);
        }
    }

    // —— 本地 zip 通道 ——

    [Fact]
    public async Task 本地zip_结构合格即就位()
    {
        var root = NewRoot();
        var zipPath = CreatePackageZip();
        try
        {
            var service = new UpdateService(root, "3.2.4", new HttpClient(new StubHandler()));
            var prepared = await service.PrepareFromZipAsync(zipPath);

            Assert.Equal(UpdatePrepareStatus.Ready, prepared.Status);
            Assert.True(File.Exists(Path.Combine(UpdateStaging.StagingRootFor(root), "LinkPocket-Update.exe")));
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* 尽力而为 */ }
            Cleanup(root);
        }
    }

    [Fact]
    public async Task 本地zip_缺更新器即失败()
    {
        var root = NewRoot();
        var zipPath = Path.Combine(Path.GetTempPath(), $"lp-update-bad-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("LinkPocket.exe");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("new-app");
            }

            var service = new UpdateService(root, "3.2.4", new HttpClient(new StubHandler()));
            var prepared = await service.PrepareFromZipAsync(zipPath);

            Assert.Equal(UpdatePrepareStatus.Failed, prepared.Status);
            Assert.Equal(UpdateErrorCodes.Package, prepared.Error);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* 尽力而为 */ }
            Cleanup(root);
        }
    }

    [Fact]
    public async Task 本地zip不存在_报找不到更新包()
    {
        var root = NewRoot();
        try
        {
            var missing = Path.Combine(Path.GetTempPath(), $"lp-update-missing-{Guid.NewGuid():N}.zip");
            var service = new UpdateService(root, "3.2.4", new HttpClient(new StubHandler()));

            var prepared = await service.PrepareFromZipAsync(missing);

            Assert.Equal(UpdatePrepareStatus.Failed, prepared.Status);
            Assert.Equal(UpdateErrorCodes.ZipMissing, prepared.Error);   // 曾误报"下载更新包失败"（Download 码）
        }
        finally { Cleanup(root); }
    }

    // —— 上次更新结果 ——

    [Fact]
    public void 上次结果_读取一次即消费()
    {
        var root = NewRoot();
        try
        {
            var stagingRoot = UpdateStaging.StagingRootFor(root);
            Directory.CreateDirectory(stagingRoot);
            File.WriteAllText(Path.Combine(stagingRoot, UpdateStaging.ResultFileName),
                "{\"ok\":true,\"mode\":\"bridge\",\"from_version\":\"3.2.4\",\"to_version\":\"4.0.0\",\"error\":null,\"finished_at\":\"2026-10-15T00:00:00Z\"}");

            var service = new UpdateService(root, "4.0.0", new HttpClient(new StubHandler()));
            var result = service.ConsumeLastResult();

            Assert.NotNull(result);
            Assert.True(result!.Ok);
            Assert.Equal("bridge", result.Mode);
            Assert.Equal("3.2.4", result.FromVersion);
            Assert.Null(service.ConsumeLastResult());   // 已消费
        }
        finally { Cleanup(root); }
    }

    // —— 自动检查偏好 ——

    [Fact]
    public void 自动检查偏好_落盘并读回()
    {
        var root = NewRoot();
        try
        {
            var first = new UpdateService(root, "3.2.4", new HttpClient(new StubHandler()));
            Assert.True(first.AutoCheckOnStartup);   // 缺省开
            first.AutoCheckOnStartup = false;

            var second = new UpdateService(root, "3.2.4", new HttpClient(new StubHandler()));
            Assert.False(second.AutoCheckOnStartup);

            second.AutoCheckOnStartup = true;
            Assert.True(new UpdateService(root, "3.2.4", new HttpClient(new StubHandler())).AutoCheckOnStartup);
        }
        finally { Cleanup(root); }
    }
}
