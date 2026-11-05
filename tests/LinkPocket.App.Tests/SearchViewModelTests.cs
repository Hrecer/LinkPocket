using System;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.I18n;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 搜索页的“全部可达”回归：结果集从首帧起就是真实总数（未到的位置是同高占位行），
/// 后台原位填充剩余命中；不存在 500 条首屏上限或“仅显示前 N 条”的截断。
/// </summary>
public class SearchViewModelTests
{
    [Fact]
    public async Task 宽命中_首屏之后后台补全到全部命中()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await using (var db = new LinkPocket.Data.LinkPocketDbContext(dbPath))
            {
                for (var i = 0; i < 501; i++)
                    db.Links.Add(new LinkPocket.Data.Link
                    {
                        LinkId = $"UI{i:D14}",
                        Url = $"https://ui-search.example/{i}",
                        Title = $"界面搜索 {i:D3}",
                    });
                await db.SaveChangesAsync();
            }

            var vm = new SearchViewModel(
                client,
                new FakeNavigationService(),
                new FakeDialogService(),
                _ => LocValue.Empty);
            vm.Query = "界面搜索";
            vm.SearchCommand.Execute(null);

            Assert.True(await WaitUntilAsync(() => !vm.IsSearching && vm.TotalHitCount > 0, TimeSpan.FromSeconds(30)),
                "搜索首屏没有在超时内完成");
            // 首帧：结果集行数已经等于真实总数（不是首屏批次），未加载行是占位。
            Assert.Equal(501, vm.TotalHitCount);
            Assert.Equal(501, vm.Results.Count);
            Assert.True(vm.LoadedCount > 0 && vm.LoadedCount <= 501);

            // 占位行不可选；真实行可选（首屏已填的前 64 行）。
            var placeholder = vm.Results.FirstOrDefault(r => string.IsNullOrEmpty(r.LinkId));
            if (placeholder is not null)
            {
                vm.ClickItem(placeholder, ModifierKeys.None);
                Assert.Equal(0, vm.SelectionCount);
            }
            var real = vm.Results.FirstOrDefault(r => !string.IsNullOrEmpty(r.LinkId));
            Assert.NotNull(real);
            vm.ClickItem(real!, ModifierKeys.None);
            Assert.Equal(1, vm.SelectionCount);
            vm.Selection.Clear();

            Assert.True(await WaitUntilAsync(
                    () => !vm.IsLoadingAll && vm.LoadedCount == 501,
                    TimeSpan.FromSeconds(30)),
                $"后台补全没有在超时内完成（当前 {vm.LoadedCount}/501）");
            Assert.Equal(501, vm.Results.Count);
            Assert.Equal(501, vm.LoadedCount);
            Assert.Equal(LocText.Empty, vm.LoadingNotice);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 空查询_结果集为空且不进入后台补全()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var vm = new SearchViewModel(client, new FakeNavigationService(),
                new FakeDialogService(), _ => LocValue.Empty);
            vm.Query = "zzzz-no-hit";
            vm.SearchCommand.Execute(null);

            Assert.True(await WaitUntilAsync(() => !vm.IsSearching, TimeSpan.FromSeconds(10)));
            Assert.Equal(0, vm.TotalHitCount);
            Assert.Empty(vm.Results);
            Assert.Equal(0, vm.LoadedCount);
            Assert.False(vm.IsLoadingAll);
            Assert.Equal(LocText.Empty, vm.LoadingNotice);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 补全期间CtrlA_等全量完成后选中全部()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await using (var db = new LinkPocket.Data.LinkPocketDbContext(dbPath))
            {
                for (var i = 0; i < 1000; i++)
                    db.Links.Add(new LinkPocket.Data.Link
                    {
                        LinkId = $"UIA{i:D13}",
                        Url = $"https://ui-select.example/{i}",
                        Title = $"界面全选 {i:D4}",
                    });
                await db.SaveChangesAsync();
            }

            var vm = new SearchViewModel(client, new FakeNavigationService(),
                new FakeDialogService(), _ => LocValue.Empty);
            vm.Query = "界面全选";
            vm.SearchCommand.Execute(null);

