using System.Linq;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 浏览页 VM 金标准（P4）：可观测行为断言——行渲染形态（文件夹在前/名称升序）、
/// 导航与面包屑父链、跳转并选中。引擎用共享 Composition 全量装配（App 同口径），
/// 数据全部经引擎命令读写（黑盒），不开 internal 后门；渲染回归仍由 SmartProbe 承担。
/// </summary>
public class BrowserViewModelTests
{
    [Fact]
    public async Task 根目录加载_文件夹在前链接在后_各自按名称升序()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.FolderCreateAsync("B 文件夹");
            await client.FolderCreateAsync("A 文件夹");
            await client.LinkCreateAsync("https://z.example", title: "Z 链接", autoFetchMetadata: false);
            await client.LinkCreateAsync("https://a.example", title: "A 链接", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            // 升序口径（Windows）：文件夹在前（按名称）、链接在后（按名称）
            Assert.Equal(4, vm.Rows.Count);
            Assert.Equal(new[] { true, true, false, false }, vm.Rows.Select(r => r.IsFolder).ToArray());
            Assert.Equal(new[] { "A 文件夹", "B 文件夹" }, vm.Rows.Take(2).Select(r => r.Name).ToArray());
            Assert.Equal(new[] { "A 链接", "Z 链接" }, vm.Rows.Skip(2).Select(r => r.Name).ToArray());

            // 根目录面包屑 = 仅「全部书签」且为最后一级（可点击跳转）
            Assert.Single(vm.Breadcrumbs);
            Assert.Equal("全部书签", vm.Breadcrumbs[0].Name);
            Assert.True(vm.Breadcrumbs[0].IsLast);

            Assert.Equal("共 4 项（2 个文件夹 / 2 个链接）", vm.StatusText);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 进入子目录_面包屑与父链_返回根恢复()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var a1 = (await client.FolderCreateAsync("A1", parentId: a.FolderId)).Data!;
            await client.LinkCreateAsync("https://c.example", title: "C", listId: a.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            Assert.Null(vm.CurrentFolderId);
            Assert.Single(vm.Rows);                       // 根 = 只有 A

            await vm.LoadAsync(a.FolderId);
            Assert.Equal(a.FolderId, vm.CurrentFolderId);
            Assert.Equal(new[] { "全部书签", "A" }, vm.Breadcrumbs.Select(b => b.Name).ToArray());
            Assert.True(vm.Breadcrumbs[1].IsLast);        // 当前目录高亮（最后一级）
            Assert.Equal(2, vm.Rows.Count);               // A1（文件夹）+ C（链接）
            Assert.Equal("A1", vm.Rows[0].Name);
            Assert.True(vm.Rows[0].IsFolder);
            Assert.Equal("C", vm.Rows[1].Name);
            Assert.False(vm.Rows[1].IsFolder);

            // 完整路径展示（详情栏口径，含自身）："全部书签 / A / A1"
            Assert.Equal("全部书签 / A / A1", vm.GetFolderPathDisplay(a1.FolderId));

            await vm.LoadAsync(null);
            Assert.Null(vm.CurrentFolderId);
            Assert.Single(vm.Rows);                       // 回到根：又只剩 A
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 移动链接后_列表同步刷新_不依赖事件链()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var link = (await client.LinkCreateAsync("https://move.example", title: "M",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(a.FolderId);
            Assert.Contains(vm.Rows, r => r.Id == link.LinkId);

            // 移动后收尾显式刷新必须生效——修复前被 IsLoading 重入守卫吞掉，列表停在旧状态
            //（该场景无事件订阅，显式刷新是唯一路径，正好验证"最后请求必被处理"）。
            await vm.MoveItemsAsync(new[] { (link.LinkId, false) }, b.FolderId);

            Assert.DoesNotContain(vm.Rows, r => r.Id == link.LinkId);   // 已移出目录 A 的行（刷新已生效）
            Assert.Empty(vm.Rows);                             // A 目录此刻为空
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 跳转导航_进入目标目录并选中目标行_已在该目录不重载()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("F")).Data!;
            var link = (await client.LinkCreateAsync("https://one.example", title: "One",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            var navigated = await vm.NavigateAndSelectAsync(folder.FolderId, link.LinkId);

            Assert.True(navigated);
            Assert.Equal(folder.FolderId, vm.CurrentFolderId);
            var selected = Assert.Single(vm.SelectedRows);
            Assert.Equal(link.LinkId, selected.Id);
            Assert.False(selected.IsFolder);

            // 已在目标目录：再跳一次不会整目录重载（行对象保持不变 → 选中仍唯一）
            var again = await vm.NavigateAndSelectAsync(folder.FolderId, link.LinkId);
            Assert.True(again);
            Assert.Equal(link.LinkId, Assert.Single(vm.SelectedRows).Id);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 编辑模式打开_预填字段携带链接数据()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://edit.example/zh-cn/3",
                title: "编辑目标", description: "描述文本", autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            vm.OpenEditorForEdit(link.LinkId);

            // fire-and-forget 预填：轮询直到字段就位（带超时，等的是引擎异步取数 + 续体）
            var urlReady = await WaitUntilAsync(
                () => vm.EditorPage is { Url: "https://edit.example/zh-cn/3" }, TimeSpan.FromSeconds(3));
            Assert.True(urlReady, "预填未在超时内完成（编辑页字段空白根因复现点）");
            Assert.True(vm.IsEditorPageOpen);
            Assert.Equal("编辑链接", vm.EditorPage!.TitleText);
            Assert.Equal("编辑目标", vm.EditorPage.LinkTitle);
            Assert.Equal("描述文本", vm.EditorPage.Description);
            Assert.True(vm.EditorPage.IsEditMode);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task 目录树_根级链接作为叶子显示在全部书签下_文件夹之前()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var linkA = (await client.LinkCreateAsync("https://a-root.example", title: "A 根级链接", autoFetchMetadata: false)).Data!;
            var linkZ = (await client.LinkCreateAsync("https://z-root.example", title: "Z 根级链接", autoFetchMetadata: false)).Data!;
            await client.FolderCreateAsync("文件夹甲");
            await client.LinkCreateAsync("https://sub.example", title: "子级链接",
                listId: (await client.FolderCreateAsync("文件夹乙")).Data!.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var root = Assert.Single(vm.FolderTree);
            Assert.True(root.IsRoot);
            // 根级链接叶子：整体在文件夹之前，按标题升序；子目录内的链接不进树
            var children = root.Children;
            Assert.Equal(4, children.Count);
            Assert.True(children[0].IsLink && children[0].Id == linkA.LinkId);
            Assert.Equal("A 根级链接", children[0].Name);
            Assert.True(children[1].IsLink && children[1].Id == linkZ.LinkId);
            Assert.Equal("Z 根级链接", children[1].Name);
            // 文件夹节点在链接之后（甲、乙，名序）；「文件夹乙」内的 sub.example 链接不进树
            Assert.False(children[2].IsLink);
            Assert.Equal("文件夹甲", children[2].Name);
            Assert.False(children[3].IsLink);
            Assert.Equal("文件夹乙", children[3].Name);
            Assert.DoesNotContain(children, c => c.Name == "子级链接");
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}