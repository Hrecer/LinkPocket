using System.Xml.Linq;
using Xunit;

namespace LinkPocket.Architecture.Tests;

/// <summary>
/// 后端依赖层级规则（阶段 13 收尾补全，方案 2.2 依赖规则表）：把「依赖只能向下」逐层写成断言。
/// 与 <see cref="DependencyRulesTests"/>（UI 侧规则）互补：本文件管内核/数据/引擎分层与模块黑盒。
///
/// <para>判定基于 csproj 的 ProjectReference（引用图 = 编译期事实），不做文本猜测；
/// 规则一旦被违反，构建后测试即红。</para>
/// </summary>
public class LayerRulesTests
{
    private const string Contracts = "LinkPocket.Contracts";
    private const string Kernel = "LinkPocket.Kernel";
    private const string Data = "LinkPocket.Data";
    private const string Infrastructure = "LinkPocket.Infrastructure";
    private const string Engine = "LinkPocket.Engine";

    /// <summary>九个业务模块（方案第五章）。</summary>
    private static readonly string[] BusinessModules =
    {
        "LinkPocket.Modules.Folders", "LinkPocket.Modules.Links", "LinkPocket.Modules.Trash",
        "LinkPocket.Modules.Search", "LinkPocket.Modules.Bookmarks", "LinkPocket.Modules.Backup",
        "LinkPocket.Modules.Dedup", "LinkPocket.Modules.Favicon", "LinkPocket.Modules.Maintenance",
    };

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

    private static string CsprojPath(string project, string subdir = "src")
        => Path.Combine(RepoRoot, subdir, project, $"{project}.csproj");

    private static string[] ProjectReferences(string project, string subdir = "src")
    {
        var path = CsprojPath(project, subdir);
        Assert.True(File.Exists(path), $"项目文件不存在: {path}");
        return XDocument.Load(path)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('/', Path.DirectorySeparatorChar)))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] PackageReferences(string project)
        => XDocument.Load(CsprojPath(project))
            .Descendants("PackageReference")
            .Select(e => (string)e.Attribute("Include")!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private static void AssertRefs(string project, params string[] expected)
        => Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(), ProjectReferences(project));

    [Fact]
    public void Contracts_零项目引用零包依赖()
    {
        Assert.Empty(ProjectReferences(Contracts));
        Assert.Empty(PackageReferences(Contracts));
    }

    [Fact]
    public void Kernel_只依赖Contracts() => AssertRefs(Kernel, Contracts);

    [Fact]
    public void Data_只依赖Kernel() => AssertRefs(Data, Kernel);

    [Fact]
    public void Infrastructure_只依赖契约与数据层() => AssertRefs(Infrastructure, Contracts, Kernel, Data);

    [Fact]
    public void Engine_只依赖契约内核与数据层_不引用业务模块()
        => AssertRefs(Engine, Contracts, Kernel, Data);

    [Theory]
    [MemberData(nameof(Modules))]
    public void 业务模块只依赖Contracts与Kernel(string module) => AssertRefs(module, Contracts, Kernel);

    [Theory]
    [MemberData(nameof(Modules))]
    public void 业务模块之间零互相引用(string module)
        => Assert.DoesNotContain(ProjectReferences(module),
            r => BusinessModules.Contains(r) && r != module);

    [Theory]
    [InlineData(Kernel)]
    [InlineData(Data)]
    [InlineData(Infrastructure)]
    public void 下层不得向上引用(string project)
        => Assert.DoesNotContain(ProjectReferences(project),
            r => r == Engine || r.StartsWith("LinkPocket.Modules.", StringComparison.Ordinal));

    /// <summary>
    /// 模块是黑盒：除模块入口（<c>CreateHandlers</c>）外一律 internal，且**不得**为测试开后门。
    /// 测试侧一律经引擎黑盒驱动（Modules.Tests 就是这么做的），这条断言保证它不会被悄悄破坏。
    /// </summary>
    [Fact]
    public void InternalsVisibleTo_零残留()
    {
        var offenders = new List<string>();
        var src = Path.Combine(RepoRoot, "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;

            if (File.ReadAllText(file).Contains("InternalsVisibleTo", StringComparison.Ordinal))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "模块黑盒被开后门（InternalsVisibleTo）：" + string.Join("、", offenders));
    }

    public static IEnumerable<object[]> Modules() => BusinessModules.Select(m => new object[] { m });
}
