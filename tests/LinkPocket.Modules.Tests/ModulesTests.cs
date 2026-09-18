using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using LinkPocket.Kernel;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>目录自描述：52 个命令全部注册、无重复、查询/变更分类正确。</summary>
public class CatalogTests
{
    [Fact]
    public void Describe_Returns_All_53_Commands()
    {
        var (engine, _, _) = TestHost.Create();
        var manifest = engine.Describe();

        Assert.Equal(53, manifest.Commands.Count);
        Assert.Equal(53, manifest.Commands.Select(c => c.Name).Distinct().Count());
        Assert.All(manifest.Commands, c => Assert.Matches(@"^[a-z_]+\.[a-z_]+$", c.Name));
    }

    [Fact]
    public void Describe_By_Category_Splits_Correctly()
    {
        var (engine, _, _) = TestHost.Create();
        Assert.Equal(14, engine.Describe("folders").Commands.Count);
        Assert.Equal(16, engine.Describe("links").Commands.Count);
        Assert.Equal(7, engine.Describe("trash").Commands.Count);
        Assert.Equal(3, engine.Describe("maintenance").Commands.Count);

        var contents = engine.Describe("folders").Commands.Single(c => c.Name == "folders.contents");
        Assert.True(contents.IsQuery);
        var overview = engine.Describe("folders").Commands.Single(c => c.Name == "folders.overview");
        Assert.True(overview.IsQuery);
        var create = engine.Describe("folders").Commands.Single(c => c.Name == "folders.create");
        Assert.True(create.IsMutation);
        var purge = engine.Describe("trash").Commands.Single(c => c.Name == "trash.purge");
        Assert.True(purge.IsDestructive);
    }
}

