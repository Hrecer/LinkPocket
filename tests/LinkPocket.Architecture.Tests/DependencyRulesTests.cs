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
    };
    private static readonly string[] ForbiddenForUi =
    {
        Engine, Kernel, Data,
        "LinkPocket.Modules.Folders", "LinkPocket.Modules.Links", "LinkPocket.Modules.Trash",
        "LinkPocket.Modules.Search", "LinkPocket.Modules.Bookmarks", "LinkPocket.Modules.Backup",
        "LinkPocket.Modules.Dedup", "LinkPocket.Modules.Favicon", "LinkPocket.Modules.Maintenance",
    };

    public static IEnumerable<object[]> UiPageProjects() => UiPages.Select(p => new object[] { p });

    [Fact]
    public void UIKit_只依赖Contracts()
    {
        var refs = ProjectReferences("src/LinkPocket.UIKit/LinkPocket.UIKit.csproj");
        Assert.Equal(new[] { Contracts }.OrderBy(n => n), refs);
    }

    [Theory]
    [MemberData(nameof(UiPageProjects))]
    public void UI页面项目_只依赖UIKit与Contracts(string project)
    {
        var refs = ProjectReferences($"src/{project}/{project}.csproj");
        var allowed = new[] { UIKit, Contracts }.OrderBy(n => n);
        Assert.Equal(allowed, refs);
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
        // {Contracts, Kernel, Data, Engine, 九模块}。不放任何 UI 项目（UI 禁引引擎实现的
        // 红线不随抽取松动）；少引一个模块或误引其它程序集都会让本断言红。
        var refs = ProjectReferences("src/LinkPocket.Composition/LinkPocket.Composition.csproj");
        var expected = new[] { Contracts, Kernel, Data, Engine }.Concat(BusinessModules).OrderBy(n => n).ToArray();
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
