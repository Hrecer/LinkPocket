using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 目录树作为**拖拽源**：载荷已抽象为轻量 <see cref="DragItem"/>（不再绑死行 VM），
/// 因此树节点与主栏行走**同一条**移动路径；选中语义与主栏完全一致——拖未选中节点先单选该节点、
/// 拖已选中节点拖动整个选中集合（树选中同样落在唯一选中集合 <c>_selectedIds</c> 里）。
///
/// <para>真实手势（MouseMove 阈值 → 面板转发 → 宿主 DoDragDrop）在视图层，由 渲染检查 渲染检查承担；
/// 这里锁 VM 侧语义与载荷构造（VM 单测覆盖不到的只有"事件接线"，那是渲染检查的活）。</para>
/// </summary>
public class TreeDragSourceTests
{
    [Fact]
    public async Task 树节点拖拽_未选中节点_先单选该节点并产出单项载荷()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var node = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            Assert.False(vm.IsSelectedId(a.FolderId));

            var items = vm.PrepareDragFromNode(node);

            var item = Assert.Single(items);
            Assert.Equal(a.FolderId, item.Id);
            Assert.True(item.IsFolder);
            Assert.Equal("A", item.Name);
            Assert.True(vm.IsSelectedId(a.FolderId));   // 拖未选中项 = 先选中该项（Explorer 口径）
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 树节点拖拽_已选中节点_拖动整个选中集合()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            await client.FolderCreateAsync("B");
            var link = (await client.LinkCreateAsync("https://x.example", title: "X",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var node = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            await vm.SelectTreeNodeAsync(node);                                   // 树选中 A（并进入 A）

            var row = vm.Rows.Single(r => r.Id == link.LinkId);
            vm.SelectRowWithModifiers(row, ModifierKeys.Control);                 // Ctrl 加选 → 集合 = {A, X}

            var items = vm.PrepareDragFromNode(node);

            Assert.Equal(2, items.Count);
            Assert.Contains(items, i => i.Id == a.FolderId && i.IsFolder);
            Assert.Contains(items, i => i.Id == link.LinkId && !i.IsFolder && i.Name == "X");
            Assert.True(vm.IsSelectedId(a.FolderId));                             // 已选中节点不改变选中集合
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 树节点拖拽_虚根不是实体不可拖_链接叶子可拖()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var link = (await client.LinkCreateAsync("https://x.example", title: "X",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            // 「全部书签」虚根：不是实体、没有可移动的身份 → 空载荷（界面据此不发起拖拽）
            var root = Assert.Single(vm.FolderTree);
            Assert.True(root.IsRoot);
            Assert.Empty(vm.PrepareDragFromNode(root));

            // 链接叶子也是拖拽源（载荷 IsFolder=false；落点仍是文件夹）
            var leaf = vm.FolderTree[0].Children
                .Single(c => c.FolderId == a.FolderId).Children.Single(c => c.Id == link.LinkId);
            var item = Assert.Single(vm.PrepareDragFromNode(leaf));
            Assert.Equal(link.LinkId, item.Id);
            Assert.False(item.IsFolder);
            Assert.Equal("X", item.Name);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>树节点拖到文件夹（主栏行 / 树节点是同一入口 <c>MoveItemsAsync</c>）：搬走且目标层可见。</summary>
    [Fact]
    public async Task 树拖拽载荷_移动到目标文件夹_与主栏走同一条路径()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var sub = (await client.FolderCreateAsync("Sub", parentId: a.FolderId)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var subNode = vm.FolderTree[0].Children
                .Single(c => c.FolderId == a.FolderId).Children.Single(c => c.FolderId == sub.FolderId);
            var items = vm.PrepareDragFromNode(subNode);

            await vm.DropItemsAsync(items, b.FolderId, TransferMode.Move);
            await vm.RefreshPreservingSelectionAsync();

            var bNode = vm.FolderTree[0].Children.Single(c => c.FolderId == b.FolderId);
            Assert.Contains(bNode.Children, c => c.FolderId == sub.FolderId);
            var aNode = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            Assert.DoesNotContain(aNode.Children, c => c.FolderId == sub.FolderId);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}
