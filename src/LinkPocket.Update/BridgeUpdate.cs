using System.Security.Cryptography;
using System.Text.Json;

namespace LinkPocket.Update;

/// <summary>
/// 乙 · 跨纪元例外路径（§5.3 乙）：**先导出、校验（sha256 + 计数）、后动任何东西**——
/// 旧版 CLI 导出 → 设置 / 宏 / 字体复制到暂存副本（设置先过步骤链）→ 旧目录留存 → 新装落地 →
/// 新版 CLI 导入（**计数对账一致才算过**）→ 设置副本就位 → 删旧、启动。
/// **不挪 `data.pending`**（旧数据随旧目录；回滚 = 改回旧目录）；失败**保留暂存区**（内含导出件）并指路。
/// </summary>
public static class BridgeUpdate
{
    public static void Execute(UpdateContext ctx, ICliRunner cli, Action<string> launch, Action<string> emit, Action<string> stage)
    {
        var staging = Path.Combine(ctx.StagingDir, "bridge");
        Directory.CreateDirectory(staging);
        var backupPath = Path.Combine(staging, "bookmarks.lpbackup");
        var carryDir = Path.Combine(staging, "settings");
        if (Directory.Exists(carryDir)) Directory.Delete(carryDir, recursive: true);
        Directory.CreateDirectory(carryDir);

        var oldCli = Path.Combine(ctx.InstallDir, "tools", "linkpocket.exe");
        var newCli = Path.Combine(ctx.InstallDir, "tools", "linkpocket.exe");   // 落地后即新装的 CLI（逐字走一遍刚装好的件）
        var oldDb = Path.Combine(ctx.DataDir, "linkpocket.db");

        string? oldPath = null;
        var newInstalled = false;

        try
        {
            stage("export");
            emit("exporting bookmarks with the old version ...");
            var exportOutput = RunCli(cli, oldCli,
                ["--db", oldDb, "backup", "export", "--output_path", backupPath, "--json"], "backup.export");
            var (exportedFolders, exportedLinks) = ReadCounts(exportOutput, "total_folders", "total_links", "backup.export");
            ctx.Report.Counts["exported_folders"] = exportedFolders;
            ctx.Report.Counts["exported_links"] = exportedLinks;
            emit($"exported {exportedFolders} folders / {exportedLinks} bookmarks (sha256 {Sha256Prefix(backupPath)})");

            stage("copy-settings");
            CopyCarrySet(ctx.DataDir, carryDir, emit);
            SettingsChain.Run(carryDir, ctx.PackageDir, ctx.StagingDir, ctx.FromVersion, ctx.ToVersion, ctx.Report, emit);

            stage("move-old");
            oldPath = Path.Combine(ctx.ParentDir, ctx.InstallName + ".old-" + ctx.Timestamp);
            StageIo.Move(ctx.InstallDir, oldPath,
                "the install directory is in use (close LinkPocket, the CLI and the MCP gateway, then retry)", emit);

            stage("install");
            Directory.CreateDirectory(ctx.InstallDir);
            StageIo.CopyTree(ctx.PackageDir, ctx.InstallDir, ctx.SkipList, emit);
            newInstalled = true;

            stage("import");
            var newDataDir = Path.Combine(ctx.InstallDir, "data");
            Directory.CreateDirectory(newDataDir);
            var importOutput = RunCli(cli, newCli,
                ["--db", Path.Combine(newDataDir, "linkpocket.db"), "backup", "import", "--file_path", backupPath, "--yes", "--json"],
                "backup.import");
            var (importedFolders, importedLinks) = ReadCounts(importOutput, "folders_created", "links_created", "backup.import");
            ctx.Report.Counts["imported_folders"] = importedFolders;
            ctx.Report.Counts["imported_links"] = importedLinks;
            if (importedFolders != exportedFolders || importedLinks != exportedLinks)
                throw new UpdateFailure(
                    $"count mismatch after import: exported {exportedFolders} folders / {exportedLinks} bookmarks, "
                    + $"but imported {importedFolders} / {importedLinks}; refusing to continue");
            emit($"imported {importedFolders} folders / {importedLinks} bookmarks (counts match the export)");

            stage("land-settings");
            StageIo.CopyTree(carryDir, newDataDir, [], emit);

            stage("cleanup-old");
            if (oldPath != null && Directory.Exists(oldPath))
            {
                if (StageIo.TryDeleteTree(oldPath, emit)) oldPath = null;
            }

            stage("launch");
            launch(Path.Combine(ctx.InstallDir, "LinkPocket.exe"));
            emit("new version launched");
        }
        catch (Exception ex)
        {
            emit("FAILED: " + ex.Message);
            emit($"the staging area is kept (it holds the exported backup): {staging}");
            Rollback(ctx, ref oldPath, newInstalled, emit);
            throw;
        }
    }