public class FoldersModuleTests
{
    [Fact]
    public async Task Create_And_Contents_Works()
    {
        var (engine, _, _) = TestHost.Create();
        var created = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作", description = "d1" });
        Assert.Equal("工作", created.Data!.Name);
        Assert.Null(created.Data!.ParentId);
        Assert.Contains("folders.changed", created.Changes!.Events);

        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", null);
        Assert.Equal(FolderIds.RootDisplayName, contents.FolderName);
        Assert.Single(contents.SubFolders);
        Assert.Equal("工作", contents.SubFolders[0].Name);
        Assert.Equal(new[] { FolderIds.RootDisplayName }, contents.Breadcrumb);

        var child = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "资料", parent_id = created.Data!.FolderId });
        var breadcrumb = await engine.QueryAsync<List<string>>("folders.breadcrumb", new { folder_id = child.Data!.FolderId });
        Assert.Equal(new[] { FolderIds.RootDisplayName, "工作", "资料" }, breadcrumb);
    }

    [Fact]
    public async Task Overview_Single_Snapshot_Contents_Plus_Tree_Plus_RootCount()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "快照" });
        await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://x.example/1", title = "内部链接", list_id = folder.Data!.FolderId });
        await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://x.example/root", title = "根级链接" });

        // 子目录分支：overview = contents 目录页 + 全量树 + 根级计数（同一 UoW 单快照）
        var overview = await engine.QueryAsync<FolderContentsDto>("folders.overview", new { folder_id = folder.Data!.FolderId });
        Assert.Equal("快照", overview.FolderName);
        Assert.Single(overview.Links);
        Assert.NotNull(overview.Tree);
        Assert.Contains(overview.Tree, f => f.FolderId == folder.Data!.FolderId && f.LinkCount == 1);
        var tree = await engine.QueryAsync<List<FolderDto>>("folders.tree", null);
        Assert.Equal(tree.Count, overview.Tree!.Count);
        Assert.All(overview.Tree, f => Assert.Contains(tree, t => t.FolderId == f.FolderId));

        // 根级计数与 links.stats.RootLevel 同口径；根分支复用目录页计数
        var stats = await engine.QueryAsync<LinkCountsDto>("links.stats", null);
        var rootOverview = await engine.QueryAsync<FolderContentsDto>("folders.overview", null);
        Assert.Equal(stats.RootLevel, rootOverview.RootLinkCount);
        Assert.Equal(rootOverview.DirectLinkCount, rootOverview.RootLinkCount);

        // 契约：folders.contents 响应形状不变（tree/root_link_count 恒 null）
        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", new { folder_id = folder.Data!.FolderId });
        Assert.Null(contents.Tree);
        Assert.Null(contents.RootLinkCount);
    }

    [Fact]
    public async Task Create_With_Missing_Parent_Fails()
    {
        var (engine, _, _) = TestHost.Create();
        var ex = await Assert.ThrowsAsync<EngineException>(
            () => engine.ExecuteAsync<FolderDto>("folders.create", new { name = "x", parent_id = "nope" }));
        Assert.Equal(EngineErrors.EntityNotFound, ex.Error.Code);
    }

    [Fact]
    public async Task Contents_LastVisited_Sort_Puts_Never_Visited_Last_Both_Ways()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "F" });
        await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://x.example/never", title = "从未", list_id = folder.Data!.FolderId });
        var visited = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://x.example/yes", title = "看过", list_id = folder.Data!.FolderId });
        await engine.ExecuteAsync<object>("links.visit_record", new { id = visited.Data!.LinkId });

        // 行为契约 §9：「最后查看」为空的恒排最后 —— 升/降序都成立（SQL 端 (col IS NULL) 前置子句）
        foreach (var order in new[] { "asc", "desc" })
        {
            var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", new
            {
                folder_id = folder.Data!.FolderId,
                sort_by = "last_visited_at",
                sort_order = order,
            });
            Assert.Equal(new[] { "看过", "从未" }, contents.Links.Select(l => l.Title));
        }
    }

    [Fact]
    public async Task Sort_Order_Is_Readable_By_Contents_And_Tree()
    {
        var (engine, _, _) = TestHost.Create();
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        var b = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B" });
        var c = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "C" });

        // folders.sort 写入手动顺序：C → B → A（此前该列只有写路径，没有读路径）
        await engine.ExecuteAsync<FolderSortResult>("folders.sort",
            new { item_ids = new[] { c.Data!.FolderId, b.Data!.FolderId, a.Data!.FolderId } });

        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", new { sort_by = "sort_order" });
        Assert.Equal(new[] { "C", "B", "A" }, contents.SubFolders.Select(f => f.Name));
        Assert.Equal(new[] { 0, 1, 2 }, contents.SubFolders.Select(f => f.SortOrder));

        var tree = await engine.QueryAsync<List<FolderDto>>("folders.tree", new { sort_by = "sort_order" });
        Assert.Equal(new[] { "C", "B", "A" }, tree.Select(f => f.Name));

        // 缺省仍为名称升序
        var byName = await engine.QueryAsync<FolderContentsDto>("folders.contents");
        Assert.Equal(new[] { "A", "B", "C" }, byName.SubFolders.Select(f => f.Name));
    }

    [Fact]
    public async Task Contents_Truncation_Is_Visible_Not_Silent()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "F" });
        await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://x.example/1", title = "1", list_id = folder.Data!.FolderId });
        await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://x.example/2", title = "2", list_id = folder.Data!.FolderId });

        // 显式 per_page ≤ 上限：分页是调用方自己的选择，不算截断
        var paged = await engine.QueryAsync<FolderContentsDto>("folders.contents",
            new { folder_id = folder.Data!.FolderId, per_page = 1 });
        Assert.False(paged.Truncated);
        Assert.Single(paged.Links);
        Assert.Equal(2, paged.DirectLinkCount);

        // 显式 per_page 超上限：夹到上限，且 truncated 必须为 true
        var capped = await engine.QueryAsync<FolderContentsDto>("folders.contents",
            new { folder_id = folder.Data!.FolderId, per_page = int.MaxValue });
        Assert.True(capped.Truncated);
        Assert.Equal(10_000, capped.PerPage);
    }

    [Fact]
    public async Task Contents_Wrong_Param_Type_Fails_Not_Silently_Defaults()
    {
        var (engine, _, _) = TestHost.Create();
        // per_page 传字符串 = 类型错 → LP.VAL.002（旧口径会静默变 0 = 全量）
        var ex = await Assert.ThrowsAsync<EngineException>(
            () => engine.QueryAsync<FolderContentsDto>("folders.contents", new { per_page = "abc" }));
        Assert.Equal(EngineErrors.TypeMismatch, ex.Error.Code);
    }

    [Fact]
    public async Task Move_Into_Own_Subtree_Rejected()
    {
        var (engine, _, _) = TestHost.Create();
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        var b = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B", parent_id = a.Data!.FolderId });

        var ex = await Assert.ThrowsAsync<EngineException>(
            () => engine.ExecuteAsync<FolderDto>("folders.move", new { folder_id = a.Data!.FolderId, target_parent_id = b.Data!.FolderId }));
        Assert.Equal(EngineErrors.CycleDetected, ex.Error.Code);

        var cycle = await engine.QueryAsync<bool>("folders.cycle_check", new { folder_id = a.Data!.FolderId, target_parent_id = b.Data!.FolderId });
        Assert.True(cycle);
    }

    [Fact]
    public async Task Delete_Trash_Mirrors_Subtree_With_Origin_Paths()
    {
        var (engine, _, _) = TestHost.Create();
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        var b = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B", parent_id = a.Data!.FolderId });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example", title = "X", list_id = b.Data!.FolderId });

        var deleted = await engine.ExecuteAsync<FolderDeleteResult>(
            "folders.delete", new { folder_id = a.Data!.FolderId });
        Assert.Equal("trash_links", deleted.Data!.Cascade);
        Assert.Equal(2, deleted.Data!.DeletedFolders);
        Assert.Equal(1, deleted.Data!.TrashedLinks);

        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", null);
        Assert.Empty(contents.SubFolders);
        Assert.Empty(contents.Links);

        // 回收站平铺：只有单元根 A（B 在树里、X 在单元里）
        var flat = await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null);
        Assert.Single(flat);
        Assert.Equal("A", flat[0].Name);

        var tree = await engine.QueryAsync<List<TrashFolderDto>>("trash.tree", null);
        Assert.Equal(2, tree.Count);
        Assert.Contains(tree, t => t.Name == "A" && t.LinkCount == 1);

        var unit = await engine.QueryAsync<List<TrashEntryDto>>("trash.unit_contents", new { id = a.Data!.FolderId });
        Assert.Equal(2, unit.Count);
        Assert.Contains(unit, e => e.EntryType == "link" && e.OriginPath == "全部书签 / A / B");
    }

    [Fact]
    public async Task Delete_MoveToList_Moves_Links()
    {
        var (engine, _, _) = TestHost.Create();
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        var target = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "T" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example", title = "X", list_id = a.Data!.FolderId });

        var deleted = await engine.ExecuteAsync<FolderDeleteResult>(
            "folders.delete", new { folder_id = a.Data!.FolderId, cascade = "move_to_list", target_list_id = target.Data!.FolderId });
        Assert.Equal(1, deleted.Data!.DeletedFolders);

        var stats = await engine.QueryAsync<LinkCountsDto>("links.stats", null);
        Assert.Equal(1, stats.ByFolder[target.Data!.FolderId]);
    }

    [Fact]
    public async Task MoveBatch_Auto_Numbering_Windows_Style()
    {
        var (engine, _, _) = TestHost.Create();
        var source1 = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        var source2 = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        var dest = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "归档" });

        var result = await engine.ExecuteAsync<FolderMoveBatchResult>(
            "folders.move_batch", new { folder_ids = new[] { source1.Data!.FolderId, source2.Data!.FolderId }, target_parent_id = dest.Data!.FolderId });
        Assert.Equal(2, result.Data!.Moved);
        Assert.Single(result.Data!.RenamedNotes);

        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", new { folder_id = dest.Data!.FolderId });
        var names = contents.SubFolders.Select(f => f.Name).ToList();
        Assert.Contains("工作", names);
        Assert.Contains("工作 (2)", names);
    }

    [Fact]
    public async Task Copy_Deep_With_New_Ids()
    {
        var (engine, _, _) = TestHost.Create();
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example", title = "X", list_id = a.Data!.FolderId });

        var copy = await engine.ExecuteAsync<FolderCopyResult>("folders.copy", new { folder_id = a.Data!.FolderId });
        Assert.NotEqual(a.Data!.FolderId, copy.Data!.NewFolderId);

        var stats = await engine.QueryAsync<LinkCountsDto>("links.stats", null);
        Assert.Equal(2, stats.Total);
        Assert.Equal(1, stats.ByFolder[copy.Data!.NewFolderId]);
    }

    [Fact]
    public async Task DryRun_Creates_Nothing()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" }, new CallOptions(DryRun: true));

        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", null);
        Assert.Empty(contents.SubFolders);
    }

    [Fact]
    public async Task Delete_Move_To_List_Into_Own_Subtree_Rejected()
    {
        var (engine, _, _) = TestHost.Create();
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        var b = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B", parent_id = a.Data!.FolderId });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example", title = "X", list_id = b.Data!.FolderId });

        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExecuteAsync<object>("folders.delete",
                new { folder_id = a.Data!.FolderId, cascade = "move_to_list", target_list_id = b.Data!.FolderId }));
        Assert.Equal(EngineErrors.CycleDetected, ex.Error.Code);

        // 目标在子树内被拒绝 → 链接未丢
        var stats = await engine.QueryAsync<LinkCountsDto>("links.stats", null);
        Assert.Equal(1, stats.Total);
    }

    [Fact]
    public async Task Sort_Duplicate_Ids_Rejected()
    {
        var (engine, _, _) = TestHost.Create();
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B" });

        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExecuteAsync<object>("folders.sort",
                new { item_ids = new[] { a.Data!.FolderId, a.Data!.FolderId } }));
        Assert.Equal(EngineErrors.TypeMismatch, ex.Error.Code);
    }

    [Fact]
    public async Task Update_Whitespace_Name_Rejected()
    {
        var (engine, _, _) = TestHost.Create();
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });

        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExecuteAsync<object>("folders.update", new { folder_id = a.Data!.FolderId, name = "   " }));
        Assert.Equal(EngineErrors.RequiredParam, ex.Error.Code);
    }

    [Fact]
    public async Task Contents_Root_Honors_Page()
    {
        var (engine, _, _) = TestHost.Create();
        for (var i = 0; i < 3; i++)
            await engine.ExecuteAsync<LinkDto>("links.create", new { url = $"https://r.example/{i}", title = $"R{i}" });

        var page2 = await engine.QueryAsync<FolderContentsDto>("folders.contents", new { page = 2, per_page = 2 });
        Assert.Equal(2, page2.CurrentPage);
        Assert.Equal(2, page2.LastPage);
        Assert.Single(page2.Links);
        Assert.Equal("R2", page2.Links[0].Title);
    }
}

