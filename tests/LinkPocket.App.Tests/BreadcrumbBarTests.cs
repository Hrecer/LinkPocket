using System.Linq;
using System.Threading.Tasks;
using LinkPocket.ViewModels;
using LinkPocket.Views;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 面包屑地址栏（BreadcrumbBar）：
/// ① 横向滚动的纯几何：窗口范围、拇指长度/位置、边缘速度——直测纯函数；
/// ② 面包屑作为第三落点区（Pane=Breadcrumb）的投影隔离——行与树不因面包屑落点而高亮。
/// </summary>
public class BreadcrumbBarTests
{
    // —— ComputeWindow：只物化可见 + 缓冲，绝不把整条深路径塞进 ItemsControl ——

    [Fact]
    public void 放得下_全量窗口()
    {
        var (start, count, inside) = BreadcrumbBar.ComputeWindow(new[] { 80.0, 60.0, 60.0 }, 0, 300, 2);
        Assert.Equal(0, start);
        Assert.Equal(3, count);
        Assert.Equal(0, inside, 3);
    }

    [Fact]
    public void 中间偏移_窗口从可见段开始()
    {
        var (start, count, inside) = BreadcrumbBar.ComputeWindow(new[] { 100.0, 100.0, 100.0, 100.0 }, 100, 250, 0);
        Assert.Equal(1, start);
        Assert.Equal(3, count);
        Assert.Equal(0, inside, 3);
    }

    [Fact]
    public void 缓冲只扩可见窗口_不扩到全路径()
    {
        // 100 段各 100px，视口 100px，偏移 5000px（第 50 段）：可见 1–2 段，缓冲后 ≤5 段。
        var widths = Enumerable.Repeat(100.0, 100).ToArray();
        var (start, count, inside) = BreadcrumbBar.ComputeWindow(widths, 5000, 100, 2);
        Assert.InRange(count, 1, 5);
        Assert.InRange(start, 47, 50);
        Assert.InRange(inside, 0, 2 * 100.0);   // 左缓冲可能把窗口顶回 2 段
    }

    [Fact]
    public void 偏移越界_夹在端点()
    {
        var widths = new[] { 100.0, 100.0, 100.0 };
        var atEnd = BreadcrumbBar.ComputeWindow(widths, 99999, 100, 0);
        var atStart = BreadcrumbBar.ComputeWindow(widths, -50, 100, 0);
        Assert.Equal(2, atEnd.Start);
        Assert.Equal(0, atStart.Start);
    }

    [Fact]
    public void 空段集合_空窗口()
    {
        Assert.Equal((0, 0, 0d), BreadcrumbBar.ComputeWindow(System.Array.Empty<double>(), 0, 300, 2));
    }

    // —— ComputeThumb：3px 视觉线，最小 36px，长度只随比例变化 ——

    [Fact]
    public void 拇指按比例_且有最小长度()
    {
        var half = BreadcrumbBar.ComputeThumb(250, 500, 1000, 200);
        Assert.Equal(100, half.Width, 3);
        Assert.Equal(50, half.Left, 3);

        var deep = BreadcrumbBar.ComputeThumb(0, 500, 100_000, 200);
        Assert.Equal(36, deep.Width, 3);           // 100 层时仍可抓
        Assert.Equal(0, deep.Left, 3);
    }

    [Fact]
    public void 拇指到端点_贴住轨道两端()
    {
        var end = BreadcrumbBar.ComputeThumb(500, 500, 1000, 200);
        Assert.Equal(100, end.Width, 3);
        Assert.Equal(100, end.Left, 3);

        var all = BreadcrumbBar.ComputeThumb(0, 1000, 1000, 200);
        Assert.Equal(200, all.Width, 3);
        Assert.Equal(0, all.Left, 3);
    }

    [Fact]
    public void 拇指轨道为零_返回零几何()
    {
        Assert.Equal((0, 0), BreadcrumbBar.ComputeThumb(0, 500, 1000, 0));
    }

    // —— EdgeScrollSpeed：越靠边越快 ——

    [Fact]
    public void 边缘速度_从慢到快_并夹住上限()
    {
        Assert.Equal(220, BreadcrumbBar.EdgeScrollSpeed(0, 28, 900), 3);
        Assert.Equal(900, BreadcrumbBar.EdgeScrollSpeed(28, 28, 900), 3);
        Assert.Equal(900, BreadcrumbBar.EdgeScrollSpeed(99, 28, 900), 3);
        Assert.Equal(220, BreadcrumbBar.EdgeScrollSpeed(-10, 28, 900), 3);
    }

    // —— SamePath：刷新会重建 VM 实例；路径没变就不许重排/闪省略号 ——

    [Fact]
    public void 路径等值_新实例也算同一条路径()
    {
        var a = new object[] { new BrowserCrumbViewModel(null, "@root"), new BrowserCrumbViewModel("f1", "甲") { IsLast = true } };
        var b = new object[] { new BrowserCrumbViewModel(null, "@root"), new BrowserCrumbViewModel("f1", "甲") { IsLast = true } };
        Assert.True(BreadcrumbBar.SamePath(a, b));
    }

    [Fact]
    public void 路径改名_不是同一条路径()
    {
        var a = new object[] { new BrowserCrumbViewModel(null, "@root"), new BrowserCrumbViewModel("f1", "甲") { IsLast = true } };
        var b = new object[] { new BrowserCrumbViewModel(null, "@root"), new BrowserCrumbViewModel("f1", "乙") { IsLast = true } };
        Assert.False(BreadcrumbBar.SamePath(a, b));
    }

    [Fact]
    public void 同名不同文件夹_不是同一条路径()
    {
        var a = new object[] { new BrowserCrumbViewModel(null, "@root"), new BrowserCrumbViewModel("f1", "同名") { IsLast = true } };
        var b = new object[] { new BrowserCrumbViewModel(null, "@root"), new BrowserCrumbViewModel("f2", "同名") { IsLast = true } };
        Assert.False(BreadcrumbBar.SamePath(a, b));
    }

    [Fact]
    public void 层级数变化或空集合_不是同一条路径()
    {
        var a = new object[] { new BrowserCrumbViewModel(null, "@root") };
        var b = new object[] { new BrowserCrumbViewModel(null, "@root"), new BrowserCrumbViewModel("f1", "甲") { IsLast = true } };
        Assert.False(BreadcrumbBar.SamePath(a, b));
        Assert.False(BreadcrumbBar.SamePath(System.Array.Empty<object>(), System.Array.Empty<object>()));
        Assert.False(BreadcrumbBar.SamePath(System.Array.Empty<object>(), a));
    }

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
