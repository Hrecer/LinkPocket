using System.Xml.Linq;
using Xunit;

namespace LinkPocket.Architecture.Tests;

/// <summary>
/// 架构依赖规则测试：
/// - UI 任何项目禁止引用 Engine/Kernel/Data/Modules.* 实现程序集；
///   UIKit 只依赖 Contracts；UI.* 页面项目只依赖 UIKit+Contracts；
///   Shell(App) 直引共享 Composition（引擎组合根）+ UIKit + 全部 UI.*（组合根唯一例外）；
/// - UI 项目之间零互相引用（模块可单独删除/演进）；
/// - Composition（共享引擎组合根）引用面被精确卡死：Contracts/Kernel/Data/Engine + 九模块；
/// - 静态服务定位器零残留（AppServices/UiCoordinator 已灭绝，防止还潮）。
/// 判定基于 csproj 的 ProjectReference 声明（引用图 = 编译期事实）。
/// </summary>
public class DependencyRulesTests
{
    /// <summary>仓库根目录（从测试二进制位置向上回溯到含 LinkPocket.sln 的目录）。</summary>
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LinkPocket.sln")))
                dir = dir.Parent!;
            return dir!.FullName;
        }
    }

    private static List<string> ProjectReferences(string csprojRelativePath)
    {
        var path = Path.Combine(RepoRoot, csprojRelativePath);
        Assert.True(File.Exists(path), $"项目文件不存在: {csprojRelativePath}");
        return XDocument.Load(path)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('/', Path.DirectorySeparatorChar)))
            .OrderBy(n => n)
            .ToList();
    }

    private const string Contracts = "LinkPocket.Contracts";
    private const string UIKit = "LinkPocket.UIKit";
    private const string Shell = "LinkPocket.App";
    private const string Engine = "LinkPocket.Engine";
    private const string Data = "LinkPocket.Data";
    private const string Kernel = "LinkPocket.Kernel";
    private const string Composition = "LinkPocket.Composition";
    private const string Theming = "LinkPocket.Theming";
    private const string I18n = "LinkPocket.I18n";
    private static readonly string[] UiPages =
    {
        "LinkPocket.UI.Browser", "LinkPocket.UI.Search", "LinkPocket.UI.Trash",
        "LinkPocket.UI.SmartLists", "LinkPocket.UI.Tools", "LinkPocket.UI.Settings",
    };
    private static readonly string[] BusinessModules =
    {
        "LinkPocket.Modules.Folders", "LinkPocket.Modules.Links", "LinkPocket.Modules.Trash",
        "LinkPocket.Modules.Search", "LinkPocket.Modules.Bookmarks", "LinkPocket.Modules.Backup",
        "LinkPocket.Modules.Dedup", "LinkPocket.Modules.Favicon", "LinkPocket.Modules.Maintenance",
        "LinkPocket.Modules.Locate",
    };
    private static readonly string[] ForbiddenForUi =
    {
        Engine, Kernel, Data,
        "LinkPocket.Modules.Folders", "LinkPocket.Modules.Links", "LinkPocket.Modules.Trash",
        "LinkPocket.Modules.Search", "LinkPocket.Modules.Bookmarks", "LinkPocket.Modules.Backup",
        "LinkPocket.Modules.Dedup", "LinkPocket.Modules.Favicon", "LinkPocket.Modules.Maintenance",
        "LinkPocket.Modules.Locate",
    };

    public static IEnumerable<object[]> UiPageProjects() => UiPages.Select(p => new object[] { p });

    [Fact]
    public void UIKit_只依赖Contracts与Theming与I18n()
    {
        // Theming = 主题/字体底层设施（颜色科学 / 派生 / 主题目录 / 字体 / 偏好 / 单点发布）。
        // 它不是控件层，与 UIKit 平级；UIKit 的共享样式只引令牌键 → 需要它。
        var refs = ProjectReferences("src/LinkPocket.UIKit/LinkPocket.UIKit.csproj");
        Assert.Equal(new[] { Contracts, Theming, I18n }.OrderBy(n => n), refs);
    }

    [Fact]
    public void Theming_只依赖Contracts()
    {
        // Theming 是**底层设施**：只许引 Contracts（日志门面 LpLog）。
        // 禁引 UIKit / UI.* / Engine / Data / Kernel / Modules.*（颜色科学不许反过来依赖控件或引擎）。
        var refs = ProjectReferences("src/LinkPocket.Theming/LinkPocket.Theming.csproj");
        Assert.Equal(new[] { Contracts }.OrderBy(n => n), refs);
    }

    [Fact]
    public void Theming_禁止引用控件与引擎实现()
    {
        var refs = ProjectReferences("src/LinkPocket.Theming/LinkPocket.Theming.csproj");
        Assert.DoesNotContain(refs, r => ForbiddenForUi.Contains(r) || r == UIKit || r == Shell || UiPages.Contains(r));
    }

    [Fact]
    public void I18n_只依赖Contracts()
    {
        // I18n 与 Theming 平级：只许引 Contracts（日志门面 LpLog）。
        // **禁引 Theming**：语言字段的持久化在 UiPreferences（Theming 拥有"存什么"）、语义在 I18n
        // （拥有"怎么生效"），两边由组合根 App 搬运；直接互引就成了环。
        var refs = ProjectReferences("src/LinkPocket.I18n/LinkPocket.I18n.csproj");
        Assert.Equal(new[] { Contracts }.OrderBy(n => n), refs);
    }

    [Fact]
    public void I18n_禁止引用控件与引擎实现()
    {
        var refs = ProjectReferences("src/LinkPocket.I18n/LinkPocket.I18n.csproj");
        Assert.DoesNotContain(refs, r => ForbiddenForUi.Contains(r) || r == UIKit || r == Shell || r == Theming || UiPages.Contains(r));
    }

    [Theory]
    [MemberData(nameof(UiPageProjects))]
    public void UI页面项目_只依赖UIKit与Contracts与I18n(string project)
    {
        // 仍然"精确"：除这几个之外引到任何程序集都算越界（引擎实现、别的页面、Shell 都不许）。
        // I18n 可选而非必需——页面按批次收口，未收口的页面不该被强迫引用一个用不上的程序集。
        // Theming 仅「设置页」必需：外观面板（主题 / 调色台 / 字体 / 语言）就是 Theming 的编辑面，
        // 它按定义要读主题目录、求解器与偏好存储——其余页面仍不许引（颜色科学不该散进页面）。
        var refs = ProjectReferences($"src/{project}/{project}.csproj");
        var allowed = project == "LinkPocket.UI.Settings"
            ? new[] { UIKit, Contracts, I18n, Theming }
            : new[] { UIKit, Contracts, I18n };
        Assert.Contains(UIKit, refs);
        Assert.Contains(Contracts, refs);
        Assert.All(refs, r => Assert.Contains(r, allowed));
    }

    [Theory]
    [MemberData(nameof(UiPageProjects))]
    public void UI页面项目_禁止引用引擎实现与彼此(string project)
    {
        var refs = ProjectReferences($"src/{project}/{project}.csproj");
        Assert.DoesNotContain(refs, r => ForbiddenForUi.Contains(r) || r == Shell || UiPages.Contains(r));
    }

    [Fact]
    public void UIKit_禁止引用引擎实现()
    {
        var refs = ProjectReferences("src/LinkPocket.UIKit/LinkPocket.UIKit.csproj");
        Assert.DoesNotContain(refs, r => ForbiddenForUi.Contains(r));
    }

    /// <summary>
    /// 源码层红线：UI 层（UIKit / UI.* / Shell 的界面代码）**不得使用引擎实现与容器的命名空间**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么需要它：项目引用规则只能卡住"声明过的引用"，而 <c>ProjectReference</c> 是**传递**的 ——
    /// 页面只要引了 UIKit，就能 using <c>LinkPocket.Theming</c>，页面引了 Contracts 也能摸到更底层的类型；
    /// 历史上出现过"设置页直接用 Theming、csproj 里却没这一行"的漂移，csproj 检查全绿而实际依赖已经越界。
    /// 这条规则直接读源码，抓的就是那一类。
    /// </para>
    /// <para>
    /// 允许面（与 csproj 规则一致）：Shell（组合根）可以用 Engine 与 Composition —— <c>AppHost</c> 是
    /// 唯一允许 new 具体实现、并直持引擎 wire 的地方；容器（<c>Microsoft.Extensions.DependencyInjection</c>）
    /// 同理只许出现在组合根，页面与 UIKit 不许解析服务（否则就成了服务定位器）。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("LinkPocket.UIKit")]
    [InlineData("LinkPocket.UI.Browser")]
    [InlineData("LinkPocket.UI.Search")]
    [InlineData("LinkPocket.UI.Trash")]
    [InlineData("LinkPocket.UI.SmartLists")]
    [InlineData("LinkPocket.UI.Tools")]
    [InlineData("LinkPocket.UI.Settings")]
    [InlineData("LinkPocket.App")]
    public void UI层源码_不得使用引擎实现与容器(string project)
    {
        var isShell = project == Shell;
        string[] forbidden = isShell
            ? ["LinkPocket.Data", "LinkPocket.Kernel", "LinkPocket.Modules", "LinkPocket.Diagnostics"]
            : ["LinkPocket.Engine", "LinkPocket.Data", "LinkPocket.Kernel", "LinkPocket.Modules", "LinkPocket.Composition", "LinkPocket.Diagnostics"];

        var offenders = new List<string>();
        foreach (var (relative, full) in SourceFiles(project))
        {
            var text = StripLineComments(File.ReadAllText(full));

            foreach (var ns in forbidden)
            {
                // 两种真实写法都要抓：using 指令 与 全限定用法（含 LinkPocket.Composition.EngineComposer 那种）
                if (text.Contains($"using {ns}", StringComparison.Ordinal)
                    || text.Contains($"{ns}.", StringComparison.Ordinal))
                {
                    offenders.Add($"{relative} → {ns}");
                }
            }

            if (!isShell && text.Contains("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal))
                offenders.Add($"{relative} → Microsoft.Extensions.DependencyInjection");
        }

        Assert.True(offenders.Count == 0,
            "UI 层源码越界使用引擎实现/容器（依赖只能经 Contracts 端口或 Shell 组合根）："
            + string.Join("、", offenders));
    }

    /// <summary>去掉行注释（`//` 之后）——红线只判代码，注释里提到命名空间不算越界。</summary>
    private static string StripLineComments(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0) lines[i] = lines[i][..idx];
        }
        return string.Join('\n', lines);
    }

    /// <summary>遍历 src 下某项目的全部源文件（跳过 obj/bin）。</summary>
    private static IEnumerable<(string Relative, string Full)> SourceFiles(string project)
    {
        var root = Path.Combine(RepoRoot, "src", project);
        Assert.True(Directory.Exists(root), $"项目目录不存在: {project}");
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
            yield return (relative, file);
        }
    }

    [Fact]
    public void Shell_装配全部UI模块与引擎组合根()
    {
        // Shell（AppHost 组合根）是「组合根 new 具体实现」的唯一合法位置：它直引共享
        // Composition（引擎组合根）+ UIKit + Contracts + 全部 UI.*。引擎实现（Engine/Data/
        // Modules.*）已收敛进 Composition，Shell 不再直引（见 Shell_不直接引用引擎实现）；
        // UI 页面/UIKit 仍禁引引擎实现。
        var refs = ProjectReferences("src/LinkPocket.App/LinkPocket.csproj");
        Assert.Contains(Composition, refs);
        Assert.Contains(UIKit, refs);
        Assert.Contains(Contracts, refs);
        foreach (var page in UiPages)
            Assert.Contains(page, refs);
    }

    [Fact]
    public void Shell_不直接引用引擎实现()
    {
        // App 的引擎装配已全部收敛进 Composition：Shell 不得再直接引用 Engine/Data/Kernel/Modules.*。
        // 红线本质不动——引擎实现只经 Composition 一个入口到达 UI 层，杜绝绕过组合根。
        var refs = ProjectReferences("src/LinkPocket.App/LinkPocket.csproj");
        Assert.DoesNotContain(refs, r => ForbiddenForUi.Contains(r));
    }

    [Fact]
    public void Composition_精确引用契约内核数据引擎与九模块()
    {
        // Composition = 各界（App/冒烟/测试/工具）共用的引擎组合根共享项目：引用面精确 =
        // {Contracts, Kernel, Data, Engine, Diagnostics, 九模块}。不放任何 UI 项目（UI 禁引引擎实现的
        // 红线不随抽取松动）；少引一个模块或误引其它程序集都会让本断言红。
        // Diagnostics = 观测面实现（日志管道），只被组合根装配、不被 UI/引擎/模块引用（它们经 Contracts 的 LpLog 门面写日志）。
        var refs = ProjectReferences("src/LinkPocket.Composition/LinkPocket.Composition.csproj");
        var expected = new[] { Contracts, Kernel, Data, Engine, "LinkPocket.Diagnostics" }
            .Concat(BusinessModules).OrderBy(n => n).ToArray();
        Assert.Equal(expected, refs);
    }

    [Fact]
    public void 静态服务定位器_零残留()
    {
        // 灭绝的三件套静态定位器；架构测试防止还潮（任何源文件不得再出现这些类型名）。
        var banned = new[] { "class AppServices", "class UiCoordinator", "class BrowserLocateHost" };
        var srcDir = Path.Combine(RepoRoot, "src");
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var b in banned)
                Assert.True(!text.Contains(b), $"{file} 出现被禁止的静态定位器: {b}");
        }
    }
}