public class LinksModuleTests
{
    [Fact]
    public async Task Create_Update_Get_Full_Lifecycle()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "F" });

        var created = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://github.com", title = "GitHub", list_id = folder.Data!.FolderId });
        Assert.Equal(16, created.Data!.LinkId.Length);

        var updated = await engine.ExecuteAsync<LinkDto>("links.update",
            new { id = created.Data!.LinkId, title = "GH", is_important = true });
        Assert.Equal("GH", updated.Data!.Title);
        Assert.True(updated.Data!.IsImportant);

        var got = await engine.QueryAsync<LinkDto>("links.get", new { id = created.Data!.LinkId });
        Assert.Equal("GH", got.Title);
    }

    [Fact]
    public async Task Get_Unknown_Link_Throws_NotFound()
    {
        var (engine, _, _) = TestHost.Create();
        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.QueryAsync<LinkDto>("links.get", new { id = "missing" }));
        Assert.Equal(EngineErrors.EntityNotFound, ex.Error.Code);
    }

    [Fact]
    public async Task Update_Wrong_Bool_Type_Fails_Not_Silently_False()
    {
        var (engine, _, _) = TestHost.Create();
        var link = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example", title = "X" });

        // is_important 传字符串 = 类型错 → LP.VAL.002（旧口径会静默当 false）
        var ex = await Assert.ThrowsAsync<EngineException>(
            () => engine.ExecuteAsync<LinkDto>("links.update", new { id = link.Data!.LinkId, is_important = "yes" }));
        Assert.Equal(EngineErrors.TypeMismatch, ex.Error.Code);
    }

    [Fact]
    public async Task Trash_Then_Restore_Preserves_Id_And_Origin()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "F" });
        var link = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://x.example", title = "X", list_id = folder.Data!.FolderId });

        var trashed = await engine.ExecuteAsync<LinkTrashResult>("links.trash", new { id = link.Data!.LinkId });
        Assert.Equal(link.Data!.LinkId, trashed.Data!.LinkId);
        Assert.Equal("全部书签 / F", trashed.Data!.OriginPath);
        Assert.Contains("trash.changed", trashed.Changes!.Events);

        var restored = await engine.ExecuteAsync<TrashRestoreResult>("trash.restore", new { id = link.Data!.LinkId });
        Assert.Equal(link.Data!.LinkId, restored.Data!.LinkId);

        var got = await engine.QueryAsync<LinkDto>("links.get", new { id = link.Data!.LinkId });
        Assert.Null(got.ListId);   // 还原落根
    }

    [Fact]
    public async Task Restore_To_Origin_Uses_Snapshot_Folder()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "F" });
        var link = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://x.example", title = "X", list_id = folder.Data!.FolderId });
        await engine.ExecuteAsync<LinkTrashResult>("links.trash", new { id = link.Data!.LinkId });

        var restored = await engine.ExecuteAsync<TrashRestoreResult>(
            "trash.restore", new { id = link.Data!.LinkId, to_origin = true });
        Assert.True(restored.Data!.RestoredToOrigin);
        Assert.Equal(folder.Data!.FolderId, restored.Data!.ListId);

        var got = await engine.QueryAsync<LinkDto>("links.get", new { id = link.Data!.LinkId });
        Assert.Equal(folder.Data!.FolderId, got.ListId);

        // 原目录已不存在 → 回落根，且结果如实回报（不假装还原到了原位）
        await engine.ExecuteAsync<LinkTrashResult>("links.trash", new { id = link.Data!.LinkId });
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = folder.Data!.FolderId });
        var fallback = await engine.ExecuteAsync<TrashRestoreResult>(
            "trash.restore", new { id = link.Data!.LinkId, to_origin = true });
        Assert.False(fallback.Data!.RestoredToOrigin);
        Assert.Null(fallback.Data!.ListId);
    }

    [Fact]
    public async Task VisitRecord_Updates_Folder_Chain()
    {
        var (engine, _, _) = TestHost.Create();
        var parent = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "P" });
        var child = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "C", parent_id = parent.Data!.FolderId });
        var link = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://x.example", title = "X", list_id = child.Data!.FolderId });

        await engine.ExecuteAsync<JsonElement>("links.visit_record", new { id = link.Data!.LinkId });

        var after = await engine.QueryAsync<FolderDto>("folders.get", new { folder_id = parent.Data!.FolderId });
        Assert.Equal(1, after.VisitCount);
        Assert.NotNull(after.LastVisitedAt);
    }

    [Fact]
    public async Task SmartList_Four_Presets()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://a.example", title = "A" });
        var link = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://b.example", title = "B" });
        await engine.ExecuteAsync<object>("links.visit_record", new { id = link.Data!.LinkId });

        var added = await engine.QueryAsync<List<LinkDto>>("links.smart_list", new { kind = "recently_added" });
        Assert.Equal(2, added.Count);
        Assert.Equal("B", added[0].Title);

        var visited = await engine.QueryAsync<List<LinkDto>>("links.smart_list", new { kind = "recently_visited" });
        Assert.Single(visited);

        var most = await engine.QueryAsync<List<LinkDto>>("links.smart_list", new { kind = "most_visited" });
        Assert.Single(most);

        var unknown = await Assert.ThrowsAsync<EngineException>(
            () => engine.QueryAsync<List<LinkDto>>("links.smart_list", new { kind = "nope" }));
        Assert.Equal(EngineErrors.EnumOutOfRange, unknown.Error.Code);
    }

    [Fact]
    public async Task Query_Structured_Filter_Sort_Fields()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://github.com/a", title = "GitHub A" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://gitlab.com/b", title = "GitLab B" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://microsoft.com", title = "MS" });

        var byUrl = await engine.QueryAsync<Modules.Links.PagedLinkResult>("links.query",
            JsonSerializer.SerializeToElement(new
            {
                filter = new object[] { new { field = "url", op = "starts", value = "https://git" } },
                sort = new object[] { new { field = "title", dir = "asc" } },
                page = new { index = 1, size = 0 },
            }));
        Assert.Equal(2, byUrl.Total);

        var projected = await engine.QueryAsync<Modules.Links.PagedLinkResult>("links.query",
            JsonSerializer.SerializeToElement(new
            {
                filter = new object[] { new { field = "title", op = "contains", value = "GitHub" } },
                fields = new[] { "id", "title" },
            }));
        var row = Assert.IsType<Dictionary<string, object?>>(projected.Items.Single());
        Assert.Equal("GitHub A", row["title"]);
        Assert.False(row.ContainsKey("url"));

        var bad = await Assert.ThrowsAsync<EngineException>(() => engine.QueryAsync<Modules.Links.PagedLinkResult>("links.query",
            JsonSerializer.SerializeToElement(new
            {
                filter = new object[] { new { field = "hacker", op = "eq", value = "1" } },
            })));
        Assert.Equal(EngineErrors.EnumOutOfRange, bad.Error.Code);

        // visit_count 不支持 eq（白名单 gte/lte/gt/lt/between）
        await Assert.ThrowsAsync<EngineException>(() => engine.QueryAsync<Modules.Links.PagedLinkResult>("links.query",
            JsonSerializer.SerializeToElement(new
            {
                filter = new object[] { new { field = "visit_count", op = "eq", value = 1 } },
            })));
    }

    [Fact]
    public async Task MoveBatch_And_CopyBatch_Work()
    {
        var (engine, _, _) = TestHost.Create();
        var f1 = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "F1" });
        var f2 = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "F2" });
        var l1 = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example/1", title = "1", list_id = f1.Data!.FolderId });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example/2", title = "2", list_id = f1.Data!.FolderId });

        var moved = await engine.ExecuteAsync<LinkBatchResult>("links.move_batch",
            new { link_ids = new[] { l1.Data!.LinkId }, target_list_id = f2.Data!.FolderId });
        Assert.Equal(1, moved.Data!.Affected);

        var copied = await engine.ExecuteAsync<LinkBatchResult>("links.copy_batch",
            new { link_ids = new[] { l1.Data!.LinkId }, target_list_id = f2.Data!.FolderId });
        Assert.Equal(1, copied.Data!.Affected);

        var stats = await engine.QueryAsync<LinkCountsDto>("links.stats", null);
        Assert.Equal(1, stats.ByFolder[f1.Data!.FolderId]);
        Assert.Equal(2, stats.ByFolder[f2.Data!.FolderId]);

        var sameUrl = await engine.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://x.example/1" });
        Assert.Equal(2, sameUrl.Count);
    }

    [Fact]
    public async Task Export_Json_And_Csv()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example,a", title = "A\"B" });

        var jsonPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpexp_{Guid.NewGuid():N}.json");
        var csvPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpexp_{Guid.NewGuid():N}.csv");

        var json = await engine.ExecuteAsync<LinkExportResult>("links.export",
            new { file_path = jsonPath, format = "json" });
        Assert.Equal(1, json.Data!.Count);
        Assert.Contains("\"url\": \"https://x.example,a\"", await File.ReadAllTextAsync(jsonPath));

        var csv = await engine.ExecuteAsync<LinkExportResult>("links.export",
            new { file_path = csvPath, format = "csv" });
        Assert.Equal(1, csv.Data!.Count);
        Assert.Contains("\"A\"\"B\"", await File.ReadAllTextAsync(csvPath));
    }

    [Fact]
    public async Task Auto_Fetch_Failure_Is_Reported_Not_Swallowed()
    {
        var (engine, _, _) = TestHost.Create();
        // 非 http/https 绝对地址 → 抓取立即失败（不触网），失败必须出现在结果的 warnings 里
        var created = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "notaurl", auto_fetch_metadata = true });

        Assert.True(created.Ok);
        var warning = Assert.Single(created.Changes!.Warnings!);
        Assert.Contains(EngineErrors.InvalidUrl, warning);
        Assert.Contains("元数据未抓取到", created.Changes!.HumanSummary);

        // 未开启抓取 → 无 warnings
        var plain = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example", title = "X" });
        Assert.Null(plain.Changes!.Warnings);
    }

    [Fact]
    public async Task MetadataFetch_Invalid_Url_Throws()
    {
        var (engine, _, _) = TestHost.Create();
        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            engine.QueryAsync<MetadataDto>("links.metadata_fetch", new { url = "ftp://bad" }));
        Assert.Equal(EngineErrors.InvalidUrl, ex.Error.Code);
    }

    [Fact]
    public async Task Idempotency_Key_Dedupes_Creation()
    {
        var (engine, _, _) = TestHost.Create();
        var first = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://x.example", title = "A" }, new CallOptions(IdempotencyKey: "k1"));
        var second = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://x.example", title = "A" }, new CallOptions(IdempotencyKey: "k1"));

        Assert.Equal(first.Data!.LinkId, second.Data!.LinkId);
        var stats = await engine.QueryAsync<LinkCountsDto>("links.stats", null);
        Assert.Equal(1, stats.Total);
    }
}

