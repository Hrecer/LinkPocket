using System.Diagnostics;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 目录树按需重取（folders.overview 的 include_tree=false 的配套纪律）：
/// 导航不再搬整棵树，但**目录变更事件**必须把它重新置脏——新建 / 改名 / 删除都要在树里可见。
/// 这是"复用旧树"最容易退化成"树永远停在启动快照"的地方。
/// </summary>
public class BrowserTreeRefreshTests
{
    [Fact]
    public Task 目录变更事件后_新建改名删除都反映在左栏树里() => StaPump.RunAsync(async () =>
    {
        var (client, engine, dbPath) = AppTestEnv.Create();
        try
        {
            var hub = new UiEventHub();
            hub.Attach(engine.Events);
            var vm = new MainViewModel(client, hub, new UiPortProvider());

            await vm.BrowserViewModel.LoadAsync(null);
            Assert.Null(Find(vm.BrowserViewModel.FolderTree, "nope"));

            var created = (await client.FolderCreateAsync("树刷新测试")).Data!;
            await WaitUntil(() => Find(vm.BrowserViewModel.FolderTree, created.FolderId) is not null);
            Assert.Equal("树刷新测试", Find(vm.BrowserViewModel.FolderTree, created.FolderId)!.Name);

            await client.FolderUpdateAsync(created.FolderId, name: "树刷新测试·改名");
            await WaitUntil(() => Find(vm.BrowserViewModel.FolderTree, created.FolderId)?.Name == "树刷新测试·改名");

            await client.FolderDeleteAsync(created.FolderId, cascade: "delete_all");
            await WaitUntil(() => Find(vm.BrowserViewModel.FolderTree, created.FolderId) is null);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    });

    private static FolderNode? Find(IEnumerable<FolderNode> nodes, string folderId)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.FolderId, folderId, StringComparison.Ordinal)) return node;
            if (Find(node.Children, folderId) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>泵真实 Dispatcher 计时器（300ms 防抖 + 异步刷新链），等到条件成立或超时。</summary>
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(25);
        Assert.True(condition(), $"condition not met within {timeoutMs}ms");
    }
}
