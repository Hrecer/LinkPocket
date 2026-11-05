using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 搜索页活结果集：收到精确变更只动对应行；精确信息缺失时回落全量快照。
/// </summary>
public class SearchViewModelLiveUpdateTests
{
    [Fact]
    public async Task 活结果集_更新删除新增_只改对应行且保持选中()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await using (var db = new LinkPocket.Data.LinkPocketDbContext(dbPath))
            {
                for (var i = 0; i < 120; i++)
                    db.Links.Add(new LinkPocket.Data.Link
                    {
                        LinkId = $"LIVE{i:D10}",
                        Url = $"https://live.example/{i}",
                        Title = $"活更新 {i:D3}",
                    });
                await db.SaveChangesAsync();
            }

            var vm = new SearchViewModel(client, new FakeNavigationService(),
                new FakeDialogService(), _ => LocValue.Empty);
            vm.Query = "活更新";
            vm.SearchCommand.Execute(null);
            Assert.True(await WaitUntilAsync(
                    () => !vm.IsSearching && !vm.IsLoadingAll && vm.LoadedCount == 120,
                    TimeSpan.FromSeconds(30)),
                $"首屏未就绪（{vm.LoadedCount}/120）");
            vm.OnNavigatedTo();

            var replaced = 0;
            vm.ResultSetReplaced += (_, _) => replaced++;
            var targetId = "LIVE0000000050";
            var target = vm.Results.Single(r => r.LinkId == targetId);
            vm.ClickItem(target, System.Windows.Input.ModifierKeys.None);
            Assert.Equal(1, vm.SelectionCount);

            // 更新一条：行对象不变、选中不变、集合不重建。
            await client.LinkUpdateAsync(targetId, title: "活更新 050 改");
            vm.NotifyDataChanges(
                [new EntityRef("link", targetId)],
                [DomainEventNames.LinksChanged], precise: true);
            Assert.True(await WaitUntilAsync(
                    () => vm.Results.Any(r => r.LinkId == targetId && r.Title == "活更新 050 改"),
                    TimeSpan.FromSeconds(10)),
                "更新后的标题没有落到活结果集");
            Assert.Equal(120, vm.Results.Count);
            Assert.Equal(120, vm.TotalHitCount);
            Assert.Same(target, vm.Results.Single(r => r.LinkId == targetId));
            Assert.Equal(1, vm.SelectionCount);
            Assert.Equal(0, replaced);

            // 删除一条：只移除该行，总数同步。
            var removeId = "LIVE0000000070";
            await client.LinkTrashAsync(removeId);
            vm.NotifyDataChanges(
                [new EntityRef("link", removeId)],
                [DomainEventNames.LinksChanged], precise: true);
            Assert.True(await WaitUntilAsync(
                    () => vm.Results.Count == 119 && vm.Results.All(r => r.LinkId != removeId),
                    TimeSpan.FromSeconds(10)),
                "删除行没有从活结果集移除");
            Assert.Equal(119, vm.TotalHitCount);

            // 新增命中：按排序位置插回，不是追加到错误位置。
            var created = await client.LinkCreateAsync(
                "https://live.example/new", title: "活更新 999", autoFetchMetadata: false);
            var createdId = created.Data!.LinkId;
            vm.NotifyDataChanges(
                [new EntityRef("link", createdId)],
                [DomainEventNames.LinksChanged], precise: true);
            Assert.True(await WaitUntilAsync(
                    () => vm.Results.Count == 120 && vm.Results[^1].LinkId == createdId,
                    TimeSpan.FromSeconds(10)),
                "新增命中没有插到排序位置");
            Assert.Equal(0, replaced);

            // 精确信息缺失（外部直写探针口径）→ 必须回落全量快照。
            vm.NotifyDataChanges([], [], precise: false);
            Assert.True(await WaitUntilAsync(() => replaced > 0, TimeSpan.FromSeconds(10)),
                "Precise=false 没有触发全量回落");
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 活结果集_排序键变化_行按同一排序移动()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await using (var db = new LinkPocket.Data.LinkPocketDbContext(dbPath))
            {
                db.Links.AddRange(
                    new LinkPocket.Data.Link { LinkId = "SORTA", Url = "https://sort.example/a", Title = "活排序 A" },
                    new LinkPocket.Data.Link { LinkId = "SORTB", Url = "https://sort.example/b", Title = "活排序 B" },
                    new LinkPocket.Data.Link { LinkId = "SORTC", Url = "https://sort.example/c", Title = "活排序 C" });
                await db.SaveChangesAsync();
            }