public class TrashModuleTests
{
    [Fact]
    public async Task Purge_Requires_Two_Phase_Confirm()
    {
        var (engine, _, _) = TestHost.Create();
        var link = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example", title = "X" });
        await engine.ExecuteAsync<object>("links.trash", new { id = link.Data!.LinkId });

        var ex = await Assert.ThrowsAsync<EngineException>(
            () => engine.ExecuteAsync<object>("trash.purge", new { id = link.Data!.LinkId, is_folder = false }));
        Assert.Equal(EngineErrors.ConfirmRequired, ex.Error.Code);
        var token = ex.Error.Details!.Value.GetProperty("confirm_token").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        await engine.ExecuteAsync<object>("trash.purge",
            new { id = link.Data!.LinkId, is_folder = false }, new CallOptions(ConfirmToken: token));

        var flat = await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null);
        Assert.Empty(flat);
    }

    [Fact]
    public async Task Purge_Folder_Removes_Subtree()
    {
        var (engine, _, _) = TestHost.Create();
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        var b = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B", parent_id = a.Data!.FolderId });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example", title = "X", list_id = b.Data!.FolderId });
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = a.Data!.FolderId });

        var ex = await Assert.ThrowsAsync<EngineException>(
            () => engine.ExecuteAsync<object>("trash.purge", new { id = a.Data!.FolderId, is_folder = true }));
        var token = ex.Error.Details!.Value.GetProperty("confirm_token").GetString();
        var purged = await engine.ExecuteAsync<JsonElement>("trash.purge",
            new { id = a.Data!.FolderId, is_folder = true }, new CallOptions(ConfirmToken: token));
        Assert.Equal(2, purged.Data.GetProperty("units").GetInt32());   // A + B

        var tree = await engine.QueryAsync<List<TrashFolderDto>>("trash.tree", null);
        Assert.Empty(tree);
    }

    [Fact]
    public async Task RestoreBatch_Works()
    {
        var (engine, _, _) = TestHost.Create();
        var l1 = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://1.example", title = "1" });
        var l2 = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://2.example", title = "2" });
        await engine.ExecuteAsync<object>("links.trash", new { id = l1.Data!.LinkId });
        await engine.ExecuteAsync<object>("links.trash", new { id = l2.Data!.LinkId });

        var restored = await engine.ExecuteAsync<TrashRestoreBatchResult>("trash.restore_batch",
            new { ids = new[] { l1.Data!.LinkId, l2.Data!.LinkId } });
        Assert.Equal(2, restored.Data!.Restored);

        var stats = await engine.QueryAsync<LinkCountsDto>("links.stats", null);
        Assert.Equal(2, stats.Total);
    }
}

