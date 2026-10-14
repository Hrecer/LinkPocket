using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LinkPocket.Contracts;

namespace LinkPocket.Services;

/// <summary>
/// 更新服务唯一实现（UPDATE.md §6）：检查（GitHub Releases latest，跳过 prerelease / draft）→
/// 下载（sha256 校验，若发布带 digest）→ 解包 + 结构校验 → 交棒临时更新器（§5.2 冻结接口）。
/// 就位布局（= 更新器暂存区约定）：<c>%TEMP%\LinkPocket-Update\&lt;安装标识&gt;\</c> 下放
/// <c>LinkPocket-Update.exe</c>（更新器自身目录 = 暂存区根，它的日志/结果也落这里）与
/// <c>package\</c>（解包后的交付内容，交棒时作 <c>--package</c>）。
/// </summary>
/// <remarks>
/// <para><b>更新源覆盖</b>：环境变量 <c>LINKPOCKET_UPDATE_FEED</c>（名字已冻结在 UPDATE.md §6）——
/// 本机 hosts 把 github 指到环回（加速器遗留），本机联调时指到本地服务即可。</para>
/// <para><b>HttpClient</b>：单实例（更新检查频率极低，不存在连接池耗尽的场景；
/// 架构红线 H4 白名单已登记本文件）。**不用整请求总超时**（170MB 的包在总超时下必然误杀——
/// 曾用 100s）——改为分段防卡：检查 20s / 取响应头 30s / 正文按"停滞 90s 无数据"判死。</para>
/// <para><b>下载就位 = 单飞</b>：同一时刻只有一次下载在飞——启动自动预下载与应用内手动「检查更新」
/// 并发时后者**复用前者**（共享同一下载 / 同一结果 / 同一份进度），绝不两次写同一个 <c>package.zip</c>
/// （曾因并发写同文件抛 IOException 被映射成"下载更新包失败"，见 WARNINGS 172）。</para>
/// <para><b>全失败路径都如实返回</b>（不抛给调用方）：更新检查的失败是**正常业务态**（离线 / 无 Release），
/// 由更新页展示 + 指路；只有"交棒"失败需要调用方留在应用里。</para>
/// </remarks>
public sealed class UpdateService : IUpdateService
{
    /// <summary>更新源覆盖环境变量（名字冻结，见 UPDATE.md §6）。</summary>
    public const string FeedEnvironmentVariable = "LINKPOCKET_UPDATE_FEED";

    /// <summary>缺省更新源：GitHub Releases latest。</summary>
    public const string DefaultFeedUrl = "https://api.github.com/repos/Hrecer/LinkPocket/releases/latest";

    /// <summary>检查请求总超时（小 JSON；超时 = 网络不可达，如实上报）。</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>下载：取响应头（含连接与重定向）的超时——只约束到"拿到头"为止，正文不受它约束（否则大包必被误杀）。</summary>
    private static readonly TimeSpan DownloadHeadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>下载：正文**停滞判死**——单次读超过它没有任何数据即失败（弱网但持续有流不误杀）。</summary>
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(90);

    private const string PreferencesFileName = "update-preferences.json";
    private const string PackageDirectoryName = "package";
    private const string UpdaterFileName = "LinkPocket-Update.exe";
    private const string LogCategory = "app.update";

    private readonly string _installRoot;
    private readonly string _preferencesPath;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private UpdateCheckResult? _lastCheck;
    private UpdatePrepareResult? _lastPrepare;

    /// <summary>在飞的下载就位任务（单飞：并发调用复用同一次下载；完成后复位）。</summary>
    private Task<UpdatePrepareResult>? _prepareInFlight;

    /// <summary>等待本次下载的所有进度接收方（手动面板 / 自动流程可能同时在等同一份进度）。</summary>
    private readonly List<IProgress<double>> _prepareProgress = new();

