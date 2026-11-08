using System.Runtime.InteropServices;

namespace LinkPocket.Contracts;

/// <summary>
/// 单文件发布的原生库启动引导（Windows）：把关键模块从 exe 自解压目录**按完整路径**装进进程，
/// 并保持句柄到进程结束。两件事各有一个明确目标：
/// <list type="bullet">
/// <item><b>钉住 vcruntime</b>：单文件宿主在退出时会清理解压目录；WPF 的 C++/CLI 程序集在 CLR
/// 关闭阶段还要用 `vcruntime140_cor3.dll`，文件先被删掉就会抛 `DllNotFoundException`，进程崩在
/// 终止阶段并留下数据库 shm 占用。已加载的 DLL 无法被删除，句柄保持到进程结束即可避开这个时序。</item>
/// <item><b>预加载 curl</b>：curl-impersonate 自带的解析器只认应用目录 / runtimes / 系统路径，
/// 不认单文件解压目录。按完整路径先装进进程后，解析器按名字加载时会直接命中已加载模块。</item>
/// </list>
/// 失败一律静默降级（保持原有行为），由调用方既有日志暴露；本类型不改变任何交付形态。
/// </summary>
public static class EmbeddedNativeBootstrap
{
    private static readonly object Gate = new();
    private static IntPtr _vcruntime;
    private static IntPtr _curl;
    private static bool _vcruntimeTried;
    private static bool _curlTried;

    /// <summary>vcruntime 是否已成功钉住（诊断用）。</summary>
    public static bool VcruntimePinned => _vcruntime != IntPtr.Zero;

    /// <summary>curl 原生库是否已成功预加载（诊断用）。</summary>
    public static bool CurlPreloaded => _curl != IntPtr.Zero;

    /// <summary>钉住 vcruntime；只对 Windows 有意义，重复调用无副作用。</summary>
    public static void PinVcruntime()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            if (_vcruntimeTried) return;
            _vcruntimeTried = true;
            _vcruntime = TryLoad("vcruntime140_cor3.dll");
        }
    }

    /// <summary>预加载 curl 及其依赖；只对 Windows 有意义，重复调用无副作用。</summary>
    public static void PreloadCurl()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            if (_curlTried) return;
            _curlTried = true;
            _ = TryLoad("vcruntime140_cor3.dll");
            _ = TryLoad("zlib.dll");
            _ = TryLoad("libcurl-impersonate.dll");
            _curl = TryLoad("curl_shim.dll");
        }
    }

    private static IntPtr TryLoad(string fileName)
    {
        foreach (var dir in SearchDirectories())
        {
            var path = Path.Combine(dir, fileName);
            if (!File.Exists(path)) continue;
            try
            {
                return NativeLibrary.Load(path);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException
                                           or IOException or UnauthorizedAccessException)
            {
                // 继续尝试下一个目录；全部失败时保持原行为（由调用方既有路径报错）。
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        // 单文件宿主把解压目录写进这个运行时数据；普通构建里则包含应用目录。
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs)
        {
            foreach (var dir in dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                yield return dir;
        }

        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
    }
}