public class SearchModuleTests
{
    [Fact]
    public async Task Search_Four_Scopes_Path_Expansion_And_Explain()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "开发工具" });
        var child = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "子目录", parent_id = folder.Data!.FolderId });
        await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://github.com", title = "GitHub", description = "代码托管", list_id = child.Data!.FolderId });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://python.org", title = "Python" });

        Assert.Single(await engine.QueryAsync<List<LinkDto>>("search.links", new { query = "github", search_url = true }));
        Assert.Single(await engine.QueryAsync<List<LinkDto>>("search.links", new { query = "托管", search_description = true }));

        // 路径范围：命中「开发工具」→ 子树展开命中子目录里的 GitHub
        var byPath = await engine.QueryAsync<List<LinkDto>>("search.links", new { query = "开发工具", search_path = true });
        Assert.Single(byPath);

        // explain：命中字段说明（关掉标题范围才能拿到纯 url 命中）
        var explain = await engine.QueryAsync<Modules.Search.SearchExplanation>("search.explain",
            new { query = "github", search_title = false, search_url = true });
        var hit = Assert.Single(explain.Hits);
        Assert.Equal(new[] { "url" }, hit.Matched);

        // 空查询：引导空态是界面职责，引擎直接报 LP.VAL.001（不返回空列表制造假象）
        var empty = await Assert.ThrowsAsync<EngineException>(
            () => engine.QueryAsync<List<LinkDto>>("search.links", new { query = "  " }));
        Assert.Equal(EngineErrors.RequiredParam, empty.Error.Code);
    }

    [Fact]
    public async Task Search_Keyword_Wildcards_Are_Literal()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://a.example", title = "100% 完成" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://b.example", title = "100 完成" });

        // 关键词里的 % 按字面匹配：不得当 LIKE 通配符把「100 完成」也捞进来
        var hit = Assert.Single(await engine.QueryAsync<List<LinkDto>>("search.links", new { query = "100%" }));
        Assert.Equal("100% 完成", hit.Title);
    }

    [Fact]
    public async Task Search_Sorts_By_Title_Through_Sql()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example/2", title = "beta 工具" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example/1", title = "Alpha 工具" });

        var asc = await engine.QueryAsync<List<LinkDto>>("search.links", new { query = "工具" });
        Assert.Equal(new[] { "Alpha 工具", "beta 工具" }, asc.Select(l => l.Title));

        var desc = await engine.QueryAsync<List<LinkDto>>("search.links", new { query = "工具", sort_order = "desc" });
        Assert.Equal(new[] { "beta 工具", "Alpha 工具" }, desc.Select(l => l.Title));
    }
}

