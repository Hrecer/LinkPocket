using System.Text.RegularExpressions;
using Xunit;

namespace LinkPocket.Architecture.Tests;

/// <summary>
/// **隐性全局不变量**的强制断言：那些"代码里事实上一直成立、但只靠文档与自觉维系"的红线。
///
/// <para>为什么单独成文件：分层引用、颜色、键位、i18n 那几类红线都有明确的"形状"可抓；
/// 这一批是**使用方式**层面的——没有形状，只有"不许这么写"。它们的共同特点是
/// **一旦破例不会立刻出事**，只会慢慢变成"历史事故"（写进 <c>WARNINGS.md</c> 的那些）。</para>
///
/// <para>每条都必须能红：判据在注释里写明"它量的是什么"，否则将来有人放宽判据时无从察觉。</para>
/// </summary>
public class ImplicitInvariantTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LinkPocket.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? Directory.GetCurrentDirectory();
        }
    }

    /// <summary>产品源文件（排除 bin/obj 与测试工程——测试可以自由造临时文件）。</summary>
    private static IEnumerable<(string Path, string Text)> ProductSources()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            // 数据层/引擎层用正则做重活时需要它；界面层不引（见 LayerRulesTests 的 UI 纪律）。
            .Select(p => (Path: p, Text: File.ReadAllText(p)));

    /// <summary>
    /// H1 临时目录只有一个入口：<c>Path.GetTempPath()</c> / <c>%TEMP%</c> 只许出现在
    /// <c>LinkPocket.Engine/TempArea.cs</c>（唯一入口，尊重 <c>LP_TEMP_ROOT</c>，临时物不落用户配置目录）。
    /// <para><b>量的是什么</b>：任何模块新写 <c>Path.GetTempPath()</c> 都会把临时文件甩进用户 %TEMP%
    /// （历史事故：一次调试在 %TEMP% 写了 2000+ 个探针文件，累计近百 MB，事后清理成本极高）。</para>
    /// </summary>
    [Fact]
    public void H1_临时目录唯一入口()
    {
        var entry = "TempArea.cs";
        foreach (var (path, text) in ProductSources())
            foreach (Match hit in Regex.Matches(text, @"GetTempPath|Environment\.GetEnvironmentVariable\(\s*""TEMP"""))
            {
                var file = Path.GetFileName(path);
                Assert.True(file == entry,
                    $"{file} 直接取系统临时目录：临时物必须经 TempArea（唯一入口，尊重 LP_TEMP_ROOT）。命中：{hit.Value}");
            }
    }

    /// <summary>
    /// H2 JSON/数据文件的写只有原子写一个实现：<c>File.WriteAllText</c> / <c>File.AppendAllText</c> /
    /// <c>File.WriteAllBytes</c> 只许出现在 <c>AtomicFile</c>（AI 数据文件唯一实现：同目录临时文件 + 原子替换）。
    /// <para><b>量的是什么</b>：直接写会**先清空再写**——进程被杀/崩时留下半截文件，用户的
    /// AI 会话/凭据/技能就废了（本仓真实修过一次：G 盘 overlay 上 <c>File.Replace</c> 瞬态失败）。
    /// 白名单含 <c>LogPipeline</c> / <c>JsonlFileSink</c>（日志管道是 append-only 文本流，
    /// 半截行可容忍且有 flush 语义，不属于"配置类数据文件"）。</para>
    /// </summary>
    [Theory]
    [InlineData("File.WriteAllText")]
    [InlineData("File.AppendAllText")]
    [InlineData("File.WriteAllBytes")]
    public void H2_数据文件只许原子写(string call)
    {
        var allowed = new[]
        {
            "AtomicFile.cs",        // 原子写的唯一实现
            "LogPipeline.cs",       // 日志泵：append-only 流
            "JsonlFileSink.cs",     // JSONL 落盘：同上
            "CliRunner.Views.cs",   // `--out <文件>`：一次性输出产物（不是配置/数据文件，失败即整条命令失败）
        };
        foreach (var (path, text) in ProductSources())
            // 负向后视：`AtomicFile.WriteAllText(...)` 是**允许**的那一处本身，不能被子串误判成 `File.WriteAllText`
            foreach (Match hit in Regex.Matches(text, @"(?<![\w.])" + Regex.Escape(call) + @"\s*\("))
            {
                var file = Path.GetFileName(path);
                Assert.True(allowed.Contains(file, StringComparer.Ordinal),
                    $"{file} 直接调 {call}：配置/数据文件必须经 AtomicFile（原子写唯一实现），否则崩溃即半截文件。");
            }
    }

    /// <summary>
    /// H3 吞异常必须写明理由：<c>catch</c> 块**不得是纯空的**（<c>{ }</c>）——注释本身就是理由，
    /// 所以带注释的关闭路径例外（它们写清了为何可吞）。
    /// <para><b>量的是什么</b>：光秃秃的 <c>catch { }</c> 会让"失败要暴露"（ARCHITECTURE 不变量 10/11）
    /// 悄悄破掉——半年后没人知道这里吞过什么。</para>
    /// </summary>
    [Fact]
    public void H3_吞异常必须写明理由()
    {
        // catch (...) { } —— 块内连一条注释都没有
        var emptyCatch = new Regex(@"catch\s*(\([^)]*\))?\s*\{\s*\}", RegexOptions.Singleline);
        foreach (var (path, text) in ProductSources())
            Assert.True(!emptyCatch.IsMatch(text),
                $"{Path.GetFileName(path)} 有光秃秃的 catch：吞异常必须写明为何可吞（一行注释即可），" +
                "否则「失败要暴露」这条红线会被悄悄破掉。");
    }

    /// <summary>
    /// H4 HTTP 客户端不得在调用点随手 <c>new</c>：<c>new HttpClient</c> 只许出现在已审的三个文件里
    /// （AI 传输 / favicon 缓存 / 链接元数据抓取）。<para><b>量的是什么</b>：每次 new 都是新的
    /// 连接池与 socket TIME_WAIT 累积，高频调用下端口耗尽（经典的 HttpClient 陷阱）；
    /// 这三处都自己管 <c>Handler</c> 复用与超时，改第四处就要重新论证一次。</para>
    /// </summary>
    [Fact]
    public void H4_HttpClient只在已审文件创建()
    {
        var allowed = new[]
        {
            "AiHttp.cs",           // AI 提供方传输（统一超时/重试/代理）
            "FaviconCache.cs",     // 图标缓存（带并发闸）
            "MetadataFetcher.cs",  // 链接元数据抓取（短时任务）
        };
        foreach (var (path, text) in ProductSources())
            if (Regex.IsMatch(text, @"new\s+HttpClient\s*\(") || Regex.IsMatch(text, @"new\s+HttpClientHandler\s*\("))
                Assert.True(allowed.Contains(Path.GetFileName(path), StringComparer.Ordinal),
                    $"{Path.GetFileName(path)} 新建 HttpClient：连接池与 socket 耗尽的经典坑；" +
                    "确需新增请先论证复用与超时，再把文件名加进本白名单。");
    }

    /// <summary>
    /// H5 界面层零同步阻塞：UI 侧项目不得出现 <c>.Wait()</c> 或 <c>.Result</c>（阻塞 UI 线程 → 界面冻结；
    /// 在有同步上下文时还可能与续体互等成死锁）。
    /// <para><b>量的是什么</b>：全仓当前零命中。此前 <c>SearchViewModel</c> 三处
    /// <c>task.Result</c>（跟在 <c>await Task.WhenAll</c> 之后、语法上不阻塞）看似安全，实则是
    /// 一颗雷：谁在前面插一行代码就变成真阻塞。已统一改成 <c>await</c>。</para>
    /// </summary>
    [Fact]
    public void H5_界面层零同步阻塞()
    {
        var uiProjects = new[] { "LinkPocket.UIKit", "LinkPocket.UI.Browser", "LinkPocket.UI.Search", "LinkPocket.UI.SmartLists", "LinkPocket.UI.Tools", "LinkPocket.UI.Ai", "LinkPocket.UI.Settings", "LinkPocket.App" };
        foreach (var project in uiProjects)
        {
            var dir = Path.Combine(RepoRoot, "src", project);
            if (!Directory.Exists(dir)) continue;   // 页面项目可能已合并/改名，缺席即无面可扫
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                var text = File.ReadAllText(file);
                // `.Result` 后面**不得**再跟 `.` 或标识符字符——`LinkLauncher.Result.NotWebAddress`
                // 里的 Result 是枚举类型名（既同步属性），不能误判成 Task.Result 阻塞。
                foreach (Match hit in Regex.Matches(text, @"\.Wait\s*\(\s*\)|\.Result\b(?![\.\w])"))
                    Assert.Fail(
                        $"{project}/{Path.GetFileName(file)} 出现同步阻塞 `{hit.Value}`：界面层一律 await，" +
                        "阻塞会冻结界面并可能与续体互等成死锁。");
            }
        }
    }

    /// <summary>
    /// H6 已灭绝的类型不得复活：静态服务定位器之外的**其它历史实现**同样登记在册
    /// ——浏览模块的旧选中管理器 <c>SelectionManager</c> 曾有过读者与写者，今天零残留；
    /// 架构不变量 12「选中单一事实来源」靠"它不再存在"保证，因此必须由断言守住。
    /// <para><b>量的是什么</b>：任何源文件里都不许再声明这些类型（注释里的历史说明不算，见下方白名单）。</para>
    /// </summary>
    [Fact]
    public void H6_已灭绝的定位器与选中管理器零残留()
    {
        var banned = new[]
        {
            "class AppServices",       // 前端对象图的前身
            "class UiCoordinator",     // 前端协调器的前身
            "class BrowserLocateHost", // 静态跳转宿主
            "class SelectionManager",  // 浏览模块旧选中管理器（不变量 12 的对立面）
        };
        foreach (var (path, text) in ProductSources())
            foreach (var token in banned)
                Assert.True(!Regex.IsMatch(text, Regex.Escape(token)),
                    $"{Path.GetFileName(path)} 出现已灭绝的类型：{token}");
    }
}