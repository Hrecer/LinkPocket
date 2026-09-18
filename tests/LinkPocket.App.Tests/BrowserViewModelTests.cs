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
}