public class BookmarksModuleTests
{
    private const string SampleHtml = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
        <TITLE>Bookmarks</TITLE>
        <H1>Bookmarks</H1>
        <DL><p>
            <DT><A HREF="https://root.example/a&amp;b">Root &amp; Link</A>
            <DT><H3 ADD_DATE="1700000000" LAST_MODIFIED="1700000100">Folder &lt;One&gt;</H3>
            <DL><p>
                <DT><A HREF="https://inner.example">Inner</A>
                <DD>Inner description
            </DL><p>
        </DL><p>
        """;

    [Fact]
    public async Task Inspect_Import_Export_RoundTrip()
    {
        var (engine, _, _) = TestHost.Create();
        var htmlPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbm_{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(htmlPath, SampleHtml);

        var inspection = await engine.QueryAsync<BookmarkFileInspectionDto>("bookmarks.inspect", new { file_path = htmlPath });
        Assert.True(inspection.IsValid);
        Assert.Equal(1, inspection.FolderCount);
        Assert.Equal(2, inspection.LinkCount);

        var imported = await engine.ExecuteAsync<JsonElement>("bookmarks.import", new { file_path = htmlPath });
        Assert.Equal(1, imported.Data.GetProperty("folders_created").GetInt32());
        Assert.Equal(2, imported.Data.GetProperty("links_created").GetInt32());

        // 实体解码与导出互逆：HREF 里的 &amp; 还原成 &
        var rootLink = await engine.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://root.example/a&b" });
        Assert.Single(rootLink);
        Assert.Equal("Root & Link", rootLink[0].Title);

        // 导出 → 二次导出逐行一致（往返保真）
        var exportPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbm_{Guid.NewGuid():N}.html");
        await engine.ExecuteAsync<object>("bookmarks.export", new { file_path = exportPath });
        var first = await File.ReadAllLinesAsync(exportPath);

        var export2Path = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbm_{Guid.NewGuid():N}.html");
        await engine.ExecuteAsync<object>("bookmarks.export", new { file_path = export2Path });
        var second = await File.ReadAllLinesAsync(export2Path);
        Assert.Equal(first, second);

        // 自校验：导出产物预检有效、条目数一致
        var reInspect = await engine.QueryAsync<BookmarkFileInspectionDto>("bookmarks.inspect", new { file_path = exportPath });
        Assert.True(reInspect.IsValid);
        Assert.Equal(2, reInspect.LinkCount);
        Assert.Equal(1, reInspect.FolderCount);
    }

    [Fact]
    public async Task Import_Invalid_File_Throws()
    {
        var (engine, _, _) = TestHost.Create();
        var badPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbm_{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(badPath, "<html>不是书签文件</html>");

        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExecuteAsync<object>("bookmarks.import", new { file_path = badPath }));
        Assert.Equal(EngineErrors.InvalidPath, ex.Error.Code);
    }

    [Fact]
    public async Task Import_Surrogate_Code_Point_Does_Not_Crash()
    {
        var (engine, _, _) = TestHost.Create();
        var htmlPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbm_{Guid.NewGuid():N}.html");
        // &#xD800; 是代理区非法标量：过去会抛 ArgumentOutOfRange → 冒成 LP.SYS.003；现在按无法解码实体保留字面量
        await File.WriteAllTextAsync(htmlPath,
            "<!DOCTYPE NETSCAPE-Bookmark-file-1>\r\n<DL><p>\r\n" +
            "<DT><A HREF=\"https://s.example\" ADD_DATE=\"1600000000\">代理 &#xD800; 码点</A>\r\n</DL><p>");

        var imported = await engine.ExecuteAsync<JsonElement>("bookmarks.import", new { file_path = htmlPath });
        Assert.Equal(1, imported.Data.GetProperty("links_created").GetInt32());

        var found = await engine.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://s.example" });
        Assert.Single(found);
        Assert.Equal("代理 &#xD800; 码点", found[0].Title);
    }

    [Fact]
    public async Task Import_Ignores_Tags_Inside_Comments()
    {
        var (engine, _, _) = TestHost.Create();
        var htmlPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbm_{Guid.NewGuid():N}.html");
        // 注释里的假 <DL>/<DT> 不应被当成真实标签；只有真实顶层 <DL> 算数
        await File.WriteAllTextAsync(htmlPath,
            "<!-- 假标签 <DL> 与 <DT></DT> 不应被解析 -->\r\n" +
            "<!DOCTYPE NETSCAPE-Bookmark-file-1>\r\n<DL><p>\r\n" +
            "<DT><H3 ADD_DATE=\"1600000000\">真实目录</H3>\r\n<DL><p>\r\n" +
            "<DT><A HREF=\"https://c.example\" ADD_DATE=\"1600000100\">C</A>\r\n</DL><p>\r\n</DL><p>");

        var inspection = await engine.QueryAsync<BookmarkFileInspectionDto>("bookmarks.inspect", new { file_path = htmlPath });
        Assert.True(inspection.IsValid);
        Assert.Equal(1, inspection.FolderCount);
        Assert.Equal(1, inspection.LinkCount);
    }

    [Fact]
    public async Task Export_Description_Uses_Crlf()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "F" });
        await engine.ExecuteAsync<LinkDto>("links.create", new
        {
            url = "https://d.example",
            title = "D",
            description = "desc 描述",
            list_id = folder.Data!.FolderId,
        });

        var exportPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbm_{Guid.NewGuid():N}.html");
        await engine.ExecuteAsync<object>("bookmarks.export", new { file_path = exportPath });
        var text = await File.ReadAllTextAsync(exportPath);

        // <DD> 行与全文一致用 CRLF（修复前混入孤立的 \n <DD>；\r\n 里天然含 \n，须用正则判「裸 \n」）
        Assert.Matches("\r\n        <DD>desc 描述\r\n", text);
        Assert.DoesNotMatch(@"(?<!\r)\n[ \t]*<DD>desc 描述", text);
    }

    [Fact]
    public async Task Import_Warnings_Flow_To_ChangeSet()
    {
        var (engine, _, _) = TestHost.Create();
        var htmlPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbm_{Guid.NewGuid():N}.html");
        // 缺顶层 </DL>：解析走容错 → 告警应经 ChangeSet.Warnings 上行（不能只留在 data.warnings）
        await File.WriteAllTextAsync(htmlPath,
            "<!DOCTYPE NETSCAPE-Bookmark-file-1>\r\n<DL><p>\r\n" +
            "<DT><A HREF=\"https://w.example\" ADD_DATE=\"1600000000\">W</A>");

        var imported = await engine.ExecuteAsync<JsonElement>("bookmarks.import", new { file_path = htmlPath });
        Assert.Equal(1, imported.Data.GetProperty("links_created").GetInt32());
        Assert.NotNull(imported.Changes!.Warnings);
        Assert.Contains(imported.Changes!.Warnings!, w => w.Contains("文件结构不完整"));
    }
}

public class BackupModuleTests
{
    [Fact]
    public async Task Export_Inspect_Import_TamperReject()
    {
        // —— 源库：A 文件夹 + X 书签；T 书签进回收站（回收站不进备份）——
        var (engine, _, _) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example", title = "X", list_id = folder.Data!.FolderId });
        var trashLink = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://t.example", title = "T" });
        await engine.ExecuteAsync<object>("links.trash", new { id = trashLink.Data!.LinkId });

        var backupPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbk_{Guid.NewGuid():N}.lpbackup");
        await engine.ExecuteAsync<object>("backup.export", new { output_path = backupPath });

        // backup.inspect：manifest + 完整性，零写入
        var inspect = await engine.QueryAsync<JsonElement>("backup.inspect", new { file_path = backupPath });
        Assert.True(inspect.GetProperty("valid").GetBoolean());
        Assert.Equal(1, inspect.GetProperty("total_folders").GetInt32());
        Assert.Equal(1, inspect.GetProperty("total_links").GetInt32());

        // —— 新库追加导入：Destructive 两阶段 ——
        var (engine2, _, _) = TestHost.Create();
        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            engine2.ExecuteAsync<object>("backup.import", new { file_path = backupPath, replace = false }));
        Assert.Equal(EngineErrors.ConfirmRequired, ex.Error.Code);
        var token = ex.Error.Details!.Value.GetProperty("confirm_token").GetString();
        await engine2.ExecuteAsync<object>("backup.import",
            new { file_path = backupPath, replace = false }, new CallOptions(ConfirmToken: token));

        var stats2 = await engine2.QueryAsync<LinkCountsDto>("links.stats", null);
        Assert.Equal(1, stats2.Total);   // 追加进空库 = 1

        // —— replace 导入：清空后导入（库内原有 1 条 → 重置为备份的 1 条，不是 2）——
        var ex2 = await Assert.ThrowsAsync<EngineException>(() =>
            engine2.ExecuteAsync<object>("backup.import", new { file_path = backupPath, replace = true }));
        var token2 = ex2.Error.Details!.Value.GetProperty("confirm_token").GetString();
        await engine2.ExecuteAsync<object>("backup.import",
            new { file_path = backupPath, replace = true }, new CallOptions(ConfirmToken: token2));

        var stats3 = await engine2.QueryAsync<LinkCountsDto>("links.stats", null);
        Assert.Equal(1, stats3.Total);
        Assert.Single(stats3.ByFolder);   // 文件夹 A 还原

        // —— 篡改拒绝 ——
        var tamperedPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbk_{Guid.NewGuid():N}.lpbackup");
        var bytes = await File.ReadAllBytesAsync(backupPath);
        bytes[^5] ^= 0xFF;
        await File.WriteAllBytesAsync(tamperedPath, bytes);
        var bad = await engine.QueryAsync<JsonElement>("backup.inspect", new { file_path = tamperedPath });
        Assert.False(bad.GetProperty("valid").GetBoolean());
    }
}

public class DedupModuleTests
{
    [Fact]
    public async Task Scan_Plan_Apply_Full_Flow()
    {
        var (engine, _, _) = TestHost.Create();
        var keep = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://dup.example", title = "Keep" });
        await engine.ExecuteAsync<object>("links.visit_record", new { id = keep.Data!.LinkId });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://dup.example", title = "V1" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://dup.example", title = "V2" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://unique.example", title = "U" });

        var groups = await engine.QueryAsync<List<Modules.Dedup.DedupGroup>>("dedup.scan", null);
        var group = Assert.Single(groups);
        Assert.Equal("https://dup.example", group.Url);
        Assert.Equal(3, group.Count);

        var plan = await engine.QueryAsync<Modules.Dedup.DedupPlan>("dedup.plan", null);
        Assert.Equal("keep_most_visited", plan.Strategy);
        Assert.Equal(2, plan.TotalToTrash);
        Assert.Equal(keep.Data!.LinkId, plan.Groups[0].Keep.LinkId);

        var applied = await engine.ExecuteAsync<JsonElement>("dedup.apply", null);
        Assert.Equal(2, applied.Data.GetProperty("trashed").GetInt32());
        Assert.Contains("trash.changed", applied.Changes!.Events);

        var stats = await engine.QueryAsync<LinkCountsDto>("links.stats", null);
        Assert.Equal(2, stats.Total);   // keep + unique

        // apply 后无重复组
        var plan2 = await engine.QueryAsync<Modules.Dedup.DedupPlan>("dedup.plan", new { strategy = "keep_newest" });
        Assert.Empty(plan2.Groups);
    }

    [Fact]
    public async Task Keep_Explicit_Skips_Groups_Not_Listed()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://a.example", title = "A1" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://a.example", title = "A2" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://b.example", title = "B1" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://b.example", title = "B2" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://b.example", title = "B3" });

        // explicit_keep 只列 a.example → 计划只含该组；b.example 组必须跳过（不回落到别的策略）
        var groups = await engine.QueryAsync<List<Modules.Dedup.DedupGroup>>("dedup.scan", null);
        var groupA = groups.Single(g => g.Url == "https://a.example");
        var explicitKeep = new Dictionary<string, string> { [groupA.Url] = groupA.Links[0].LinkId };

        var plan = await engine.QueryAsync<Modules.Dedup.DedupPlan>("dedup.plan",
            new { strategy = "keep_explicit", explicit_keep = explicitKeep });
        var planned = Assert.Single(plan.Groups);
        Assert.Equal(groupA.Url, planned.Url);
        Assert.Equal(groupA.Links.Count - 1, plan.TotalToTrash);

        var applied = await engine.ExecuteAsync<JsonElement>("dedup.apply",
            new { strategy = "keep_explicit", explicit_keep = explicitKeep });
        Assert.Equal(1, applied.Data.GetProperty("trashed").GetInt32());
        Assert.Contains("trash.changed", applied.Changes!.Events);

        // 未点名的 b.example 组原样保留（仍有重复），a.example 组已被处置
        var after = await engine.QueryAsync<List<Modules.Dedup.DedupGroup>>("dedup.scan", null);
        var remaining = Assert.Single(after);
        Assert.Equal("https://b.example", remaining.Url);
        Assert.Equal(3, remaining.Count);
    }

    [Fact]
    public async Task Group_Urls_With_Non_String_Element_Reports_TypeMismatch()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://dup.example", title = "X" });

        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            engine.QueryAsync<Modules.Dedup.DedupPlan>("dedup.plan", new { group_urls = new object[] { 1 } }));
        Assert.Equal(EngineErrors.TypeMismatch, ex.Error.Code);
    }

    [Fact]
    public async Task Apply_Without_Duplicates_Publishes_No_Events()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://unique.example", title = "U" });

        var applied = await engine.ExecuteAsync<JsonElement>("dedup.apply", null);
        Assert.Equal(0, applied.Data.GetProperty("trashed").GetInt32());
        // 空计划 = 零实际变更：不得发布 trash.changed（空事件会假失效缓存/假刷新）
        Assert.Empty(applied.Changes!.Events);
    }

    [Fact]
    public void Dedup_Apply_Descriptor_Is_Not_Reversible()
    {
        var (engine, _, _) = TestHost.Create();
        var manifest = engine.Describe("dedup");
        var apply = Assert.Single(manifest.Commands, c => c.Name == "dedup.apply");
        Assert.False(apply.Caps.HasFlag(CommandCaps.Reversible));
    }
}

public class BackupRobustnessTests
{
    [Fact]
    public async Task Import_Replace_Clears_Nested_Folders()
    {
        var (engine, _, _) = TestHost.Create();
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B", parent_id = a.Data!.FolderId });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example", title = "X", list_id = a.Data!.FolderId });

        var backupPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbk_{Guid.NewGuid():N}.lpbackup");
        await engine.ExecuteAsync<object>("backup.export", new { output_path = backupPath });

        // 目标库已有嵌套 C→D：replace 必须先清空（自引用 RESTRICT 由 ClearAllDataAsync 事务兜底）
        var (engine2, _, _) = TestHost.Create();
        var c = await engine2.ExecuteAsync<FolderDto>("folders.create", new { name = "C" });
        await engine2.ExecuteAsync<FolderDto>("folders.create", new { name = "D", parent_id = c.Data!.FolderId });

        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            engine2.ExecuteAsync<object>("backup.import", new { file_path = backupPath, replace = true }));
        Assert.Equal(EngineErrors.ConfirmRequired, ex.Error.Code);
        var token = ex.Error.Details!.Value.GetProperty("confirm_token").GetString();
        await engine2.ExecuteAsync<object>("backup.import",
            new { file_path = backupPath, replace = true }, new CallOptions(ConfirmToken: token));

        var tree = await engine2.QueryAsync<List<FolderDto>>("folders.tree", null);
        Assert.Equal(2, tree.Count);
        Assert.All(tree, f => Assert.NotEqual("C", f.Name));   // 旧嵌套数据已清空
        Assert.Contains(tree, f => f.Name == "A");
    }

    [Fact]
    public async Task Import_Malformed_Data_Json_Reports_InvalidPath()
    {
        var (engine, _, _) = TestHost.Create();
        var badPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbk_{Guid.NewGuid():N}.lpbackup");
        BuildBackup(badPath, System.Text.Encoding.UTF8.GetBytes("this is not json"));

        // SHA 匹配但 data.json 非合法 JSON → 明确 InvalidPath，而非内部错误（Destructive 先取确认令牌）
        var gate = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExecuteAsync<object>("backup.import", new { file_path = badPath }));
        Assert.Equal(EngineErrors.ConfirmRequired, gate.Error.Code);
        var token = gate.Error.Details!.Value.GetProperty("confirm_token").GetString();
        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExecuteAsync<object>("backup.import",
                new { file_path = badPath }, new CallOptions(ConfirmToken: token)));
        Assert.Equal(EngineErrors.InvalidPath, ex.Error.Code);
        Assert.Contains("data.json", ex.Error.Message);
    }

    [Fact]
    public async Task Import_Duplicate_Folder_Keys_Rejected()
    {
        var (engine, _, _) = TestHost.Create();
        var badPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbk_{Guid.NewGuid():N}.lpbackup");
        var data = """{"folders":[{"key":"f1","name":"甲","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"},{"key":"f1","name":"乙","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}],"links":[]}""";
        BuildBackup(badPath, System.Text.Encoding.UTF8.GetBytes(data));

        var gate = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExecuteAsync<object>("backup.import", new { file_path = badPath }));
        var token = gate.Error.Details!.Value.GetProperty("confirm_token").GetString();
        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExecuteAsync<object>("backup.import",
                new { file_path = badPath }, new CallOptions(ConfirmToken: token)));
        Assert.Equal(EngineErrors.InvalidPath, ex.Error.Code);
        Assert.Contains("重复的文件夹 key", ex.Error.Message);
    }