    /// <summary>回滚：删未成的新装 → 旧目录改回（暂存区保留——里面有导出件，数据不丢）。</summary>
    private static void Rollback(UpdateContext ctx, ref string? oldPath, bool newInstalled, Action<string> emit)
    {
        emit("rolling back ...");

        if (newInstalled && Directory.Exists(ctx.InstallDir))
        {
            StageIo.TryDeleteTree(ctx.InstallDir, emit);
        }

        if (oldPath != null && Directory.Exists(oldPath) && !Directory.Exists(ctx.InstallDir))
        {
            if (StageIo.MoveQuiet(oldPath, ctx.InstallDir))
            {
                oldPath = null;
                emit("old install restored");
            }
            else
            {
                emit("WARNING: could not restore the old install directory");
            }
        }

        if (oldPath != null)
            emit($"WARNING: manual recovery may be needed: {oldPath}");
    }

    /// <summary>携带集（§4.1）：设置 / 宏 / 字体——**用量与运行痕迹不带**（`ai\usage.json` 剔除）。</summary>
    private static void CopyCarrySet(string dataDir, string carryDir, Action<string> emit)
    {
        if (!Directory.Exists(dataDir)) return;

        CopyFile("ui-preferences.json");
        CopyFile("macros.json");
        CopyDirectory("ai");
        CopyDirectory("fonts");

        var usage = Path.Combine(carryDir, "ai", "usage.json");
        if (File.Exists(usage))
        {
            File.Delete(usage);
            emit("not carried: ai\\usage.json (usage statistics are not migrated)");
        }

        void CopyFile(string relative)
        {
            var source = Path.Combine(dataDir, relative);
            if (!File.Exists(source)) return;
            var destination = Path.Combine(carryDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            emit($"carried: {relative}");
        }

        void CopyDirectory(string relative)
        {
            var source = Path.Combine(dataDir, relative);
            if (!Directory.Exists(source)) return;
            StageIo.CopyTree(source, Path.Combine(carryDir, relative), [], emit);
            emit($"carried: {relative}\\ (directory)");
        }
    }

    private static string RunCli(ICliRunner cli, string exePath, string[] args, string command)
    {
        CliResult result;
        try
        {
            result = cli.Run(exePath, args);
        }
        catch (Exception ex)
        {
            throw new UpdateFailure($"failed to start the CLI for {command}: {ex.Message}");
        }

        if (result.ExitCode != 0)
            throw new UpdateFailure($"'{command}' failed (exit {result.ExitCode}): {FirstLine(result.StdErr, result.StdOut)}");
        return result.StdOut;
    }

    private static (long Folders, long Links) ReadCounts(string stdout, string foldersKey, string linksKey, string command)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var data = document.RootElement.GetProperty("data");
            return (data.GetProperty(foldersKey).GetInt64(), data.GetProperty(linksKey).GetInt64());
        }
        catch (Exception ex)
        {
            throw new UpdateFailure($"could not read the counts from the '{command}' output: {ex.Message}");
        }
    }

    private static string FirstLine(params string[] candidates)
    {
        foreach (var text in candidates)
        {
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                return trimmed.Length > 300 ? trimmed[..300] + "..." : trimmed;
            }
        }

        return "(no output)";
    }

    private static string Sha256Prefix(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexStringLower(hash)[..16] + "...";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "(hash unavailable)";
        }
    }
}
