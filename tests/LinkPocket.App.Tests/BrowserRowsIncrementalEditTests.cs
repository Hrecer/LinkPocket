using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 主栏行集合增量编辑（C 路线）的金标准：写操作（增/删/改名重排）之后的行同步**不换集合**——
/// 只对真动了的行发一次集合通知（插入 Add / 删除 Remove / 挪位置 = Remove+Add），
/// 既有行对象原地保留（展示字段写回）；变动面超过编辑脚本上限（256）才整表替换（一次 Reset）。
/// </summary>
public class BrowserRowsIncrementalEditTests
{
    private static List<NotifyCollectionChangedAction> Watch(BrowserViewModel vm)
    {
        var events = new List<NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => events.Add(e.Action);
        return events;
    }

    [Fact]
    public async Task 新增一行_只发一次插入通知_既有行对象全部保留()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("增")).Data!;
            for (var i = 1; i <= 4; i++)
                await client.LinkCreateAsync($"https://add.example/{i}", title: $"A{i}",
                    listId: folder.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(folder.FolderId);
            var before = vm.Rows.ToList();
            Assert.Equal(4, before.Count);
            var events = Watch(vm);

            // "A2x" 按名称升序落在 A2 与 A3 之间 → 单次按位置插入
            var added = (await client.LinkCreateAsync("https://add.example/2x", title: "A2x",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;
            await vm.RefreshPreservingSelectionAsync();

            Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, events);
            Assert.Equal(5, vm.Rows.Count);
            Assert.Same(before[0], vm.Rows[0]);
            Assert.Same(before[1], vm.Rows[1]);
            Assert.Equal(added.LinkId, vm.Rows[2].Id);   // 新行按位置就位
            Assert.Same(before[2], vm.Rows[3]);
            Assert.Same(before[3], vm.Rows[4]);
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 删除一行_只发一次移除通知_其余行对象保留()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("删")).Data!;
            var links = new List<string>();
            for (var i = 1; i <= 4; i++)
                links.Add((await client.LinkCreateAsync($"https://del.example/{i}", title: $"A{i}",
                    listId: folder.FolderId, autoFetchMetadata: false)).Data!.LinkId);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(folder.FolderId);
            var before = vm.Rows.ToList();   // A1 A2 A3 A4
            var events = Watch(vm);

            await client.LinkTrashAsync(links[1]);   // 删 A2
            await vm.RefreshPreservingSelectionAsync();

            Assert.Equal(new[] { NotifyCollectionChangedAction.Remove }, events);
            Assert.Equal(3, vm.Rows.Count);
            Assert.Same(before[0], vm.Rows[0]);
            Assert.Same(before[2], vm.Rows[1]);
            Assert.Same(before[3], vm.Rows[2]);
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 改名导致重排_行被挪动复用_两次通知且字段写回()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("挪")).Data!;
            await client.LinkCreateAsync("https://mv.example/1", title: "A1",
                listId: folder.FolderId, autoFetchMetadata: false);
            var l3 = (await client.LinkCreateAsync("https://mv.example/3", title: "A3",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(folder.FolderId);
            var rowA1 = vm.Rows[0];
            var rowA3 = vm.Rows[1];
            var events = Watch(vm);

            // A3 → A0：名称升序下从尾部挪到头部
            await client.LinkUpdateAsync(l3.LinkId, title: "A0");
            await vm.RefreshPreservingSelectionAsync();

            Assert.Equal(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add }, events);
            Assert.Same(rowA3, vm.Rows[0]);   // 行对象复用（挪位置，不重建）
            Assert.Same(rowA1, vm.Rows[1]);
            Assert.Equal("A0", vm.Rows[0].Name);   // 目标值写回
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 增删混合同步_只动真变了的那些行_字段就地更新()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("混")).Data!;
            var created = new List<string>();
            for (var i = 1; i <= 4; i++)
                created.Add((await client.LinkCreateAsync($"https://mix.example/{i}", title: $"A{i}",
                    listId: folder.FolderId, autoFetchMetadata: false)).Data!.LinkId);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(folder.FolderId);
            var before = vm.Rows.ToList();   // A1 A2 A3 A4
            var events = Watch(vm);

            // 同一次刷新里：删掉 A2、插入 A2x、访问一次 A1（展示字段变化）
            await client.LinkTrashAsync(created[1]);
            await client.LinkCreateAsync("https://mix.example/2x", title: "A2x",
                listId: folder.FolderId, autoFetchMetadata: false);
            await client.LinkVisitRecordAsync(created[0]);
            await vm.RefreshPreservingSelectionAsync();

            Assert.Equal(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add }, events);
            Assert.Equal(new[] { "A1", "A2x", "A3", "A4" }, vm.Rows.Select(r => r.Name));
            Assert.Same(before[0], vm.Rows[0]);
            Assert.Same(before[2], vm.Rows[2]);
            Assert.Same(before[3], vm.Rows[3]);
            Assert.Equal(1, vm.Rows[0].ViewCount);       // A1 的访问计数就地写回
            Assert.NotNull(vm.Rows[0].LastViewedAt);
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 小目录换目录_目标集合不相交也走增量编辑()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("甲")).Data!;
            var b = (await client.FolderCreateAsync("乙")).Data!;
            await client.LinkCreateAsync("https://nav.example/a1", title: "A1", listId: a.FolderId, autoFetchMetadata: false);
            await client.LinkCreateAsync("https://nav.example/a2", title: "A2", listId: a.FolderId, autoFetchMetadata: false);
            await client.LinkCreateAsync("https://nav.example/b1", title: "B1", listId: b.FolderId, autoFetchMetadata: false);
            await client.LinkCreateAsync("https://nav.example/b2", title: "B2", listId: b.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(a.FolderId);
            Assert.Equal(new[] { "A1", "A2" }, vm.Rows.Select(r => r.Name));
            var events = Watch(vm);

            // 小集合：移除 2 + 插入 2（都在编辑脚本上限内）→ 不整表替换
            await vm.LoadAsync(b.FolderId);
            Assert.Equal(new[] { "B1", "B2" }, vm.Rows.Select(r => r.Name));
            Assert.Equal(new[]
            {
                NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Remove,
                NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add,
            }, events);
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 变动超过编辑脚本上限_回退整表替换_一次Reset()
    {
        var (client, engine, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("大")).Data!;
            await client.LinkCreateAsync("https://big.example/base", title: "base",
                listId: folder.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(folder.FolderId);
            Assert.Single(vm.Rows);
            var events = Watch(vm);

            // 外部进程一次性写入 300 条（模拟批脚本直改库）：变动面远超编辑脚本上限（256）
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO links (id, url, title, folder_id, is_important, visit_count, created_at, updated_at) "
                                  + "VALUES (@id, @url, @title, @folder, 0, 0, @now, @now)";
                cmd.Parameters.AddWithValue("@url", "https://big.example/x");
                cmd.Parameters.AddWithValue("@title", "bulk");
                cmd.Parameters.AddWithValue("@folder", folder.FolderId);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@id", string.Empty);
                for (var i = 0; i < 300; i++)
                {
                    cmd.Parameters["@id"].Value = $"B{i:D15}";
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            // 外部写入不会自己发事件——宿主探测到"外部进程写过库"后作废查询缓存（App 里同一动作）
            engine.InvalidateQueryCache();

            await vm.RefreshPreservingSelectionAsync();
            Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
            Assert.Equal(301, vm.Rows.Count);
        }
        finally { AppTestEnv.Delete(dbPath); }
    }
}