    private static void BuildBackup(string path, byte[] dataBytes)
    {
        using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        var dataEntry = archive.CreateEntry("data.json");
        using (var s = dataEntry.Open()) s.Write(dataBytes, 0, dataBytes.Length);

        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dataBytes)).ToLowerInvariant();
        var manifest = $"{{\"version\":\"2.0\",\"data_sha256\":\"{sha}\",\"statistics\":{{\"total_folders\":0,\"total_links\":0}}}}";
        var manifestEntry = archive.CreateEntry("manifest.json");
        var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifest);
        using (var s = manifestEntry.Open()) s.Write(manifestBytes, 0, manifestBytes.Length);
    }
}

public class MaintenanceModuleTests
{
    [Fact]
    public async Task Schema_Version_And_Diagnostics()
    {
        var (engine, _, _) = TestHost.Create();
        var version = await engine.QueryAsync<JsonElement>("maintenance.schema_version", null);
        // 全新建库 = 完整版本链（v2 基线 + v3 索引复核），版本表落最高版本
        Assert.Equal(3, version.GetProperty("schema_version").GetInt32());

        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        var diag = await engine.QueryAsync<JsonElement>("diagnostics.collect", null);
        Assert.Equal(1, diag.GetProperty("counts").GetProperty("folders").GetInt32());

        // runtime 段（阶段 12）：组合根已接线 → 必须是引擎真实读数而不是 null/假值
        var runtime = diag.GetProperty("runtime");
        Assert.Equal(JsonValueKind.Object, runtime.ValueKind);
        Assert.Equal(0L, runtime.GetProperty("cache_hits").GetInt64());
        Assert.Equal(0L, runtime.GetProperty("cache_entries").GetInt64());
        Assert.True(runtime.GetProperty("event_store_head").GetInt64() > 0, "建文件夹后事件存储游标应已推进");
    }

