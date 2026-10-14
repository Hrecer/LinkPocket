using System.Security.Cryptography;
using System.Text;

namespace LinkPocket.Contracts;

/// <summary>
/// 更新暂存区（<c>%TEMP%\LinkPocket-Update\&lt;安装标识&gt;\</c>）的**唯一约定**：
/// 更新器把 <c>update.log</c> / <c>update-result.json</c> 写在自己目录；新版应用首启**先读结果、再清扫**。
/// 安装标识由本类计算——更新器（提取落点）与应用的哈希口径必须逐字一致，否则应用会找不到自己的暂存区。
/// </summary>
public static class UpdateStaging
{
    /// <summary>暂存区上级目录名（<c>%TEMP%</c> 下）。</summary>
    public const string DirectoryName = "LinkPocket-Update";

    /// <summary>更新结果文件名（清扫时保留，直到被消费）。</summary>
    public const string ResultFileName = "update-result.json";

    /// <summary>安装标识 = 安装目录规范化路径（绝对化、小写、去尾分隔符）的 SHA-256 前 12 位十六进制。</summary>
    public static string InstallId(string installPath)
    {
        var full = Path.GetFullPath(installPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = full.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash)[..12];
    }

    /// <summary>
    /// 本安装的暂存区路径。根 = 系统临时目录；<c>LP_TEMP_ROOT</c> 指到别处时用它
    /// （与引擎 <c>TempArea</c> 同一环境变量、同一口径——更新器是独立进程、不引引擎，故在契约层复刻这一条）。
    /// </summary>
    public static string StagingRootFor(string installPath)
    {
        var tempRoot = Environment.GetEnvironmentVariable("LP_TEMP_ROOT");
        if (string.IsNullOrWhiteSpace(tempRoot)) tempRoot = Path.GetTempPath();
        return Path.Combine(tempRoot, DirectoryName, InstallId(installPath));
    }

    /// <summary>读上次更新的结果 JSON（不存在 / 不可读 = null）。调用方读完后自行 <see cref="ConsumeResult"/>。</summary>
    public static string? TryReadResultJson(string installPath)
    {
        var path = Path.Combine(StagingRootFor(installPath), ResultFileName);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>消费结果文件（更新页展示过之后调；缺失 = 无事）。</summary>
    public static void ConsumeResult(string installPath)
    {
        var path = Path.Combine(StagingRootFor(installPath), ResultFileName);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LpLog.Warn($"failed to delete the update result file: {path} ({ex.Message})", category: "app.lifecycle");
        }
    }

    /// <summary>
    /// 新版应用首启清扫：删除本安装暂存区里的全部内容，**只保留结果文件**（等更新页消费）。
    /// 尽力而为——删不净记日志、下次启动再清（Windows 上正被占用的文件删不掉，由这条接力兜底）。
    /// </summary>
    public static void SweepOwnStaging(string installPath)
    {
        var root = StagingRootFor(installPath);
        if (!Directory.Exists(root)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (string.Equals(Path.GetFileName(entry), ResultFileName, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LpLog.Warn($"update staging cleanup failed, will retry on the next launch: {entry} ({ex.Message})",
                    category: "app.lifecycle");
            }
        }
    }
}
