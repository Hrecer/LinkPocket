using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using LinkPocket.Kernel;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 命令覆盖补全（收尾）：把此前"有描述符、无断言"的查询/变更命令逐个补上黑盒用例。
/// 判据 = 每个用例都断言**可观测结果**（返回值 / 落库值 / 错误码），不比"调用没抛异常"。
///
/// <para>存在的意义：目录里有 58 条模块命令，若某条从没被任何测试正面驱动过，
/// 它的参数解析、错误语义、排序口径都处于"写完即冻结"状态；这些用例就是把那部分补齐。</para>
/// </summary>
public class FoldersQueryCoverageTests
{
    [Fact]
    public async Task Find_Exact_And_Contains()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作副本" });
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "Work" });

        var exact = await engine.QueryAsync<List<FolderDto>>("folders.find", new { name = "工作" });
        Assert.Single(exact);
        Assert.Equal("工作", exact[0].Name);

        var contains = await engine.QueryAsync<List<FolderDto>>("folders.find", new { name = "工作", contains = true });
        Assert.Equal(2, contains.Count);

        // 大小写不敏感（OrdinalIgnoreCase，既有按名定位口径）
        var caseInsensitive = await engine.QueryAsync<List<FolderDto>>("folders.find", new { name = "work" });
        Assert.Single(caseInsensitive);
        Assert.Equal("Work", caseInsensitive[0].Name);

        Assert.Empty(await engine.QueryAsync<List<FolderDto>>("folders.find", new { name = "不存在" }));
    }

    [Fact]
    public async Task Get_Returns_Recursive_Count_And_Rejects_Root()
    {
        var (engine, _, _) = TestHost.Create();
        var parent = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "父" })).Data!;
        var child = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "子", parent_id = parent.FolderId })).Data!;
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://a.example/", list_id = child.FolderId });

        var parentDto = await engine.QueryAsync<FolderDto>("folders.get", new { folder_id = parent.FolderId });
        Assert.Equal(1, parentDto.LinkCount);   // 递归计数：含子孙目录的链接

        var childDto = await engine.QueryAsync<FolderDto>("folders.get", new { folder_id = child.FolderId });
        Assert.Equal(1, childDto.LinkCount);

        var rootEx = await Assert.ThrowsAsync<EngineException>(
            () => engine.QueryAsync<FolderDto>("folders.get", null));
        Assert.Equal(EngineErrors.RootNotEntity, rootEx.Error.Code);

        // 无兼容：字符串 "0" 不是根，只是一个查不到的 ID
        var sentinelEx = await Assert.ThrowsAsync<EngineException>(
            () => engine.QueryAsync<FolderDto>("folders.get", new { folder_id = "0" }));
        Assert.Equal(EngineErrors.EntityNotFound, sentinelEx.Error.Code);
    }

    [Fact]
    public async Task Breadcrumb_Includes_Root_Display_Name()
    {
        var (engine, _, _) = TestHost.Create();
        var a = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" })).Data!;
        var b = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B", parent_id = a.FolderId })).Data!;

        var crumb = await engine.QueryAsync<List<string>>("folders.breadcrumb", new { folder_id = b.FolderId });
        Assert.Equal(new[] { FolderIds.RootToken, "A", "B" }, crumb);

        var rootCrumb = await engine.QueryAsync<List<string>>("folders.breadcrumb", null);
        Assert.Equal(new[] { FolderIds.RootToken }, rootCrumb);

        // 未知目录回落根（既有口径：不抛错）
        var unknown = await engine.QueryAsync<List<string>>("folders.breadcrumb", new { folder_id = "no-such" });
        Assert.Equal(new[] { FolderIds.RootToken }, unknown);
    }

    [Fact]
    public async Task CycleCheck_Detects_Descendant_Target()
    {
        var (engine, _, _) = TestHost.Create();
        var a = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" })).Data!;
        var b = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B", parent_id = a.FolderId })).Data!;

        // 把 A 移到自己的子孙 B 下 → 成环
        Assert.True(await engine.QueryAsync<bool>("folders.cycle_check",
            new { folder_id = a.FolderId, target_parent_id = b.FolderId }));

        // 移到根 → 永不成环（缺省 target_parent_id = 根）
        Assert.False(await engine.QueryAsync<bool>("folders.cycle_check", new { folder_id = a.FolderId }));

        // 把 B 移到 A 下（本来就是）→ 不成环
        Assert.False(await engine.QueryAsync<bool>("folders.cycle_check",
            new { folder_id = b.FolderId, target_parent_id = a.FolderId }));
    }

    [Fact]
    public async Task Sort_Reorders_And_Rejects_Foreign_Id()
    {
        var (engine, _, dbPath) = TestHost.Create();
        var first = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "甲" })).Data!;
        var second = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "乙" })).Data!;
        var outsider = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "丙", parent_id = first.FolderId })).Data!;

        var result = await engine.ExecuteAsync<FolderSortResult>("folders.sort",
            new { item_ids = new[] { second.FolderId, first.FolderId } });
        Assert.Equal(2, result.Data!.Sorted);

        // 落库口径：sort_order = item_ids 下标
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Assert.Equal(0L, ScalarLong(conn, $"SELECT sort_order FROM folders WHERE id = '{second.FolderId}'"));
            Assert.Equal(1L, ScalarLong(conn, $"SELECT sort_order FROM folders WHERE id = '{first.FolderId}'"));
        }

        // 不属于目标父目录的 ID → ENTITY_NOT_FOUND（整条命令不落任何改动）
        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<FolderSortResult>("folders.sort",
            new { item_ids = new[] { outsider.FolderId } }));
        Assert.Equal(EngineErrors.EntityNotFound, ex.Error.Code);
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}

