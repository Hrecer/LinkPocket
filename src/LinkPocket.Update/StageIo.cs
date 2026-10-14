namespace LinkPocket.Update;

/// <summary>搬运用文件操作（唯一实现）：复制树（带跳过清单）/ 目录体积 / 尽力删除 / 改名（失败带上下文）。</summary>
internal static class StageIo
{
    public static void CopyTree(string sourceDir, string targetDir, IReadOnlyList<string> skip, Action<string> emit)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (PackageLayout.IsSkipped(skip, relative)) continue;
            var destination = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        emit($"copied {Path.GetFileName(sourceDir)} -> {targetDir}");
    }

    public static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // 取不到体积按 0 计（只影响空间检查的保守度）
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 整目录枚举失败按 0 计：空间检查是保险，不是判据
        }

        return total;
    }

    public static bool TryDeleteTree(string dir, Action<string> emit)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            emit($"WARNING: could not delete '{dir}': {ex.Message} (it was left in place)");
            return false;
        }
    }

    public static void Move(string source, string destination, string failureContext, Action<string> emit)
    {
        try
        {
            Directory.Move(source, destination);
            emit($"moved {source} -> {destination}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateFailure($"{failureContext}: {ex.Message}");
        }
    }

    public static bool MoveQuiet(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
