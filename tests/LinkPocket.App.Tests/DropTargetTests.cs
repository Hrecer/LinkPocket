using System.Linq;
using System.Threading.Tasks;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 拖拽落点状态：悬停高亮 + 「移动到 X」提示的**唯一事实来源**。
/// 落点是覆盖式状态（每次 DragOver 重写，铁律 9），两栏共享一个落点但**只高亮指针真正所在的那一栏**
/// （同一实体可能两栏都有呈现：当前目录的子项同时也在树里）。
/// </summary>
public class DropTargetTests
{
    [Fact]
    public async Task 落点高亮_只落在指针所在那一栏()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            // 主栏落点：只有 A 行高亮，提示文案跟着走
            vm.SetDropTarget(new BrowserDropTarget(a.FolderId, BrowserPane.Main, "A"));
            Assert.True(vm.Rows.Single(r => r.Id == a.FolderId).IsDropTarget);
            Assert.False(vm.Rows.Single(r => r.Id == b.FolderId).IsDropTarget);
            Assert.Equal("移动到「A」", vm.DropTargetHintText);

            // 覆盖式：换落点，旧落点必须熄灭（不允许累积）
            vm.SetDropTarget(new BrowserDropTarget(b.FolderId, BrowserPane.Main, "B"));
            Assert.False(vm.Rows.Single(r => r.Id == a.FolderId).IsDropTarget);
            Assert.True(vm.Rows.Single(r => r.Id == b.FolderId).IsDropTarget);
            Assert.Equal("移动到「B」", vm.DropTargetHintText);

            // 同一 ID 但落点在树栏：主栏行不高亮，树节点高亮（高亮随指针所在栏走）
            var nodeA = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            vm.SetDropTarget(new BrowserDropTarget(a.FolderId, BrowserPane.Tree, "A"));
            Assert.False(vm.Rows.Single(r => r.Id == a.FolderId).IsDropTarget);
            Assert.True(nodeA.IsDropTarget);
            Assert.False(vm.FolderTree[0].IsDropTarget);              // 「全部书签」虚根永不作落点
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 落点清空_两栏高亮全灭且提示为空()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var nodeA = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            vm.SetDropTarget(new BrowserDropTarget(a.FolderId, BrowserPane.Tree, "A"));
            Assert.True(nodeA.IsDropTarget);

            vm.ClearDropTarget();                                     // 松手 / Esc / 拖出可落点

            Assert.False(nodeA.IsDropTarget);
            Assert.Empty(vm.DropTargetHintText);

            // 显式传 null（指针在空白或非法目标上）= 同样的全灭结果
            vm.SetDropTarget(new BrowserDropTarget(a.FolderId, BrowserPane.Main, "A"));
            vm.SetDropTarget(null);
            Assert.Empty(vm.DropTargetHintText);
            Assert.False(vm.Rows.Single(r => r.Id == a.FolderId).IsDropTarget);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>链接叶子（书签）不是落点：即便给它写落点，树侧投影也必须拒绝高亮。</summary>
    [Fact]
    public async Task 链接叶子永不高亮为落点()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var link = (await client.LinkCreateAsync("https://x.example", title: "X",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var leaf = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId)
                .Children.Single(c => c.Id == link.LinkId);
            vm.SetDropTarget(new BrowserDropTarget(link.LinkId, BrowserPane.Tree, "X"));

            Assert.False(leaf.IsDropTarget);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>
    /// 「落在根目录」（列表空白 = 根）**不是**「没有落点」：对象非 null 但 FolderId 为 null。
    /// 前者要显示「移动到「全部书签」」，后者要整条熄灭——两者混为一谈就会在根目录里丢掉落点提示。
    /// </summary>
    [Fact]
    public async Task 落在根目录_提示显示但不产生行高亮()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.FolderCreateAsync("A");
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            vm.SetDropTarget(new BrowserDropTarget(vm.CurrentFolderId, BrowserPane.Main, vm.CurrentFolderDisplayName));

            Assert.Equal("移动到「全部书签」", vm.DropTargetHintText);
            Assert.All(vm.Rows, r => Assert.False(r.IsDropTarget));   // 空白落点没有目标项 → 不高亮任何行

            vm.ClearDropTarget();
            Assert.Empty(vm.DropTargetHintText);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}