public class LinksQueryCoverageTests
{
    [Fact]
    public async Task FindByUrl_Returns_All_Duplicates()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "查重" })).Data!;
        var a = (await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://dup.example/", list_id = folder.FolderId })).Data!;
        var b = (await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://dup.example/" })).Data!;
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://unique.example/" });

        var hits = await engine.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://dup.example/" });
        Assert.Equal(2, hits.Count);
        Assert.Equal(new[] { a.LinkId, b.LinkId }.OrderBy(x => x).ToArray(),
            hits.Select(h => h.LinkId).OrderBy(x => x).ToArray());

        Assert.Empty(await engine.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://dup.example" }));
    }

    [Fact]
    public async Task Roots_Only_Unfiled_And_Respects_Limit()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "有归属" })).Data!;
        var older = (await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://root1.example/" })).Data!;
        await Task.Delay(20);   // created_at 精度足够，但留出可读的先后差
        var newer = (await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://root2.example/" })).Data!;
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://filed.example/", list_id = folder.FolderId });

        var roots = await engine.QueryAsync<List<LinkDto>>("links.roots");
        Assert.Equal(2, roots.Count);
        Assert.All(roots, r => Assert.Null(r.ListId));
        Assert.Equal(newer.LinkId, roots[0].LinkId);      // 缺省 created_at 降序
        Assert.Equal(older.LinkId, roots[1].LinkId);

        var limited = await engine.QueryAsync<List<LinkDto>>("links.roots", new { per_page = 1 });
        Assert.Single(limited);
    }

    [Fact]
    public async Task VisitBatch_Counts_Each_Link_And_Refreshes_Folder_Once()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "记账" })).Data!;
        var a = (await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://v1.example/", list_id = folder.FolderId })).Data!;
        var b = (await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://v2.example/", list_id = folder.FolderId })).Data!;

        var result = await engine.ExecuteAsync<LinkBatchResult>("links.visit_batch", new { link_ids = new[] { a.LinkId, b.LinkId } });
        Assert.Equal("recorded", result.Data!.Kind);
        Assert.Equal(2, result.Data!.Affected);
        Assert.Contains("links.changed", result.Changes!.Events);

        Assert.Equal(1, (await engine.QueryAsync<LinkDto>("links.get", new { id = a.LinkId })).VisitCount);
        Assert.Equal(1, (await engine.QueryAsync<LinkDto>("links.get", new { id = b.LinkId })).VisitCount);

        // 同一目录的两条链接 → 父链只刷新一次（去重口径）
        var folderDto = await engine.QueryAsync<FolderDto>("folders.get", new { folder_id = folder.FolderId });
        Assert.Equal(1, folderDto.VisitCount);

        var empty = await Assert.ThrowsAsync<EngineException>(
            () => engine.ExecuteAsync<LinkBatchResult>("links.visit_batch", new { link_ids = Array.Empty<string>() }));
        Assert.Equal(EngineErrors.RequiredParam, empty.Error.Code);
    }
}