    /// <param name="installRoot">安装根（程序根）：暂存区标识与 <c>--install</c> 都用它。</param>
    /// <param name="currentVersion">当前版本（3 段）；组合根从本进程 exe 文件版本读出。</param>
    /// <param name="http">测试注入；null = 自建单实例（整请求不设总超时，防卡见类注释）。</param>
    public UpdateService(string installRoot, string currentVersion, HttpClient? http = null)
    {
        _installRoot = installRoot;
        CurrentVersion = currentVersion;
        // 偏好文件挂在**安装的数据区**（缺省解析下即 AppPaths.DataRoot；显式 --db 时跟随安装根，
        // 与暂存区/交棒同一归属）：测试注入临时安装根 = 偏好也落临时区，不碰用户数据区。
        _preferencesPath = Path.Combine(installRoot, "data", PreferencesFileName);
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "LinkPocket");
        if (!_http.DefaultRequestHeaders.Contains("Accept"))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
    }

    public string CurrentVersion { get; }

    public UpdateCheckResult? LastCheck
    {
        get { lock (_gate) return _lastCheck; }
    }

    public UpdatePrepareResult? LastPrepare
    {
        get { lock (_gate) return _lastPrepare; }
    }

    /// <summary>启动时自动检查开关（持久化在数据区 <c>update-preferences.json</c>；损坏 / 缺失 = 缺省开）。</summary>
    public bool AutoCheckOnStartup
    {
        get
        {
            try
            {
                if (!File.Exists(_preferencesPath)) return true;
                var model = JsonSerializer.Deserialize<PreferencesFile>(File.ReadAllText(_preferencesPath));
                if (model is null || model.Version != PreferencesFile.CurrentVersion)
                {
                    LpLog.Warn($"the update preferences file has an unsupported version; using the default (auto check on): {_preferencesPath}", category: LogCategory);
                    return true;
                }
                return model.AutoCheckOnStartup;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LpLog.Warn($"the update preferences file is unreadable - using the default (auto check on): {ex.Message}", category: LogCategory);
                return true;
            }
        }
        set
        {
            try
            {
                AtomicFile.WriteAllText(_preferencesPath,
                    JsonSerializer.Serialize(new PreferencesFile { AutoCheckOnStartup = value },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LpLog.Warn($"failed to save the update preferences (the toggle will not persist): {ex.Message}", category: LogCategory);
            }
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var feedUrl = Environment.GetEnvironmentVariable(FeedEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(feedUrl)) feedUrl = DefaultFeedUrl;

        UpdateCheckResult result;
        try
        {
            // 整请求总超时只留在"检查"这一档（小 JSON）；下载正文走"停滞判死"（见类注释）
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CheckTimeout);
            using var response = await _http.GetAsync(feedUrl, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LpLog.Warn($"the update feed returned HTTP {(int)response.StatusCode}: {feedUrl}", category: LogCategory);
                result = Failed(UpdateErrorCodes.Http);
            }
            else
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                result = ParseRelease(document.RootElement);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            LpLog.Warn($"the update feed is unreachable ({feedUrl}): {ex.Message}", category: LogCategory);
            result = Failed(UpdateErrorCodes.Network);
        }

        lock (_gate)
        {
            // 新检查发现"版本变了"→ 旧的就位作废；同一版本仍可用（避免重复下载）
            var keepPrepared = result.Status == UpdateCheckStatus.Available
                               && _lastPrepare?.Status == UpdatePrepareStatus.Ready
                               && _lastCheck?.LatestVersion == result.LatestVersion;
            _lastCheck = result;
            if (!keepPrepared) _lastPrepare = null;
        }

        LpLog.Info($"update check: {result.Status}"
                   + (result.LatestVersion is null ? "" : $" (latest v{result.LatestVersion})")
                   + (result.Error is null ? "" : $" ({result.Error})"), category: LogCategory);
        return result;
    }

    public async Task<UpdatePrepareResult> PrepareAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Task<UpdatePrepareResult> flight;
        lock (_gate)
        {
            // **单飞**：已有下载在飞 → 复用（并发调用共享同一次下载、同一结果与同一份进度；
            // 绝不两次 File.Create 同一个 package.zip —— 那会抛 IOException 被映射成"下载失败"，见 WARNINGS 172）
            flight = _prepareInFlight ??= PrepareOnceAsync(ct);
            if (progress is not null) _prepareProgress.Add(progress);
        }

        try
        {
            return await flight.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_prepareInFlight, flight))
                {
                    _prepareInFlight = null;
                    _prepareProgress.Clear();
                }
            }
        }
    }

    /// <summary>一次真实的"下载 → 校验 → 解包就位"（单飞任务的执行体；并发调用方经 <see cref="PrepareAsync"/> 共享它）。</summary>
    private async Task<UpdatePrepareResult> PrepareOnceAsync(CancellationToken ct)
    {
        UpdateCheckResult? check;
        lock (_gate) check = _lastCheck;
        if (check is null || check.Status != UpdateCheckStatus.Available || check.DownloadUrl is null)
            return Store(FailedPrepare(UpdateErrorCodes.NotPrepared));

        // 同版本已就位：直接返回（与更新面板同一条跳过口径；服务层兜住所有调用方）
        if (_lastPrepare is { Status: UpdatePrepareStatus.Ready } ready
            && string.Equals(ready.PreparedVersion, check.LatestVersion, StringComparison.Ordinal))
            return ready;

        var stagingRoot = UpdateStaging.StagingRootFor(_installRoot);
        var zipPath = Path.Combine(stagingRoot, "package.zip");
        try
        {
            Directory.CreateDirectory(stagingRoot);
            Report(0);
            await DownloadAsync(check.DownloadUrl, zipPath, ct).ConfigureAwait(false);
            if (!DigestMatches(zipPath, check.Sha256Digest))
                return Store(FailedPrepare(UpdateErrorCodes.Digest));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            LpLog.Warn($"downloading the update package failed: {ex.Message}", category: LogCategory);
            return Store(FailedPrepare(UpdateErrorCodes.Download));
        }

        return await PrepareCoreAsync(zipPath, new FanOutProgress(Report), ct).ConfigureAwait(false);
    }

    /// <summary>把本次下载的进度分发给所有等待者（手动面板与启动自动流程可能同时在等同一份下载）。</summary>
    private void Report(double value)
    {
        IProgress<double>[] sinks;
        lock (_gate) sinks = _prepareProgress.ToArray();
        foreach (var sink in sinks) sink.Report(value);
    }

    /// <summary>进度扇出（同步转发；向 UI 线程的封送由各接收方自己的 Progress 负责）。</summary>
    private sealed class FanOutProgress : IProgress<double>
    {
        private readonly Action<double> _report;

        public FanOutProgress(Action<double> report) => _report = report;

        public void Report(double value) => _report(value);
    }

    public Task<UpdatePrepareResult> PrepareFromZipAsync(string zipPath, IProgress<double>? progress = null, CancellationToken ct = default)
        => PrepareCoreAsync(zipPath, progress, ct);

    /// <summary>解包 + 结构校验 + 更新器就位（下载与本地 zip 两条通道的唯一实现）。</summary>
    private async Task<UpdatePrepareResult> PrepareCoreAsync(string zipPath, IProgress<double>? progress, CancellationToken ct)
    {
        if (!File.Exists(zipPath))
        {
            // 下载通道不会走到这里（下载失败在 PrepareOnceAsync 里已按 Download 码返回）——
            // 实际入口 = 本地 zip 通道（拣选后文件被移动 / 删除）：给专用的"找不到包"码，不说"下载失败"
            LpLog.Warn($"the zip file does not exist: {zipPath}", category: LogCategory);
            return Store(FailedPrepare(UpdateErrorCodes.ZipMissing));
        }

        var stagingRoot = UpdateStaging.StagingRootFor(_installRoot);
        try
        {
            Directory.CreateDirectory(stagingRoot);
            var packageDir = Path.Combine(stagingRoot, PackageDirectoryName);
            if (Directory.Exists(packageDir)) Directory.Delete(packageDir, recursive: true);
            progress?.Report(60);
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, packageDir), ct).ConfigureAwait(false);
            progress?.Report(90);

            var validation = ValidatePackage(packageDir);
            if (validation is not null) return Store(FailedPrepare(validation));

            // 更新器提到暂存区根：它的自身目录 = 暂存区（日志 / 结果都落那里，见 §5.2 冻结接口）
            File.Copy(Path.Combine(packageDir, UpdaterFileName),
                Path.Combine(stagingRoot, UpdaterFileName), overwrite: true);

            var version = ReadProductVersion(Path.Combine(packageDir, "LinkPocket.exe"));
            progress?.Report(100);
            return Store(new UpdatePrepareResult(UpdatePrepareStatus.Ready, version));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            LpLog.Warn($"extracting the update package failed: {ex.Message}", category: LogCategory);
            return Store(FailedPrepare(UpdateErrorCodes.Package));
        }
    }

    public string? HandOffToUpdater()
    {
        var stagingRoot = UpdateStaging.StagingRootFor(_installRoot);
        var updater = Path.Combine(stagingRoot, UpdaterFileName);
        var package = Path.Combine(stagingRoot, PackageDirectoryName);
        if (!File.Exists(updater) || !Directory.Exists(package))
            return UpdateErrorCodes.NotPrepared;

        try
        {
            // 参数逐项给（ArgumentList 负责引号与转义）：手工拼串时尾分隔符会把 \" 转义成字面引号，
            // 后续 --wait-pid 被吞进安装路径（WARNINGS 173）
            var startInfo = new ProcessStartInfo
            {
                FileName = updater,
                WorkingDirectory = stagingRoot,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--package");
            startInfo.ArgumentList.Add(package);
            startInfo.ArgumentList.Add("--install");
            startInfo.ArgumentList.Add(_installRoot);
            startInfo.ArgumentList.Add("--wait-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(startInfo);
            LpLog.Info($"handed off to the updater; the app will now exit: {updater}", category: LogCategory);
            return null;
        }
        catch (Exception ex)
        {
            LpLog.Warn($"could not start the updater: {ex.Message}", category: LogCategory);
            return UpdateErrorCodes.Updater;
        }
    }

    public UpdateLastResult? ConsumeLastResult()
    {
        var json = UpdateStaging.TryReadResultJson(_installRoot);
        if (json is null) return null;

        UpdateLastResult? result = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            result = new UpdateLastResult(
                root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                root.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : null,
                root.TryGetProperty("from_version", out var from) && from.ValueKind == JsonValueKind.String ? from.GetString() : null,
                root.TryGetProperty("to_version", out var to) && to.ValueKind == JsonValueKind.String ? to.GetString() : null,
                root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null,
                root.TryGetProperty("finished_at", out var finished) && finished.ValueKind == JsonValueKind.String ? finished.GetString() : null);
        }
        catch (JsonException)
        {
            result = null;   // 半截结果文件 = 没有结果（消费掉，不再重复读）
        }

        UpdateStaging.ConsumeResult(_installRoot);
        return result;
    }

    /// <summary>读 exe 的产品版本（3 段）；读不到 = null（组合根据此回退装配版本）。</summary>
    public static string? ReadProductVersion(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (!Version.TryParse(info.FileVersion, out var version)) return null;
            return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // —— 检查解析（GitHub Releases latest 的字段面；只认正式 Release）——

    private UpdateCheckResult ParseRelease(JsonElement root)
    {
        var prerelease = root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
        var draft = root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
        if (prerelease || draft)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, CurrentVersion);   // 只有预发布/草稿 = 没有可用的正式新版

        var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        var versionText = tag.TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var latest))
        {
            LpLog.Warn($"unexpected release tag '{tag}'", category: LogCategory);
            return Failed(UpdateErrorCodes.Release);
        }
        if (!Version.TryParse(CurrentVersion, out var current) || latest <= current)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, CurrentVersion, LatestVersion: versionText);

        string? url = null, digest = null;
        long size = 0;
        var expectedAsset = $"LinkPocket_v{versionText}.zip";
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (!string.Equals(name, expectedAsset, StringComparison.OrdinalIgnoreCase)) continue;
                url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                digest = asset.TryGetProperty("digest", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
                size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            LpLog.Warn($"release v{versionText} has no asset '{expectedAsset}'", category: LogCategory);
            return Failed(UpdateErrorCodes.Release);
        }

        // GitHub 的 digest 形如 "sha256:<hex>"；没有 = 跳过摘要校验（结构校验仍然做）
        var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest["sha256:".Length..]
            : null;
        var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null;
        return new UpdateCheckResult(UpdateCheckStatus.Available, CurrentVersion, versionText, notes, url, sha256, size);
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken ct)
    {
        // 取响应头（连接 / 重定向）：限时 30s；**拿到头就释放计时器**——正文的防卡交给"停滞判死"，
        // 整请求总超时会把 170MB 的包在慢网下必然误杀（见类注释）。
        HttpResponseMessage response;
        var head = CancellationTokenSource.CreateLinkedTokenSource(ct);
        head.CancelAfter(DownloadHeadTimeout);
        try
        {
            response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, head.Token).ConfigureAwait(false);
        }
        finally
        {
            head.Dispose();
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;

            // 先写 `<包>.part`、完整落盘后原子改名：正式文件名只会是完整包（失败 / 中断不留半截）
            var partPath = destination + ".part";
            try
            {
                await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var target = File.Create(partPath))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    while (true)
                    {
                        int read;
                        using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            stall.CancelAfter(DownloadStallTimeout);
                            try
                            {
                                read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                throw new IOException($"the download stalled (no data for {DownloadStallTimeout.TotalSeconds:0} seconds)");
                            }
                        }

                        if (read <= 0) break;
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        received += read;
                        if (total > 0) Report(Math.Min(60.0, received * 60.0 / total));   // 0–60 = 下载段；60+ = 解包段
                    }
                }

                File.Move(partPath, destination, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(partPath)) File.Delete(partPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 尽力而为；下次启动清扫接力 */ }
                throw;
            }
        }
    }

    /// <summary>摘要校验（发布未带 digest = 跳过）；不匹配进日志（界面只见原因码）。</summary>
    private static bool DigestMatches(string zipPath, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return true;
        using var stream = File.OpenRead(zipPath);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (string.Equals(actual, expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal)) return true;
        LpLog.Warn($"the downloaded package fails the sha256 check (expected {expectedSha256}, actual {actual})", category: LogCategory);
        return false;
    }

    /// <summary>结构校验（§6：解包必须含更新器与预期交付件才允许交棒）；缺件细节进日志。</summary>
    private static string? ValidatePackage(string packageDir)
    {
        if (!File.Exists(Path.Combine(packageDir, "LinkPocket.exe")))
        {
            LpLog.Warn($"the prepared package has no LinkPocket.exe: {packageDir}", category: LogCategory);
            return UpdateErrorCodes.Package;
        }

        if (!File.Exists(Path.Combine(packageDir, UpdaterFileName)))
        {
            LpLog.Warn($"the prepared package has no updater ({UpdaterFileName}): {packageDir}", category: LogCategory);
            return UpdateErrorCodes.Package;
        }

        if (!Directory.Exists(Path.Combine(packageDir, "update")))
        {
            LpLog.Warn($"the prepared package has no update\\ directory: {packageDir}", category: LogCategory);
            return UpdateErrorCodes.Package;
        }

        return null;
    }

    private UpdateCheckResult Failed(string error) => new(UpdateCheckStatus.Failed, CurrentVersion, Error: error);

    private static UpdatePrepareResult FailedPrepare(string error) => new(UpdatePrepareStatus.Failed, Error: error);

    private UpdatePrepareResult Store(UpdatePrepareResult result)
    {
        lock (_gate) _lastPrepare = result;
        LpLog.Info($"update prepare: {result.Status}"
                   + (result.Status == UpdatePrepareStatus.Ready ? $" (v{result.PreparedVersion ?? "?"})" : $" ({result.Error})"),
            category: LogCategory);
        return result;
    }

    /// <summary>更新偏好文件（数据区，自家文件；无版本迁移负担，版本不符按缺省处理）。</summary>
    private sealed class PreferencesFile
    {
        public const int CurrentVersion = 1;

        [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
        [JsonPropertyName("auto_check_on_startup")] public bool AutoCheckOnStartup { get; set; } = true;
    }
}
