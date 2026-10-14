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
/// <para><b>HttpClient</b>：单实例 + 固定超时（更新检查频率极低，不存在连接池耗尽的场景；
/// 架构红线 H4 白名单已登记本文件）。</para>
/// <para><b>全失败路径都如实返回</b>（不抛给调用方）：更新检查的失败是**正常业务态**（离线 / 无 Release），
/// 由更新页展示 + 指路；只有"交棒"失败需要调用方留在应用里。</para>
/// </remarks>
public sealed class UpdateService : IUpdateService
{
    /// <summary>更新源覆盖环境变量（名字冻结，见 UPDATE.md §6）。</summary>
    public const string FeedEnvironmentVariable = "LINKPOCKET_UPDATE_FEED";

    /// <summary>缺省更新源：GitHub Releases latest。</summary>
    public const string DefaultFeedUrl = "https://api.github.com/repos/Hrecer/LinkPocket/releases/latest";

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

    /// <param name="installRoot">安装根（程序根）：暂存区标识与 <c>--install</c> 都用它。</param>
    /// <param name="currentVersion">当前版本（3 段）；组合根从本进程 exe 文件版本读出。</param>
    /// <param name="http">测试注入；null = 自建单实例（固定超时）。</param>
    public UpdateService(string installRoot, string currentVersion, HttpClient? http = null)
    {
        _installRoot = installRoot;
        CurrentVersion = currentVersion;
        // 偏好文件挂在**安装的数据区**（缺省解析下即 AppPaths.DataRoot；显式 --db 时跟随安装根，
        // 与暂存区/交棒同一归属）：测试注入临时安装根 = 偏好也落临时区，不碰用户数据区。
        _preferencesPath = Path.Combine(installRoot, "data", PreferencesFileName);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
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
            using var response = await _http.GetAsync(feedUrl, ct).ConfigureAwait(false);
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
        UpdateCheckResult? check;
        lock (_gate) check = _lastCheck;
        if (check is null || check.Status != UpdateCheckStatus.Available || check.DownloadUrl is null)
            return Store(FailedPrepare(UpdateErrorCodes.NotPrepared));

        var stagingRoot = UpdateStaging.StagingRootFor(_installRoot);
        var zipPath = Path.Combine(stagingRoot, "package.zip");
        try
        {
            Directory.CreateDirectory(stagingRoot);
            progress?.Report(0);
            await DownloadAsync(check.DownloadUrl, zipPath, progress, ct).ConfigureAwait(false);
            if (!DigestMatches(zipPath, check.Sha256Digest))
                return Store(FailedPrepare(UpdateErrorCodes.Digest));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            LpLog.Warn($"downloading the update package failed: {ex.Message}", category: LogCategory);
            return Store(FailedPrepare(UpdateErrorCodes.Download));
        }

        return await PrepareCoreAsync(zipPath, progress, ct).ConfigureAwait(false);
    }

    public Task<UpdatePrepareResult> PrepareFromZipAsync(string zipPath, IProgress<double>? progress = null, CancellationToken ct = default)
        => PrepareCoreAsync(zipPath, progress, ct);

    /// <summary>解包 + 结构校验 + 更新器就位（下载与本地 zip 两条通道的唯一实现）。</summary>
    private async Task<UpdatePrepareResult> PrepareCoreAsync(string zipPath, IProgress<double>? progress, CancellationToken ct)
    {
        if (!File.Exists(zipPath))
        {
            LpLog.Warn($"the zip file does not exist: {zipPath}", category: LogCategory);
            return Store(FailedPrepare(UpdateErrorCodes.Download));
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
            Process.Start(new ProcessStartInfo
            {
                FileName = updater,
                WorkingDirectory = stagingRoot,
                UseShellExecute = false,
                Arguments = $"--package \"{package}\" --install \"{_installRoot}\" --wait-pid {Environment.ProcessId}",
            });
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

    private async Task DownloadAsync(string url, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(destination);
        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            if (total > 0) progress?.Report(Math.Min(60.0, received * 60.0 / total));   // 0–60 = 下载段；60+ = 解包段
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
