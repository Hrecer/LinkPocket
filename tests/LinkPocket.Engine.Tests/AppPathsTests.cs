using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 库 / 程序根路径解析（<see cref="AppPaths"/>，4.0.0 起的统一布局）：
/// 优先级顺序（--db → env → 回溯 → 缺省）、`data\linkpocket.db` 回溯（旧平铺位置不再识别）、
/// 程序根标记（`data\` 子目录 / **精确大小写**的 `LinkPocket.exe`）。
/// </summary>
/// <remarks>
/// 走的是纯函数入口（四个输入全部显式给出），不吃进程状态。
/// "回溯落空"类用例假定 Temp 的祖先链上没有 `data\linkpocket.db` / `LinkPocket.exe` 标记
/// （与开发机现状一致；在别处跑时若恰有此类标记，会以明确的断言失败暴露出来）。
/// </remarks>
public class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lp_apppaths_" + Guid.NewGuid().ToString("N"));

    public AppPathsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 尽力而为 */ }
    }

    [Fact]
    public void 解析_显式参数优先_其次是环境变量()
    {
        var arg = Path.Combine(_root, "arg.db");
        var byArg = AppPaths.ResolveDatabasePath(arg, Path.Combine(_root, "env.db"), _root,
            Path.Combine(_root, "fallback.db"), out var argSource);
        Assert.Equal("argument", argSource);
        Assert.Equal(Path.GetFullPath(arg), byArg);

        // 显式参数只有空白 = 未给（回落环境变量）
        var env = Path.Combine(_root, "env.db");
        var byEnv = AppPaths.ResolveDatabasePath("   ", env, _root,
            Path.Combine(_root, "fallback.db"), out var envSource);
        Assert.Equal("env", envSource);
        Assert.Equal(Path.GetFullPath(env), byEnv);
    }

    [Fact]
    public void 解析_从当前目录向上找_data_而不认平铺旧位置()
    {
        var dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "linkpocket.db"), "");
        File.WriteAllText(Path.Combine(_root, "linkpocket.db"), "");   // 旧平铺位置：一律不再识别

        var deep = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(deep);
        var resolved = AppPaths.ResolveDatabasePath(null, null, deep,
            Path.Combine(_root, "fallback.db"), out var source);

        Assert.Equal("search", source);
        Assert.Equal(Path.Combine(dataDir, "linkpocket.db"), resolved);
    }

    [Fact]
    public void 解析_全部落空_回落缺省()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        var fallback = Path.Combine(_root, "program", "data", "linkpocket.db");

        var resolved = AppPaths.ResolveDatabasePath(null, null, empty, fallback, out var source);
        Assert.Equal("default", source);
        Assert.Equal(Path.GetFullPath(fallback), resolved);
    }

    [Fact]
    public void 程序根_认_data子目录标记()
    {
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(install, "data"));
        var deep = Path.Combine(install, "x", "y");
        Directory.CreateDirectory(deep);

        Assert.Equal(install, AppPaths.FindProgramRoot(deep));
    }

    [Fact]
    public void 程序根_tools里的小写linkpocket_exe不算标记_继续向上()
    {
        // tools\ 里是 CLI 的 linkpocket.exe（小写）：Windows 文件系统不区分大小写，
        // 若用 File.Exists("LinkPocket.exe") 判标记，这里会错停在 tools\（数据区也跟着错位）。
        var install = Path.Combine(_root, "pkg");
        var tools = Path.Combine(install, "tools");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "linkpocket.exe"), "");
        File.WriteAllText(Path.Combine(install, "LinkPocket.exe"), "");

        Assert.Equal(install, AppPaths.FindProgramRoot(tools));
    }

    [Fact]
    public void 程序根_无任何标记_回落起点()
    {
        var start = Path.Combine(_root, "plain", "deeper");
        Directory.CreateDirectory(start);

        Assert.Equal(Path.GetFullPath(start), AppPaths.FindProgramRoot(start));
    }
}
