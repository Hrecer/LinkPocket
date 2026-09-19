using System.Linq;
using System.Threading.Tasks;
using LinkPocket.ViewModels;
using LinkPocket.Views;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 面包屑地址栏（BreadcrumbBar）：
/// ① 溢出折叠装箱算法（Windows 11 口径：从左折叠、当前段永远可见、« 占宽）——纯函数直测；
/// ② 面包屑作为第三落点区（Pane=Breadcrumb）的投影隔离——行与树不因面包屑落点而高亮。
/// </summary>
public class BreadcrumbBarTests
{
    // —— ComputeFoldCount：从左折叠、保底保留当前段 ——

    [Fact]
    public void 放得下_不折叠()
    {
        // 三段共 200px，可用 300px → 全显
        Assert.Equal(0, BreadcrumbBar.ComputeFoldCount(new[] { 80.0, 60.0, 60.0 }, 300, 0));
    }

    [Fact]
    public void 放不下_从左折叠_保住右侧()
    {
        // 段宽 [100,100,100,100]（共 400），可用 250：从右累加 100+100=200 ≤ 250，+100=300 > 250
        // → 保留后 2 段，折叠前 2 段
        Assert.Equal(2, BreadcrumbBar.ComputeFoldCount(new[] { 100.0, 100.0, 100.0, 100.0 }, 250, 0));
    }

    [Fact]
    public void 预留折叠按钮占宽_折叠数相应变多()
    {
        // 同样四段：可用 250，但 « 要占 60 → 预算 190 → 只保得住最后 1 段，折叠前 3 段
        Assert.Equal(3, BreadcrumbBar.ComputeFoldCount(new[] { 100.0, 100.0, 100.0, 100.0 }, 250, 60));
    }

    [Fact]
    public void 当前段自己超宽_至少保留它()
    {
        // 唯一段就超宽：保住它（折叠数最多 = count-1 = 0）
        Assert.Equal(0, BreadcrumbBar.ComputeFoldCount(new[] { 500.0 }, 200, 44));
    }

    [Fact]
    public void 全都放不下_折叠到只剩当前段()
    {
        // 五段可用仅够一段：折叠 4 段，保住最后一段（当前段永可见）
        Assert.Equal(4, BreadcrumbBar.ComputeFoldCount(new[] { 90.0, 90.0, 90.0, 90.0, 90.0 }, 100, 0));
    }

    [Fact]
    public void 空段集合_不折叠()
        => Assert.Equal(0, BreadcrumbBar.ComputeFoldCount(System.Array.Empty<double>(), 300, 44));

    // —— 面包屑作为第三落点区：投影按栏隔离 ——

    /// <summary>落点写在面包屑栏时，主栏行与树节点都必须保持熄灭（高亮只落在指针真正所在的那一区）。</summary>
    [Fact]
    public async Task 面包屑落点_行与树都不高亮()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            vm.SetDropTarget(new BrowserDropTarget(b.FolderId, BrowserPane.Breadcrumb, "B"));

            Assert.All(vm.Rows, r => Assert.False(r.IsDropTarget));
            Assert.All(vm.FolderTree.SelectMany(Flatten), n => Assert.False(n.IsDropTarget));

            // 清空后同样全灭（拖拽收尾统一走这条）
            vm.ClearDropTarget();
            Assert.All(vm.Rows, r => Assert.False(r.IsDropTarget));
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    private static System.Collections.Generic.IEnumerable<FolderNode> Flatten(FolderNode n)
    {
        yield return n;
        foreach (var c in n.Children)
            foreach (var d in Flatten(c))
                yield return d;
    }
}
