using LinkPocket.Contracts;

namespace LinkPocket.Update;

/// <summary>
/// 清理旧版本的单文件解压目录：`%TEMP%\.net\LinkPocket\<bundle-hash>`。
/// 更新器在新版启动前执行——此时旧版已退出，新版还没开始解压；被占用的目录尽力删除失败即保留。
/// 只碰 LinkPocket 自己的目录，不碰 `.net` 下其他应用与更新器自身的目录。
/// </summary>
public static class BundleExtractionCleanup
{
    private const string AppBundleName = "LinkPocket";

    /// <param name="emit">进度出口。</param>
    /// <param name="extractBaseDir">测试注入的解压基目录；null = 按 .NET 单文件规则解析。</param>
    public static void Run(Action<string> emit, string? extractBaseDir = null)
    {
        var baseDir = extractBaseDir ?? ResolveBaseDirectory();
        var appDir = Path.Combine(baseDir, AppBundleName);
        if (!Directory.Exists(appDir))
        {
            emit("bundle extraction cleanup: nothing to clean");
            return;
        }

        var removed = 0;
        var kept = 0;
        List<string> bundleDirs;
        try
        {
            bundleDirs = Directory.EnumerateDirectories(appDir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            emit($"WARNING: could not enumerate '{appDir}': {ex.Message} (cleanup skipped)");
            return;
        }

        foreach (var dir in bundleDirs)
        {
            if (StageIo.TryDeleteTree(dir, emit)) removed++;
            else kept++;
        }

        try
        {
            if (Directory.GetFileSystemEntries(appDir).Length == 0)
                Directory.Delete(appDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            emit($"WARNING: could not delete '{appDir}': {ex.Message} (it was left in place)");
        }

        emit($"bundle extraction cleanup: removed {removed}, kept {kept}");
    }

    private static string ResolveBaseDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(UpdateStaging.TempRoot(), ".net")
            : configured.Trim();
    }
}
