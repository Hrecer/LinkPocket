using System.Xml.Linq;
using Xunit;

namespace LinkPocket.Architecture.Tests;

/// <summary>
/// 架构依赖规则测试（阶段 10 上线，规则 = 方案 2.2）：
/// - UI 任何项目禁止引用 Engine/Kernel/Data/Modules.* 实现程序集；
///   UIKit 只依赖 Contracts+Infrastructure；UI.* 页面项目只依赖 UIKit+Contracts+Infrastructure；
///   Shell(App) 装配全部 UI 项目；
/// - UI 项目之间零互相引用（模块可单独删除/演进）；
/// - 静态服务定位器零残留（AppServices/UiCoordinator 已于阶段 7 灭绝，防止还潮）。
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
    private const string Infrastructure = "LinkPocket.Infrastructure";
    private const string UIKit = "LinkPocket.UIKit";
    private const string Shell = "LinkPocket.App";
    private static readonly string[] UiPages =
    {
        "LinkPocket.UI.Browser", "LinkPocket.UI.Search", "LinkPocket.UI.Trash",
        "LinkPocket.UI.SmartLists", "LinkPocket.UI.Tools", "LinkPocket.UI.Settings",
    };
    private static readonly string[] ForbiddenForUi =
    {
        "LinkPocket.Engine", "LinkPocket.Kernel", "LinkPocket.Data",
        "LinkPocket.Modules.Folders", "LinkPocket.Modules.Links", "LinkPocket.Modules.Trash",
        "LinkPocket.Modules.Search", "LinkPocket.Modules.Bookmarks", "LinkPocket.Modules.Backup",
        "LinkPocket.Modules.Dedup", "LinkPocket.Modules.Favicon", "LinkPocket.Modules.Maintenance",
    };

    public static IEnumerable<object[]> UiPageProjects() => UiPages.Select(p => new object[] { p });

    [Fact]
    public void UIKit_只依赖Contracts与Infrastructure()
    {
        var refs = ProjectReferences("src/LinkPocket.UIKit/LinkPocket.UIKit.csproj");
        Assert.Equal(new[] { Contracts, Infrastructure }.OrderBy(n => n), refs);
    }

    [Theory]
    [MemberData(nameof(UiPageProjects))]
    public void UI页面项目_只依赖UIKitContracts与Infrastructure(string project)
    {
        var refs = ProjectReferences($"src/{project}/{project}.csproj");
        var allowed = new[] { UIKit, Contracts, Infrastructure }.OrderBy(n => n);
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
    public void Shell_装配全部UI模块且不引用引擎实现()
    {
        var refs = ProjectReferences("src/LinkPocket.App/LinkPocket.csproj");
        Assert.DoesNotContain(refs, r => ForbiddenForUi.Contains(r));
        Assert.Contains(UIKit, refs);
        Assert.Contains(Contracts, refs);
        Assert.Contains(Infrastructure, refs);
        foreach (var page in UiPages)
            Assert.Contains(page, refs);
    }

    [Fact]
    public void 静态服务定位器_零残留()
    {
        // 阶段 7 灭绝的三件套静态定位器；架构测试防止还潮（任何源文件不得再出现这些类型名）。
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
