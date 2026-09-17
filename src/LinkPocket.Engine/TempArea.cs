namespace LinkPocket.Engine;

/// <summary>
/// 临时文件根解析（**工作区卫生约定**）：缺省 = 系统临时目录；
/// 环境变量 <c>LP_TEMP_ROOT</c> 指到别的目录时用它。
///
/// <para>CI 与测试把它指向工作区内的 <c>build/artifacts/tmp/</c>，这样临时库、暂存区、日志都落在项目里，
/// 不会往用户配置目录（<c>C:\Users\&lt;用户&gt;\</c>，含 %TEMP%）堆垃圾——历史上这里堆过 2000+ 个文件。</para>
///
/// <para>生产宿主应显式传入自己的数据目录（组合根决定），不要依赖环境变量。</para>
/// </summary>
public static class TempArea
{
    /// <summary>指向临时根的环境变量名。</summary>
    public const string EnvVar = "LP_TEMP_ROOT";

    /// <summary>解析当前临时根（不存在则创建）。</summary>
    public static string Resolve()
    {
        var root = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(root)) return Path.GetTempPath();
        Directory.CreateDirectory(root);
        return root;
    }
}
