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
    public async Task 目录树_全量链接叶子_文件夹在前链接在后各自名称升序_子目录链接注入其下()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            // 纯 ASCII 名称：CurrentCulture 升序结果确定；验证树与主区同口径（升序：文件夹组在前、链接组在后）
            var linkA = (await client.LinkCreateAsync("https://a-root.example", title: "Alpha 根级", autoFetchMetadata: false)).Data!;
            var linkZ = (await client.LinkCreateAsync("https://z-root.example", title: "Zulu 根级", autoFetchMetadata: false)).Data!;
            await client.FolderCreateAsync("Charlie 文件夹");
            var delta = (await client.FolderCreateAsync("Delta 文件夹")).Data!;
            var bravo = (await client.LinkCreateAsync("https://sub.example", title: "Bravo 子级",
                listId: delta.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var root = Assert.Single(vm.FolderTree);
            Assert.True(root.IsRoot);
            // 文件夹组在前（名称升序）、链接组在后（名称升序）——链接绝不骑在文件夹之前
            var children = root.Children;
            Assert.Equal(4, children.Count);
            Assert.Equal(new[] { "Charlie 文件夹", "Delta 文件夹", "Alpha 根级", "Zulu 根级" },
                children.Select(c => c.Name).ToArray());
            Assert.False(children[0].IsLink);
            Assert.False(children[1].IsLink);
            // 根级链接叶子：ParentId = null（所属目录 = 根）
            Assert.True(children[2].IsLink && children[2].Id == linkA.LinkId && children[2].ParentId == null);
            Assert.True(children[3].IsLink && children[3].Id == linkZ.LinkId && children[3].ParentId == null);

            // 子目录（Delta）的直接链接注入其节点下（名称升序叶子；ParentId = 所属目录）
            var deltaNode = children[1];
            Assert.Equal(delta.FolderId, deltaNode.FolderId);
            var deltaLeaf = Assert.Single(deltaNode.Children);
            Assert.True(deltaLeaf.IsLink && deltaLeaf.Id == bravo.LinkId && deltaLeaf.ParentId == delta.FolderId);
            Assert.Equal("Bravo 子级", deltaLeaf.Name);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 目录树_文件夹行单击_选中该文件夹并进入()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            await client.LinkCreateAsync("https://a.example/1", title: "One", listId: a.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var node = Assert.Single(vm.FolderTree[0].Children, c => !c.IsLink);
            Assert.Equal(a.FolderId, node.FolderId);

            await vm.SelectTreeNodeAsync(node);

            Assert.Equal(a.FolderId, vm.CurrentFolderId);                 // 已进入该目录
            Assert.True(vm.IsSelectedId(a.FolderId));                     // 该文件夹进入选中集合（树高亮唯一事实源）
            var nodeNow = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            Assert.True(nodeNow.IsSelected);                              // 树节点投影高亮（重建后仍成立）
            Assert.Equal("One", Assert.Single(vm.Rows).Name);             // 主栏显示该目录内容
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 目录树_链接叶子单击_定位到父目录并选中该行()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var link = (await client.LinkCreateAsync("https://a.example/1", title: "One",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var aNode = vm.FolderTree[0].Children.Single(c => !c.IsLink);
            var leaf = Assert.Single(aNode.Children);
            Assert.True(leaf.IsLink && leaf.Id == link.LinkId && leaf.ParentId == a.FolderId);

            await vm.SelectTreeNodeAsync(leaf);

            Assert.Equal(a.FolderId, vm.CurrentFolderId);     // 进入链接所在父目录
            Assert.True(vm.IsSelectedId(link.LinkId));        // 该链接进入选中集合（主栏 + 树叶子同时投影）
            Assert.Equal(link.LinkId, Assert.Single(vm.SelectedRows).Id);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 目录树_根节点单击_进入根目录且虚根永不选中()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            await client.LinkCreateAsync("https://a.example/1", title: "One", listId: a.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            // 已在根：单击「全部书签」不重载、不写选中（虚根不是实体）
            var root = Assert.Single(vm.FolderTree);
            Assert.True(root.IsRoot);
            await vm.SelectTreeNodeAsync(root);
            Assert.Null(vm.CurrentFolderId);
            Assert.False(root.IsSelected);
            Assert.False(vm.IsSelectedId(root.Id));      // 虚根无身份可选中（Id 为空，永不入集合）

            // 先进入子目录，再单击「全部书签」= 回根目录（导航；位置由面包屑表达，不产生选中高亮）
            var aNode = vm.FolderTree[0].Children.Single(c => !c.IsLink);
            await vm.SelectTreeNodeAsync(aNode);
            Assert.Equal(a.FolderId, vm.CurrentFolderId);

            var rootNow = Assert.Single(vm.FolderTree);
            await vm.SelectTreeNodeAsync(rootNow);
            Assert.Null(vm.CurrentFolderId);             // 已回到根目录
            Assert.False(rootNow.IsSelected);            // 虚根仍不显示选中
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}