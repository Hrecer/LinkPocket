using System.Linq;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 目录树叶子的**内容版本**回归网（2026-09-26）：
/// 树刷新时"已加载叶子原样搬运"是性能机制（不重查同一目录的叶子），但**目录内容变过**后
/// 再搬运旧叶子就是让界面停在旧数据上（实测：回溯把链接挪回去后，已展开节点下的叶子还是旧的）。
/// 口径：目录 UpdatedAt（内核沿父链维护，查看不算变动）一致才复用旧叶子，不一致就作废重取。
/// </summary>
public class BrowserTreeLeafRefreshTests
{
    [Fact]
    public async Task 树叶子_内容没变的刷新复用旧叶子_目录改动后刷新重取()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var link = (await client.LinkCreateAsync("https://leaf.example", title: "叶子一",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var nodeA = vm.FolderTree[0].Children.Single(n => n.FolderId == a.FolderId);
            nodeA.IsExpanded = true;
            await vm.WaitForTreeLinksAsync(nodeA);
            var loadedLeaf = Assert.Single(nodeA.Children.Where(n => n.IsLink).ToList());
            Assert.Equal(link.LinkId, loadedLeaf.Id);

            // ① 内容没变的刷新：叶子照旧**原样搬运**（同一批对象 = 没有多余重取；这条性能机制不许被回退）
            await vm.RefreshPreservingSelectionAsync();
            var nodeA2 = vm.FolderTree[0].Children.Single(n => n.FolderId == a.FolderId);
            Assert.Contains(nodeA2.Children, n => n.IsLink && ReferenceEquals(n, loadedLeaf));

            // ② 目录内容变了（链接被移走——回溯/批量移动都走这条，UpdatedAt 沿父链刷新）：
            //    旧叶子**不许再搬运**（否则展开着的那棵树下永远显示旧数据），重取后 A 下为空。
            var moved = await client.LinkMoveBatchAsync([link.LinkId], b.FolderId);
            Assert.True(moved.Ok);
            await vm.RefreshPreservingSelectionAsync();

            var nodeA3 = vm.FolderTree[0].Children.Single(n => n.FolderId == a.FolderId);
            Assert.DoesNotContain(nodeA3.Children, n => n.IsLink && n.Id == link.LinkId);
            await vm.WaitForTreeLinksAsync(nodeA3);
            Assert.DoesNotContain(nodeA3.Children, n => n.IsLink);   // 重取后如实为空
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}