            var vm = new SearchViewModel(client, new FakeNavigationService(),
                new FakeDialogService(), _ => LocValue.Empty);
            vm.Query = "活排序";
            vm.SearchCommand.Execute(null);
            Assert.True(await WaitUntilAsync(
                    () => !vm.IsSearching && !vm.IsLoadingAll && vm.LoadedCount == 3,
                    TimeSpan.FromSeconds(10)));
            vm.OnNavigatedTo();

            var replaced = 0;
            vm.ResultSetReplaced += (_, _) => replaced++;
            await client.LinkUpdateAsync("SORTA", title: "活排序 Z");
            vm.NotifyDataChanges(
                [new EntityRef("link", "SORTA")],
                [DomainEventNames.LinksChanged], precise: true);

            Assert.True(await WaitUntilAsync(
                    () => vm.Results.Select(r => r.Title).SequenceEqual(["活排序 B", "活排序 C", "活排序 Z"]),
                    TimeSpan.FromSeconds(10)),
                "排序键变化后行没有移动");
            Assert.Equal(3, vm.Results.Count);
            Assert.Equal(0, replaced);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 活结果集_隐藏期间深层改名_进入时只更新该行()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await using (var db = new LinkPocket.Data.LinkPocketDbContext(dbPath))
            {
                for (var i = 0; i < 120; i++)
                    db.Links.Add(new LinkPocket.Data.Link
                    {
                        LinkId = $"HIDE{i:D10}",
                        Url = $"https://hide.example/{i}",
                        Title = $"隐藏更新 {i:D3}",
                    });
                await db.SaveChangesAsync();
            }

            var vm = new SearchViewModel(client, new FakeNavigationService(),
                new FakeDialogService(), _ => LocValue.Empty);
            vm.Query = "隐藏更新";
            vm.SearchCommand.Execute(null);
            Assert.True(await WaitUntilAsync(
                    () => !vm.IsSearching && !vm.IsLoadingAll && vm.LoadedCount == 120,
                    TimeSpan.FromSeconds(30)));
            vm.OnNavigatedTo();

            // 第 100 行远在首屏预取（64 行）之外——正是旧入口等价判定会漏掉的位置。
            var target = vm.Results[100];
            var targetId = target.LinkId;
            var original = target.Title;
            vm.OnNavigatedFrom();

            await client.LinkUpdateAsync(targetId, title: "隐藏更新 100 改");
            vm.NotifyDataChanges(
                [new EntityRef("link", targetId)],
                [DomainEventNames.LinksChanged], precise: true);
            await Task.Delay(120);
            Assert.Equal(original, target.Title);   // 隐藏期间只排队，不动结果集

            var replaced = 0;
            vm.ResultSetReplaced += (_, _) => replaced++;
            vm.OnNavigatedTo();
            Assert.True(await WaitUntilAsync(
                    () => target.Title == "隐藏更新 100 改",
                    TimeSpan.FromSeconds(10)),
                "进入搜索页时深层改名没有追平");
            Assert.Same(target, vm.Results.Single(r => r.LinkId == targetId));
            Assert.Equal(120, vm.Results.Count);
            Assert.Equal(0, replaced);

            // 没有新变更：再次进入不应触发任何全量重建。
            vm.OnNavigatedFrom();
            vm.OnNavigatedTo();
            await Task.Delay(200);
            Assert.Equal(0, replaced);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 活结果集_随机增删改_与全量搜索逐项一致()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var known = new List<string>();
            await using (var db = new LinkPocket.Data.LinkPocketDbContext(dbPath))
            {
                for (var i = 0; i < 30; i++)
                {
                    var id = $"RAND{i:D10}";
                    known.Add(id);
                    db.Links.Add(new LinkPocket.Data.Link
                    {
                        LinkId = id,
                        Url = $"https://random.example/{i}",
                        Title = $"对拍 {i:D3}",
                        CreatedAt = DateTime.UtcNow.AddMinutes(i),
                        UpdatedAt = DateTime.UtcNow.AddMinutes(i),
                    });
                }
                await db.SaveChangesAsync();
            }