    [Fact]
    public async Task Reinit_Two_Phase_Clears_Everything()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        var link = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example", title = "X" });
        await engine.ExecuteAsync<object>("links.trash", new { id = link.Data!.LinkId });

        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<object>("maintenance.reinit", null));
        Assert.Equal(EngineErrors.ConfirmRequired, ex.Error.Code);
        var token = ex.Error.Details!.Value.GetProperty("confirm_token").GetString();

        await engine.ExecuteAsync<object>("maintenance.reinit", null, new CallOptions(ConfirmToken: token));

        var diag = await engine.QueryAsync<JsonElement>("diagnostics.collect", null);
        Assert.Equal(0, diag.GetProperty("counts").GetProperty("folders").GetInt32());
        Assert.Equal(0, diag.GetProperty("counts").GetProperty("links").GetInt32());
        Assert.Equal(0, diag.GetProperty("counts").GetProperty("trash_links").GetInt32());
    }

    [Fact]
    public async Task Reinit_Clears_Nested_Folder_Tree()
    {
        var (engine, _, _) = TestHost.Create();
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B", parent_id = a.Data!.FolderId });

        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<object>("maintenance.reinit", null));
        var token = ex.Error.Details!.Value.GetProperty("confirm_token").GetString();
        await engine.ExecuteAsync<object>("maintenance.reinit", null, new CallOptions(ConfirmToken: token));

        var diag = await engine.QueryAsync<JsonElement>("diagnostics.collect", null);
        Assert.Equal(0, diag.GetProperty("counts").GetProperty("folders").GetInt32());
    }

    /// <summary>
    /// 干跑零副作用：整库重置走批量删除（ExecuteDelete 绕过变更跟踪），
    /// 若引擎没在干跑时先开事务，删除会立刻落库 → 这条断言就是那个护栏。
    /// </summary>
    [Fact]
    public async Task Reinit_DryRun_Clears_Nothing()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        var link = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example", title = "X" });
        await engine.ExecuteAsync<object>("links.trash", new { id = link.Data!.LinkId });
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B" });

        var dry = await engine.ExecuteAsync<JsonElement>(
            "maintenance.reinit", null, new CallOptions(DryRun: true));
        Assert.True(dry.Data.GetProperty("cleared").GetBoolean());

        var diag = await engine.QueryAsync<JsonElement>("diagnostics.collect", null);
        Assert.Equal(2, diag.GetProperty("counts").GetProperty("folders").GetInt32());
        Assert.Equal(1, diag.GetProperty("counts").GetProperty("trash_links").GetInt32());
    }
}

public class FaviconModuleTests
{
    [Fact]
    public async Task CacheStats_Reports_Directory()
    {
        var (engine, _, _) = TestHost.Create();
        var stats = await engine.QueryAsync<JsonElement>("favicon.cache_stats", null);
        Assert.True(stats.GetProperty("file_count").GetInt32() >= 0);
        Assert.EndsWith("favicons", stats.GetProperty("cache_directory").GetString()!);
    }
}