            Assert.True(await WaitUntilAsync(() => !vm.IsSearching && vm.TotalHitCount == 1000,
                TimeSpan.FromSeconds(30)));
            Assert.Equal(1000, vm.Results.Count);
            vm.SelectAllCommand.Execute(null);

            Assert.True(await WaitUntilAsync(() => !vm.IsLoadingAll && vm.LoadedCount == 1000,
                TimeSpan.FromSeconds(30)));
            Assert.Equal(1000, vm.SelectionCount);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 视口优先_末页先到且导航顺序仍按行序()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            // 10 001 条 = 两页（第 2 页只有 1 行）：请求末行时若按顺序补页，要先填完第 1 页。
            await using (var db = new LinkPocket.Data.LinkPocketDbContext(dbPath))
            {
                var rows = new List<LinkPocket.Data.Link>(10_001);
                for (var i = 0; i < 10_001; i++)
                    rows.Add(new LinkPocket.Data.Link
                    {
                        LinkId = $"UIP{i:D13}",
                        Url = $"https://ui-priority.example/{i}",
                        Title = $"界面优先 {i:D5}",
                    });
                db.Links.AddRange(rows);
                await db.SaveChangesAsync();
            }

            var vm = new SearchViewModel(client, new FakeNavigationService(),
                new FakeDialogService(), _ => LocValue.Empty);
            vm.Query = "界面优先";
            vm.SearchCommand.Execute(null);

            Assert.True(await WaitUntilAsync(() => !vm.IsSearching && vm.TotalHitCount == 10_001,
                TimeSpan.FromSeconds(30)));
            Assert.Equal(10_001, vm.Results.Count);
            vm.RequestVisibleRow(10_000);   // 视口落到最后一行 → 第 2 页优先

            Assert.True(await WaitUntilAsync(
                    () => !string.IsNullOrEmpty(vm.Results[^1].LinkId), TimeSpan.FromSeconds(10)),
                "末行没有在超时内变成真实行");
            Assert.True(vm.LoadedCount < 2000,
                $"末页没有优先到达：末行落地时已填 {vm.LoadedCount}/10001（顺序补页会填满第 1 页的 9936 行）");

            Assert.True(await WaitUntilAsync(() => !vm.IsLoadingAll && vm.LoadedCount == 10_001,
                TimeSpan.FromSeconds(30)));
            Assert.Equal(10_001, vm.OrderedLinkIds.Count);
            for (var i = 0; i < vm.Results.Count; i += 997)
                Assert.Equal(vm.Results[i].Key, vm.OrderedLinkIds[i]);   // 顺序表按跨类型身份键索引
            Assert.Equal(vm.Results[^1].Key, vm.OrderedLinkIds[^1]);
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

    [Fact]
    public async Task 多选删除_批量入站_一次撤销全部还原()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var ids = new string[3];
            for (var i = 0; i < ids.Length; i++)
                ids[i] = (await client.LinkCreateAsync($"https://batch-search.example/{i}", title: $"多选批量 {i}",
                    autoFetchMetadata: false)).Data!.LinkId;

            var vm = new SearchViewModel(client, new FakeNavigationService(),
                new FakeDialogService(), _ => LocValue.Empty);
            vm.Query = "多选批量";
            vm.SearchCommand.Execute(null);
            Assert.True(await WaitUntilAsync(
                    () => !vm.IsSearching && vm.TotalHitCount == ids.Length,
                    TimeSpan.FromSeconds(10)),
                $"搜索结果未在超时内就绪（总数：{vm.TotalHitCount}）");
            vm.SelectAllCommand.Execute(null);
            Assert.Equal(ids.Length, vm.SelectionCount);

            vm.DeleteSelectionCommand.Execute(null);
            Assert.True(await WaitUntilAsync(
                    () => vm.TotalHitCount == 0 && vm.Results.Count == 0,
                    TimeSpan.FromSeconds(10)),
                $"多选删除后结果未清空（总数：{vm.TotalHitCount}）");
            Assert.Equal(ids.Length, (await client.TrashListAsync()).Count);

