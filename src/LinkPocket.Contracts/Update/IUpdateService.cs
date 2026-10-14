namespace LinkPocket.Contracts;

/// <summary>检查更新源的结果类别。</summary>
public enum UpdateCheckStatus
{
    /// <summary>已是（或高于）最新正式版。</summary>
    UpToDate,

    /// <summary>发现更新的正式 Release（尚未下载）。</summary>
    Available,

    /// <summary>检查失败（网络 / 响应 / 资产缺失——<c>Error</c> 带原因）。</summary>
    Failed,
}

/// <summary>一次「检查更新」的结果（失败也如实带当前版本与原因）。</summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? LatestVersion = null,
    string? Notes = null,
    string? DownloadUrl = null,
    string? Sha256Digest = null,
    long SizeBytes = 0,
    string? Error = null);

/// <summary>下载 / 解包就位的结果类别。</summary>
public enum UpdatePrepareStatus
{
    /// <summary>包已解包校验就位，可交棒更新器。</summary>
    Ready,

    /// <summary>失败（<c>Error</c> 带原因；暂存区原样保留）。</summary>
    Failed,
}

/// <summary>「就位」结果：<c>Ready</c> = 可交棒（<see cref="PreparedVersion"/> 为包版本）。</summary>
public sealed record UpdatePrepareResult(UpdatePrepareStatus Status, string? PreparedVersion = null, string? Error = null);

/// <summary>上次更新的终局摘要（读自暂存区 <c>update-result.json</c>；读后即消费）。</summary>
public sealed record UpdateLastResult(bool Ok, string? Mode, string? FromVersion, string? ToVersion, string? Error, string? FinishedAt);

/// <summary>
/// 更新失败原因码（**语言无关的契约**：界面据码取词——<c>update.err.*</c>；异常原文只进日志，不上屏）。
/// </summary>
public static class UpdateErrorCodes
{
    /// <summary>更新源不可达 / 响应不是合法 JSON。</summary>
    public const string Network = "network";

    /// <summary>更新服务器返回非 2xx。</summary>
    public const string Http = "http";

    /// <summary>发布页数据不完整（缺更新包资产 / tag 形状不对）。</summary>
    public const string Release = "release";

    /// <summary>下载更新包失败。</summary>
    public const string Download = "download";

    /// <summary>包摘要（sha256）校验不通过。</summary>
    public const string Digest = "digest";

    /// <summary>所选更新包文件不存在（本地 zip 通道：拣选后文件被移动 / 删除）。</summary>
    public const string ZipMissing = "zip-missing";

    /// <summary>解包失败 / 包结构不含预期交付件。</summary>
    public const string Package = "package";

    /// <summary>尚未下载就位。</summary>
    public const string NotPrepared = "not-prepared";

    /// <summary>更新器无法启动。</summary>
    public const string Updater = "updater";
}

/// <summary>
/// 更新服务端口（实现 = 宿主侧 <c>LinkPocket.App.Services.UpdateService</c>，见 UPDATE.md §6）：
/// 检查（GitHub Releases latest，跳过 prerelease / draft）→ 下载 + 校验（sha256）→ 解包就位 → 交棒临时更新器。
/// 更新源可用环境变量 <c>LINKPOCKET_UPDATE_FEED</c> 覆盖（本机 hosts 把 github 指到环回时用）。
/// </summary>
public interface IUpdateService
{
    /// <summary>当前版本（3 段，如 <c>3.2.4</c>）。</summary>
    string CurrentVersion { get; }

    /// <summary>启动时自动检查（并预下载）——持久化在数据区 <c>update-preferences.json</c>，缺省开。</summary>
    bool AutoCheckOnStartup { get; set; }

    /// <summary>最近一次检查结果（后台自动检查的产物；从未检查 = null）。</summary>
    UpdateCheckResult? LastCheck { get; }

    /// <summary>最近一次就位结果（新一次检查发现版本变化时作废；从未就位 = null）。</summary>
    UpdatePrepareResult? LastPrepare { get; }

    /// <summary>检查最新正式 Release（跳过 prerelease / draft）。</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default);

    /// <summary>下载 + 校验 + 解包就位（要求最近一次检查为 Available）。</summary>
    Task<UpdatePrepareResult> PrepareAsync(IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>从本地 zip 就位（「从本地 zip 更新」兜底通道；校验 / 解包与下载路径同一实现）。</summary>
    Task<UpdatePrepareResult> PrepareFromZipAsync(string zipPath, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>交棒：启动临时更新器（<c>--wait-pid</c> 等本进程退出后动手）。成功 = null；失败 = 原因（此时不应退出应用）。</summary>
    string? HandOffToUpdater();

    /// <summary>读取并消费上次更新的结果（新版首启展示一次；无 = null）。</summary>
    UpdateLastResult? ConsumeLastResult();
}
