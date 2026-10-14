namespace LinkPocket.Update;

/// <summary>
/// 甲 · 同纪元主路径（§5.3 甲；不变量：**数据区只搬不读**）：
/// 改名暂挪 → 旧目录留存 → 新装落地 → 数据回位 → 转换 + 剔除运行痕迹 → 删旧 → 启动。
/// 任一步失败 → 回滚到"原样"（先数据、再新装、再旧目录、后数据回位）。
/// </summary>
internal static class DirectUpdate
{
    public static void Execute(UpdateContext ctx, Action<string> launch, Action<string> emit, Action<string> stage)
    {
        string? pendingPath = null;   // data 暂挪件（与安装目录平级）
        string? oldPath = null;       // 旧程序目录改名件
        var newInstalled = false;
        var dataHome = false;

        try
        {
            stage("move-data");
            if (Directory.Exists(ctx.DataDir))
            {
                pendingPath = Path.Combine(ctx.ParentDir, "data.pending-" + ctx.Timestamp);
                StageIo.Move(ctx.DataDir, pendingPath,
                    "the data directory is in use (close LinkPocket, the CLI and the MCP gateway, then retry)", emit);
            }
            else
            {
                emit("no data directory; nothing to preserve");
            }

            stage("move-old");
            oldPath = Path.Combine(ctx.ParentDir, ctx.InstallName + ".old-" + ctx.Timestamp);
            StageIo.Move(ctx.InstallDir, oldPath,
                "the install directory is in use (close LinkPocket, the CLI and the MCP gateway, then retry)", emit);

            stage("install");
            Directory.CreateDirectory(ctx.InstallDir);
            StageIo.CopyTree(ctx.PackageDir, ctx.InstallDir, ctx.SkipList, emit);
            newInstalled = true;

            stage("restore-data");
            if (pendingPath != null)
            {
                StageIo.Move(pendingPath, ctx.DataDir, "failed to move the data directory back into the new install", emit);
                pendingPath = null;
                dataHome = true;
            }

            stage("convert");
            SettingsChain.Run(ctx.DataDir, ctx.PackageDir, ctx.StagingDir, ctx.FromVersion, ctx.ToVersion, ctx.Report, emit);
            PruneRuntimeTraces(ctx.DataDir, emit);

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
            Rollback(ctx, ref oldPath, ref pendingPath, newInstalled, dataHome, emit);
            throw;
        }
    }

    /// <summary>回滚到"原样"：数据先挪回暂存件 → 删未成的新装 → 旧目录改回 → 数据回位；每步尽力而为并如实留痕。</summary>
    private static void Rollback(UpdateContext ctx, ref string? oldPath, ref string? pendingPath,
        bool newInstalled, bool dataHome, Action<string> emit)
    {
        emit("rolling back ...");

        if (dataHome && Directory.Exists(ctx.DataDir))
        {
            var backAside = Path.Combine(ctx.ParentDir, "data.pending-" + ctx.Timestamp);
            if (StageIo.MoveQuiet(ctx.DataDir, backAside))
            {
                pendingPath = backAside;
                emit($"data moved back aside: {backAside}");
            }
            else
            {
                emit("WARNING: could not move the data directory aside for rollback");
            }
        }

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

        if (pendingPath != null && Directory.Exists(pendingPath) && Directory.Exists(ctx.InstallDir))
        {
            if (StageIo.MoveQuiet(pendingPath, ctx.DataDir))
            {
                pendingPath = null;
                emit("data restored");
            }
            else
            {
                emit("WARNING: could not restore the data directory");
            }
        }

        if (oldPath != null || pendingPath != null)
            emit($"WARNING: manual recovery may be needed; left-over items: {oldPath ?? pendingPath}");
    }

    /// <summary>§0"运行痕迹"清单：随库直搬后**剔除**这些（审计 / 幂等库、撤销栈、AI 用量、图标缓存）——不携带、缓存重建。</summary>
    private static void PruneRuntimeTraces(string dataDir, Action<string> emit)
    {
        if (!Directory.Exists(dataDir)) return;

        string[] files =
        [
            "linkpocket-ops.db", "linkpocket-ops.db-wal", "linkpocket-ops.db-shm",
            "undo-journal.json", "undo-journal.cli.json",
            Path.Combine("ai", "usage.json"),
        ];
        foreach (var relative in files)
        {
            var path = Path.Combine(dataDir, relative);
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                emit($"pruned: {relative}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                emit($"WARNING: could not remove {relative}: {ex.Message} (it will simply stay in the data directory)");
            }
        }

        string[] directories =
        [
            "undo-journal.json.parts", "undo-journal.cli.json.parts", "favicons",
        ];
        foreach (var relative in directories)
        {
            var path = Path.Combine(dataDir, relative);
            try
            {
                if (!Directory.Exists(path)) continue;
                Directory.Delete(path, recursive: true);
                emit($"pruned: {relative}\\ (directory)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                emit($"WARNING: could not remove {relative}\\: {ex.Message} (it will simply stay in the data directory)");
            }
        }
    }
}