            // 一条撤销记录 → 一次 restore_batch：三条全部还原，证明搜索页删除确实走了批量命令
            var undo = await client.UndoAsync();
            Assert.True(undo.Ok);
            var all = await client.LinkListAsync(perPage: 0);
            Assert.Equal(ids.Length, all.Links.Count(l => ids.Contains(l.LinkId)));
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>
    /// 「位置」列：引擎白名单没有 path，界面必须用目录树前序在**全量到齐后**本地重排，
    /// 而不是把 path 静默当 title。补页期间点排序 → 排队，CompleteLoad 应用；文件夹块仍在前。
    /// </summary>
    [Fact]
    public async Task 位置排序_补页期间排队_全量完成后按目录前序重排()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await using (var db = new LinkPocket.Data.LinkPocketDbContext(dbPath))
            {
                db.Folders.AddRange(
                    new LinkPocket.Data.Folder { FolderId = "900000000001", Name = "F1" },
                    new LinkPocket.Data.Folder { FolderId = "900000000002", Name = "F2" });
                // 标题序 = F2(A) → F1(B) → 根(C)，与位置序（根 → F1 → F2）相反。
                for (var i = 0; i < 300; i++)
                    db.Links.Add(new LinkPocket.Data.Link
                    {
                        LinkId = $"SRTPATH2{i:D8}",
                        Url = $"https://path2.example/{i}",
                        Title = $"排序标记 A{i:D3}",
                        ListId = "900000000002",
                    });
                for (var i = 0; i < 300; i++)
                    db.Links.Add(new LinkPocket.Data.Link
                    {
                        LinkId = $"SRTPATH1{i:D8}",
                        Url = $"https://path1.example/{i}",
                        Title = $"排序标记 B{i:D3}",
                        ListId = "900000000001",
                    });
                for (var i = 0; i < 200; i++)
                    db.Links.Add(new LinkPocket.Data.Link
                    {
                        LinkId = $"SRTPATH0{i:D8}",
                        Url = $"https://path0.example/{i}",
                        Title = $"排序标记 C{i:D3}",
                    });
                await db.SaveChangesAsync();
            }

            var vm = new SearchViewModel(client, new FakeNavigationService(),
                new FakeDialogService(), _ => LocValue.Empty,
                resolveFolderOrder: id => id switch
                {
                    "900000000001" => 1,
                    "900000000002" => 2,
                    _ => 0,
                });
            vm.Query = "排序标记";
            vm.SearchCommand.Execute(null);

            Assert.True(await WaitUntilAsync(() => !vm.IsSearching && vm.TotalHitCount == 800,
                    TimeSpan.FromSeconds(30)),
                $"首屏没有在超时内完成（总数：{vm.TotalHitCount}）");

            // 补页中（或刚好完成）点「位置」：无论哪一刻，最终都必须按前序排好。
            await vm.SortAsync("path", true);
            Assert.True(await WaitUntilAsync(() => !vm.IsLoadingAll && vm.LoadedCount == 800,
                    TimeSpan.FromSeconds(30)),
                $"补全/位置排序没有在超时内完成（{vm.LoadedCount}/800）");

            var titles = vm.Results.Select(r => r.Title).ToList();
            Assert.Equal(800, titles.Count);
            Assert.All(titles.Take(200), t => Assert.StartsWith("排序标记 C", t));   // 根级（前序 0）
            Assert.All(titles.Skip(200).Take(300), t => Assert.StartsWith("排序标记 B", t)); // F1
            Assert.All(titles.Skip(500), t => Assert.StartsWith("排序标记 A", t));   // F2

            // 降序：块顺序仍为 文件夹块 → 链接块；链接块内前序反向（F2 → F1 → 根）。
            await vm.SortAsync("path", false);
            var desc = vm.Results.Select(r => r.Title).ToList();
            Assert.All(desc.Take(300), t => Assert.StartsWith("排序标记 A", t));
            Assert.All(desc.Skip(300).Take(300), t => Assert.StartsWith("排序标记 B", t));
            Assert.All(desc.Skip(600), t => Assert.StartsWith("排序标记 C", t));
            Assert.Equal(800, vm.LoadedCount);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmDeleteFolder(string folderName) => true;
        public bool Confirm(string title, string message, string? confirmText = null,
            string iconKind = "delete-outline") => true;
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
