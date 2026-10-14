using System.Diagnostics;
using LinkPocket.Contracts;
using Microsoft.Data.Sqlite;

namespace LinkPocket.Update;

public enum UpdateMode
{
    /// <summary>同纪元直搬（§5.3 甲）。</summary>
    Direct,

    /// <summary>跨纪元走桥（§5.3 乙：旧版 CLI 导出 → 新版 CLI 导入）。</summary>
    Bridge,
}

public sealed record PreflightResult(UpdateMode Mode, string? FromVersion, string? ToVersion);

/// <summary>
/// 预检（§5.3 甲 1）：结构与残留 / 纪元路由 / 磁盘空间 / 版本读取——**全部通过才动任何东西**。
/// 拒绝消息面向用户（说清哪不对、怎么修），绝不猜、绝不半动。
/// </summary>
public static class Preflight
{
    private const long SpaceMarginBytes = 64L * 1024 * 1024;
    private const long MinSqliteHeaderBytes = 100;

    public static PreflightResult Run(UpdateContext ctx, Action<string> emit)
    {
        var packageExe = Path.Combine(ctx.PackageDir, "LinkPocket.exe");
        var installExe = Path.Combine(ctx.InstallDir, "LinkPocket.exe");

        if (!Directory.Exists(ctx.PackageDir))
            throw new UpdateFailure($"the package directory does not exist: {ctx.PackageDir}");
        if (!File.Exists(packageExe))
            throw new UpdateFailure($"the package does not look like a LinkPocket package (no LinkPocket.exe): {ctx.PackageDir}");
        if (Directory.Exists(Path.Combine(ctx.PackageDir, "data")))
            throw new UpdateFailure("the package must not contain a data\\ directory (the data area always comes from the install)");
        if (!Directory.Exists(ctx.InstallDir))
            throw new UpdateFailure($"the install directory does not exist: {ctx.InstallDir}");
        if (!File.Exists(installExe))
            throw new UpdateFailure($"the install directory does not look like a LinkPocket install (no LinkPocket.exe): {ctx.InstallDir}");
        if (string.Equals(ctx.InstallDir, ctx.PackageDir, StringComparison.OrdinalIgnoreCase))
            throw new UpdateFailure("--package and --install point to the same directory");
        if (IsUnder(ctx.PackageDir, ctx.InstallDir) || IsUnder(ctx.InstallDir, ctx.PackageDir))
            throw new UpdateFailure("--package and --install must not be nested inside each other");

        DetectStaleArtifacts(ctx);

        var dbPath = Path.Combine(ctx.DataDir, "linkpocket.db");
        var mode = UpdateMode.Direct;
        if (File.Exists(dbPath) && new FileInfo(dbPath).Length >= MinSqliteHeaderBytes)
        {
            int epoch;
            try
            {
                epoch = EpochReader.ReadUserVersion(dbPath);
            }
            catch (Exception ex)
            {
                throw new UpdateFailure($"cannot read the database header ({dbPath}): {ex.Message}");
            }

            if (epoch == SchemaEpoch.EpochVersion) mode = UpdateMode.Direct;
            else if (epoch < SchemaEpoch.EpochVersion) mode = UpdateMode.Bridge;
            else
                throw new UpdateFailure(
                    $"the data file was created by a newer version (epoch {epoch} > {SchemaEpoch.EpochVersion}); "
                    + "this updater cannot downgrade - use the newer package instead");
        }

        if (mode == UpdateMode.Bridge)
        {
            var oldCli = Path.Combine(ctx.InstallDir, "tools", "linkpocket.exe");
            if (!File.Exists(oldCli))
                throw new UpdateFailure(
                    "converting across database formats needs the old command-line client, but it is missing: " + oldCli);
            if (!File.Exists(Path.Combine(ctx.PackageDir, "tools", "linkpocket.exe")))
                throw new UpdateFailure("the package is missing tools\\linkpocket.exe (required to import the converted backup)");
        }

        var packageBytes = StageIo.DirectorySize(ctx.PackageDir);
        var oldBytes = StageIo.DirectorySize(ctx.InstallDir);
        var free = FreeSpaceOn(ctx.InstallDir);
        var required = packageBytes + oldBytes + SpaceMarginBytes;
        if (free >= 0 && free < required)
            throw new UpdateFailure(
                $"not enough disk space on the install volume: need about {Mb(required)}, free {Mb(free)} "
                + $"(package {Mb(packageBytes)} + current install {Mb(oldBytes)} + 64 MB margin)");

        var fromVersion = ReadProductVersion(installExe);
        var toVersion = ReadProductVersion(packageExe);
        emit($"preflight ok: {Mb(free)} free, package {Mb(packageBytes)}, current install {Mb(oldBytes)}");
        return new PreflightResult(mode, fromVersion, toVersion);
    }

    /// <summary>恢复线索检测：安装目录旁已有 `data.pending-*` / `LinkPocket.old-*` = 上次中断，拒绝 + 指路（不替用户猜）。</summary>
    private static void DetectStaleArtifacts(UpdateContext ctx)
    {
        foreach (var dir in Directory.EnumerateDirectories(ctx.ParentDir))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("data.pending-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(ctx.InstallName + ".old-", StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateFailure(
                    $"a previous update seems to have been interrupted: '{dir}' exists. "
                    + "Recover it manually first (move it back where it belongs), then re-run the updater.");
            }
        }
    }

    private static bool IsUnder(string candidate, string root)
        => candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static long FreeSpaceOn(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;   // 取不到就不拦（空间检查是保险，不是判据）
        }
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:N1} MB";

    /// <summary>读 exe 的产品版本（单文件发布也带 VERSIONINFO 资源）；读不到 = null（链解析时会如实拒绝）。</summary>
    internal static string? ReadProductVersion(string exePath)
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
}

/// <summary>库头纪元戳读取（唯一实现）：只读打开 + PRAGMA user_version——不解析 schema、不做任何写入。</summary>
internal static class EpochReader
{
    public static int ReadUserVersion(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            // 关池化：连接池会把句柄留到进程退出——接下来 data\ 要整体改名搬移，任何残留句柄都会让搬移门误判"被占用"
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
