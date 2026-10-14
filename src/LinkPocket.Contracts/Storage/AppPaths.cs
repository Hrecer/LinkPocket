namespace LinkPocket.Contracts;

/// <summary>
/// 程序根 / 数据根 / 库文件的**唯一路径解析实现**（应用 / CLI / MCP / 内部工具同一口径）。
///
/// <para><b>布局（4.0.0 起）</b>：程序区（exe / tools / 文档）与数据区分离——数据区 = <c>data\</c>
/// 子目录，运行期一切写落这里（见 <c>内部资产/文档/UPDATE.md</c> §2）；更新 = 程序区整体换装 +
/// 数据区重建，库目录不再是"exe 旁边那一堆"。因此"库在哪"必须由**一处**解析：契约层（全仓依赖的最底层），
/// 谁都不能再各拼一份 <c>AppContext.BaseDirectory/linkpocket.db</c>。</para>
///
/// <para><b>程序根</b>：从可执行文件所在目录向上找最近含 <c>data\</c> 子目录或含
/// <c>LinkPocket.exe</c> 的目录。CLI / 网关在 <c>tools\</c> 里（<c>linkpocket.exe</c> / <c>linkpocket-mcp.exe</c>），
/// 靠"文件名精确区分大小写"这一条与主程序 exe 区分——Windows 文件系统不区分大小写，
/// 按 <c>File.Exists("LinkPocket.exe")</c> 判会把 <c>tools\</c> 误判成程序根。</para>
///
/// <para><b>库文件发现（优先级由高到低）</b>：
/// ① 显式参数（命令行 <c>--db</c>）；② 环境变量 <c>LINKPOCKET_DB</c>；
/// ③ 从当前目录向上回溯到第一个含 <c>data\linkpocket.db</c> 的目录（命令在数据目录树里就地执行时无需传参）；
/// ④ 程序根 <c>data\linkpocket.db</c>（首启时可能还不存在，由建库方按需创建）。
/// 旧的"程序目录平铺 <c>linkpocket.db</c>"**一律不再识别**（不检测、不提示）。</para>
/// </summary>
public static class AppPaths
{
    /// <summary>数据区目录名（程序根下）。</summary>
    public const string DataDirectoryName = "data";

    /// <summary>书签库文件名（数据区内）。</summary>
    public const string DatabaseFileName = "linkpocket.db";

    /// <summary>库路径的环境变量覆盖名。</summary>
    public const string DatabaseEnvironmentVariable = "LINKPOCKET_DB";

    /// <summary>主程序可执行文件名（程序根标记；**按文件名精确大小写**比对，理由见类型注释）。</summary>
    private const string AppExecutableName = "LinkPocket.exe";

    /// <summary>程序根（见类型注释；任何层都不该自己重新推导它；返回路径不带尾分隔符）。</summary>
    public static string ProgramRoot { get; } = FindProgramRoot(AppContext.BaseDirectory);

    /// <summary>数据根 = 程序根 <c>data\</c>：AI / 字体 / 图标缓存 / 日志 / 界面偏好等运行数据的落点。</summary>
    public static string DataRoot { get; } = Path.Combine(ProgramRoot, DataDirectoryName);

    /// <summary>库文件缺省路径（= 数据根 <c>linkpocket.db</c>；仅发现规则的兜底一支，见 <see cref="ResolveDatabasePath(string?, out string)"/>）。</summary>
    public static string DefaultDatabasePath { get; } = Path.Combine(DataRoot, DatabaseFileName);

    /// <summary>
    /// 库路径解析（生产入口）：<c>--db</c> → 环境变量 → 当前目录回溯 → 数据根缺省。
    /// <paramref name="source"/> 为命中的那一支（<c>argument</c> / <c>env</c> / <c>search</c> / <c>default</c>），供回显与排障。
    /// </summary>
    public static string ResolveDatabasePath(string? explicitPath, out string source)
        => ResolveDatabasePath(
            explicitPath,
            Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable),
            Directory.GetCurrentDirectory(),
            DefaultDatabasePath,
            out source);

    /// <summary>
    /// 解析核心（四个输入全部显式给出，**纯函数、不吃进程状态**；测试缝——生产代码走上面的入口）。
    /// </summary>
    /// <param name="explicitPath">命令行 <c>--db</c> 的值。</param>
    /// <param name="environmentPath">环境变量 <c>LINKPOCKET_DB</c> 的值。</param>
    /// <param name="startDirectory">回溯搜索起点（生产 = 当前目录）。</param>
    /// <param name="fallbackPath">全部落空时的缺省路径（生产 = <see cref="DefaultDatabasePath"/>）。</param>
    public static string ResolveDatabasePath(string? explicitPath, string? environmentPath,
        string startDirectory, string fallbackPath, out string source)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            source = "argument";
            return Path.GetFullPath(explicitPath.Trim());
        }

        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            source = "env";
            return Path.GetFullPath(environmentPath.Trim());
        }

        var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, DataDirectoryName, DatabaseFileName);
            if (File.Exists(candidate))
            {
                source = "search";
                return candidate;
            }
            dir = dir.Parent;
        }

        source = "default";
        return Path.GetFullPath(fallbackPath);
    }

    /// <summary>
    /// 程序根解析核心（纯函数；生产入口 = <see cref="ProgramRoot"/>）：从 <paramref name="startDirectory"/>
    /// 向上找最近含 <c>data\</c> 子目录或含 <c>LinkPocket.exe</c> 的目录；一路到卷根都没有 → 起点目录本身。
    /// 返回路径一律去掉尾分隔符（<c>AppContext.BaseDirectory</c> 恒带尾分隔符；尾分隔符进命令行拼参会
    /// 触发 <c>\"</c> 转义事故——见 WARNINGS 173）。
    /// </summary>
    public static string FindProgramRoot(string startDirectory)
    {
        var start = Path.GetFullPath(startDirectory);
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, DataDirectoryName)))
                return Path.TrimEndingDirectorySeparator(dir.FullName);
            if (ContainsAppExecutable(dir))
                return Path.TrimEndingDirectorySeparator(dir.FullName);
            dir = dir.Parent;
        }
        return Path.TrimEndingDirectorySeparator(start);
    }

    /// <summary>目录里是否恰好有 <c>LinkPocket.exe</c>（精确大小写；权限受限 / 瞬态 IO 的目录按"没有"处理，继续向上）。</summary>
    private static bool ContainsAppExecutable(DirectoryInfo dir)
    {
        try
        {
            foreach (var file in dir.EnumerateFiles(AppExecutableName))
                if (string.Equals(file.Name, AppExecutableName, StringComparison.Ordinal)) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 不可读的目录 = 没有标记（卷根 / 受限目录），继续向上找
        }
        return false;
    }
}