public class TrashQueryCoverageTests
{
    [Fact]
    public async Task List_Tree_And_UnitContents_Match_Recycle_Bin_Semantics()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "待删目录" })).Data!;
        var inFolder1 = (await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://in1.example/", list_id = folder.FolderId })).Data!;
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://in2.example/", list_id = folder.FolderId });
        var standalone = (await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://standalone.example/" })).Data!;

        await engine.ExecuteAsync<object>("links.trash", new { id = standalone.LinkId });      // 单独删除的书签
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = folder.FolderId }); // 整个目录 → 回收站单元

        // 平铺 = 单独删除的书签 + 被删文件夹单元根（不含单元内部条目）
        var flat = await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null);
        Assert.Equal(2, flat.Count);
        Assert.Contains(flat, e => e.EntryType == "link" && e.Id == standalone.LinkId);
        var unit = Assert.Single(flat, e => e.EntryType == "folder");
        Assert.Equal("待删目录", unit.Name);
        Assert.DoesNotContain(flat, e => e.Id == inFolder1.LinkId);

        // 单元树的计数 = 单元子树内书签总数
        var tree = await engine.QueryAsync<List<TrashFolderDto>>("trash.tree", null);
        var node = Assert.Single(tree, n => n.TrashFolderId == unit.Id);
        Assert.Equal(2, node.LinkCount);
        Assert.Null(node.ParentTrashFolderId);

        // 单元内容 = 直接子单元 + 子树内全部书签快照
        var contents = await engine.QueryAsync<List<TrashEntryDto>>("trash.unit_contents", new { id = unit.Id });
        Assert.Equal(2, contents.Count);
        Assert.All(contents, e => Assert.Equal("link", e.EntryType));
        Assert.Contains(contents, e => e.Id == inFolder1.LinkId);

        var missing = await Assert.ThrowsAsync<EngineException>(
            () => engine.QueryAsync<List<TrashEntryDto>>("trash.unit_contents", new { id = "no-such-unit" }));
        Assert.Equal(EngineErrors.EntityNotFound, missing.Error.Code);
    }

    [Fact]
    public async Task Overview_Snapshot_Carries_Units_Direct_Links_And_Root_Links()
    {
        var (engine, _, _) = TestHost.Create();
        // 结构：单元 A（直挂 L1 + 子单元 B；B 内 L2）+ 根级单独删除的 S
        var a = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" })).Data!;
        var b = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B", parent_id = a.FolderId })).Data!;
        var l1 = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://a.example/", title = "A 链接", list_id = a.FolderId })).Data!;
        var l2 = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://b.example/", title = "B 链接", list_id = b.FolderId })).Data!;
        var s = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://s.example/", title = "根级删除" })).Data!;

        await engine.ExecuteAsync<object>("links.trash", new { id = s.LinkId });
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = a.FolderId });

        var overview = await engine.QueryAsync<TrashOverviewDto>("trash.overview", null);

        // 单元：全量 + 子树计数 + 原位置快照（主栏「原位置」列的数据源）
        Assert.Equal(2, overview.Folders.Count);
        var unitA = Assert.Single(overview.Folders, f => f.Name == "A");
        var unitB = Assert.Single(overview.Folders, f => f.Name == "B");
        Assert.Equal(2, unitA.LinkCount);
        Assert.Equal(1, unitB.LinkCount);
        Assert.Equal(unitA.TrashFolderId, unitB.ParentTrashFolderId);
        // 原位置快照 = 单元**自身**删除前的完整路径（与 folders.delete 写快照的口径一致）
        Assert.Equal("@root/A", unitA.OriginPath);
        Assert.Equal("@root/A/B", unitB.OriginPath);

        // 链接：全量 + 每项携归属单元（null = 根级）——树叶子注入的唯一数据源
        Assert.Equal(3, overview.Links.Count);
        Assert.Null(Assert.Single(overview.Links, l => l.Id == s.LinkId).TrashFolderId);
        Assert.Equal(unitA.TrashFolderId, Assert.Single(overview.Links, l => l.Id == l1.LinkId).TrashFolderId);
        Assert.Equal(unitB.TrashFolderId, Assert.Single(overview.Links, l => l.Id == l2.LinkId).TrashFolderId);
    }
}

public class FaviconCoverageTests
{
    [Fact]
    public async Task Prefetch_Queues_Missing_Icons_And_Rejects_Unknown_Link()
    {
        var (engine, _, _) = TestHost.Create();
        var queueId = Guid.NewGuid().ToString("N");
        // 127.0.0.1:1 = 立刻连接被拒（不留网络依赖，也不落任何真实图标文件）
        var withIcon = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://pf.example/", favicon_url = $"http://127.0.0.1:1/favicon-{queueId}.ico" })).Data!;
        var withoutIcon = (await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://no-icon.example/" })).Data!;

        var queued = await engine.ExecuteAsync<JsonElement>("favicon.prefetch", new { link_ids = new[] { withIcon.LinkId } });
        Assert.Equal(1, queued.Data.GetProperty("queued").GetInt32());

        // 没有图标地址的链接无事可做 → 入队 0
        var nothing = await engine.ExecuteAsync<JsonElement>("favicon.prefetch", new { link_ids = new[] { withoutIcon.LinkId } });
        Assert.Equal(0, nothing.Data.GetProperty("queued").GetInt32());

        var missing = await Assert.ThrowsAsync<EngineException>(
            () => engine.ExecuteAsync<JsonElement>("favicon.prefetch", new { link_ids = new[] { "no-such-link" } }));
        Assert.Equal(EngineErrors.EntityNotFound, missing.Error.Code);
    }

    [Fact]
    public async Task Prefetch_All_Missing_When_No_Ids_Given()
    {
        var (engine, _, _) = TestHost.Create();
        var queueId = Guid.NewGuid().ToString("N");
        await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://all1.example/", favicon_url = $"http://127.0.0.1:1/a-{queueId}.ico" });
        await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://all2.example/", favicon_url = $"http://127.0.0.1:1/b-{queueId}.ico" });

        var queued = await engine.ExecuteAsync<JsonElement>("favicon.prefetch", null);
        Assert.Equal(2, queued.Data.GetProperty("queued").GetInt32());
        Assert.Contains("links.changed", queued.Changes!.Events);
        Assert.Contains("favicon_cache", queued.Changes!.Touched.Select(t => t.Type));
    }
}