            var vm = new SearchViewModel(client, new FakeNavigationService(),
                new FakeDialogService(), _ => LocValue.Empty);
            vm.Query = "对拍";
            vm.SearchCommand.Execute(null);
            Assert.True(await WaitUntilAsync(
                    () => !vm.IsSearching && !vm.IsLoadingAll && vm.LoadedCount == 30,
                    TimeSpan.FromSeconds(20)));
            vm.OnNavigatedTo();

            var rnd = new Random(20261105);
            for (var round = 0; round < 30; round++)
            {
                var op = rnd.Next(4);
                string? affected = null;
                if (op == 0 && known.Count > 0)
                {
                    affected = known[rnd.Next(known.Count)];
                    await client.LinkUpdateAsync(affected, title: $"对拍 改{round:D3}");
                }
                else if (op == 1 && known.Count > 0)
                {
                    var index = rnd.Next(known.Count);
                    affected = known[index];
                    known.RemoveAt(index);
                    await client.LinkTrashAsync(affected);
                }
                else if (op == 2)
                {
                    var created = await client.LinkCreateAsync(
                        $"https://random.example/new/{round}", title: $"对拍 新增{round:D3}",
                        autoFetchMetadata: false);
                    affected = created.Data!.LinkId;
                    known.Add(affected);
                }
                else if (known.Count > 0)
                {
                    affected = known[rnd.Next(known.Count)];
                    await client.LinkUpdateAsync(affected, title: $"不匹配 {round:D3}");
                }

                // 真实链路里是 ChangeSet.Touched；这里直接投递同一形状，避免依赖 UiEventHub 的 300ms 计时。
                if (affected is not null)
                    vm.NotifyDataChanges(
                        [new EntityRef("link", affected)],
                        [DomainEventNames.LinksChanged], precise: true);

                // 关键：不是“等固定时间”，而是等活结果集真的排空。
                Assert.True(await WaitUntilAsync(() => !vm.HasPendingLiveChanges, TimeSpan.FromSeconds(5)),
                    $"第 {round} 轮活结果集没有排空");

                // 每轮都以“全量搜索”为唯一事实源对拍。
                var engine = await client.SearchLinksAsync("对拍",
                    searchTitle: true, searchUrl: true, searchDescription: true, searchPath: true,
                    sortBy: "title", sortOrder: "asc", perPage: 0);
                Assert.Equal(engine.Select(l => l.LinkId), vm.Results.Select(r => r.LinkId));
                Assert.Equal(engine.Select(l => l.Title), vm.Results.Select(r => r.Title));
                Assert.Equal(engine.Count, vm.TotalHitCount);
            }
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 活结果集_文件夹变更_回落全量并更新文件夹行()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = await client.FolderCreateAsync("活文件夹");
            await client.LinkCreateAsync("https://live-folder.example", title: "路径命中链接",
                listId: folder.Data!.FolderId, autoFetchMetadata: false);

            var vm = new SearchViewModel(client, new FakeNavigationService(),
                new FakeDialogService(), _ => LocValue.Empty);
            vm.Query = "活文件夹";
            vm.SearchCommand.Execute(null);
            Assert.True(await WaitUntilAsync(
                    () => !vm.IsSearching && !vm.IsLoadingAll && vm.LoadedCount == 2,
                    TimeSpan.FromSeconds(15)),
                $"文件夹搜索结果未就绪（{vm.LoadedCount}/2）");
            vm.OnNavigatedTo();

            var replaced = 0;
            vm.ResultSetReplaced += (_, _) => replaced++;
            await client.FolderUpdateAsync(folder.Data.FolderId, name: "活文件夹改");
            vm.NotifyDataChanges(
                [new EntityRef("folder", folder.Data.FolderId)],
                [DomainEventNames.FoldersChanged], precise: true);

            Assert.True(await WaitUntilAsync(
                    () => replaced > 0 && vm.Results.Any(r => r.IsFolder && r.Title == "活文件夹改"),
                    TimeSpan.FromSeconds(15)),
                "文件夹变更没有回落全量并更新文件夹行");
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
            await Task.Delay(25);
        }
        return condition();
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool Confirm(string title, string message, string? confirmText = null,
            string iconKind = "delete-outline") => true;
        public bool ConfirmDeleteFolder(string folderName) => true;
        public void Alert(string title, string message) { }
    }

    private sealed class FakeNavigationService : INavigationService
    {
        public void OpenLinkInBrowser(string linkId) { }
        public void OpenFolderInBrowser(string folderId) { }
        public void NavigateToSearch() { }
        public Task RefreshTrashPageAsync() => Task.CompletedTask;
    }
}
