namespace LinkPocket.Update;

/// <summary>
/// 包结构约定（UPDATE.md §5.2 冻结接口）：`update\steps\`（步骤区）+ `update\settings-manifest.json`
/// （设置版本清单）+ `update\skip-install.txt`（跳过安装清单——更新器与 `update\` 不进安装目录）。
/// </summary>
internal static class PackageLayout
{
    public const string UpdateDirectoryName = "update";
    public const string StepsDirectoryName = "steps";
    public const string ManifestFileName = "settings-manifest.json";
    public const string SkipListFileName = "skip-install.txt";

    /// <summary>缺省跳过项，**始终并入**——文件漏写也不会把更新器与步骤区装进安装目录。</summary>
    private static readonly string[] DefaultSkipEntries = ["LinkPocket-Update.exe", "update\\"];

    public static string UpdateDir(string packageDir) => Path.Combine(packageDir, UpdateDirectoryName);

    public static string StepsDir(string packageDir) => Path.Combine(UpdateDir(packageDir), StepsDirectoryName);

    public static string ManifestPath(string packageDir) => Path.Combine(UpdateDir(packageDir), ManifestFileName);

    public static IReadOnlyList<string> LoadSkipList(string packageDir)
    {
        var path = Path.Combine(UpdateDir(packageDir), SkipListFileName);
        var entries = new List<string>();
        if (File.Exists(path))
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                entries.Add(line);
            }
        }

        foreach (var fallback in DefaultSkipEntries)
        {
            if (!entries.Contains(fallback, StringComparer.OrdinalIgnoreCase)) entries.Add(fallback);
        }

        return entries;
    }

    /// <summary>包内相对路径是否命中跳过清单（分隔符归一；目录条目以 `\` 结尾 = 前缀匹配）。</summary>
    public static bool IsSkipped(IReadOnlyList<string> entries, string relativePath)
    {
        var relative = relativePath.Replace('/', '\\');
        foreach (var entry in entries)
        {
            var normalized = entry.Replace('/', '\\');
            if (normalized.EndsWith('\\'))
            {
                if (relative.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(relative, normalized[..^1], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(relative, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
