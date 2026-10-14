using System.Diagnostics;

namespace LinkPocket.Update;

/// <summary>
/// 更新器**唯一流水线**（UPDATE.md §5.3）：预检 → 路由（甲 = 同纪元直搬 / 乙 = 跨纪元走桥）→ 执行 → 回滚/收尾。
/// 失败一律先回滚到"原样"再把原因上抛（可逆先行；唯一不可逆动作 = 成功收尾删旧目录）。
/// </summary>
public sealed class UpdateRunner(
    ICliRunner cli,
    Action<string> launch,
    Action<string> emit,
    string stagingDirectory,
    Action<string>? onStage = null)
{
    public void Run(UpdateArgs args, UpdateReport report)
    {
        var packageDir = Normalize(args.PackagePath);
        var installDir = Normalize(args.InstallPath);
        var parentDir = Path.GetDirectoryName(installDir)
            ?? throw new UpdateFailure($"cannot resolve the parent directory of the install path: {installDir}");
        var context = new UpdateContext
        {
            Report = report,
            StagingDir = stagingDirectory,
            PackageDir = packageDir,
            InstallDir = installDir,
            ParentDir = parentDir,
            InstallName = Path.GetFileName(installDir),
        };

        if (args.WaitPid is int pid) WaitForProcessExit(pid);

        var preflight = Preflight.Run(context, emit);
        report.Mode = preflight.Mode == UpdateMode.Direct ? "direct" : "bridge";
        report.FromVersion = preflight.FromVersion;
        report.ToVersion = preflight.ToVersion;
        context.FromVersion = preflight.FromVersion;
        context.ToVersion = preflight.ToVersion;
        context.SkipList = PackageLayout.LoadSkipList(context.PackageDir);
        emit($"mode: {report.Mode} ({report.FromVersion ?? "?"} -> {report.ToVersion ?? "?"})");

        if (preflight.Mode == UpdateMode.Direct) DirectUpdate.Execute(context, launch, emit, Stage);
        else BridgeUpdate.Execute(context, cli, launch, emit, Stage);
        report.Complete();
    }

    private void Stage(string name)
    {
        emit($"stage: {name}");
        onStage?.Invoke(name);
    }

    private void WaitForProcessExit(int pid)
    {
        Process? process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            emit($"process {pid} has already exited");
            return;
        }

        using (process)
        {
            emit($"waiting for process {pid} to exit (up to 120s) ...");
            if (!process.WaitForExit(120_000))
                throw new UpdateFailure($"process {pid} did not exit within 120 seconds; aborting (nothing was touched)");
            emit($"process {pid} exited");
        }
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

/// <summary>一次更新的上下文（路径 + 报告出口）；测试直接构造它驱动单条路径。</summary>
public sealed class UpdateContext
{
    public required UpdateReport Report { get; init; }
    public required string StagingDir { get; init; }
    public required string PackageDir { get; init; }
    public required string InstallDir { get; init; }
    public required string ParentDir { get; init; }
    public required string InstallName { get; init; }
    public string DataDir => Path.Combine(InstallDir, "data");
    public string Timestamp { get; init; } = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
    public IReadOnlyList<string> SkipList { get; set; } = [];
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
}
