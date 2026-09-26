using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using LinkPocket.Kernel;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>目录自描述：60 个命令全部注册、无重复、查询/变更分类正确。</summary>
public class CatalogTests
{
    [Fact]
    public void Describe_Returns_All_62_Commands()
    {
        var (engine, _, _) = TestHost.Create();
        var manifest = engine.Describe();

        Assert.Equal(62, manifest.Commands.Count);
        Assert.Equal(62, manifest.Commands.Select(c => c.Name).Distinct().Count());
        Assert.All(manifest.Commands, c => Assert.Matches(@"^[a-z_]+\.[a-z_]+$", c.Name));
    }

    [Fact]
    public void Describe_By_Category_Splits_Correctly()
    {
        var (engine, _, _) = TestHost.Create();
        Assert.Equal(15, engine.Describe("folders").Commands.Count);  // + folders.tree_links（树叶子懒加载的轻量出口）
        Assert.Equal(16, engine.Describe("links").Commands.Count);
        Assert.Equal(9, engine.Describe("trash").Commands.Count);  // + trash.restore_unit（还原）/ trash.overview
        Assert.Equal(3, engine.Describe("maintenance").Commands.Count);
        Assert.Single(engine.Describe("locate").Commands);
        Assert.Equal(2, engine.Describe("audit").Commands.Count);  // audit.query / audit.prune
        Assert.Equal(2, engine.Describe("logs").Commands.Count);   // logs.query / logs.level

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
        Assert.Equal(FolderIds.RootToken, contents.FolderName);
        Assert.Single(contents.SubFolders);
        Assert.Equal("工作", contents.SubFolders[0].Name);
        Assert.Equal(new[] { FolderIds.RootToken }, contents.Breadcrumb);

        var child = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "资料", parent_id = created.Data!.FolderId });
        var breadcrumb = await engine.QueryAsync<List<string>>("folders.breadcrumb", new { folder_id = child.Data!.FolderId });
        Assert.Equal(new[] { FolderIds.RootToken, "工作", "资料" }, breadcrumb);
    }

    [Fact]
    public async Task 根级保留名被拒_子级同名放行()
    {
        // 各语言的根显示名登记进契约层（真实启动由 App 做；这里只喂保留名判据）

        var (engine, _, _) = TestHost.Create();
        foreach (var taken in new[] { "@root", "Bookmarks", "回收站", "Trash", "@root", "@trash" })
        {
            var err = await Assert.ThrowsAsync<EngineException>(
                () => engine.ExecuteAsync<FolderDto>("folders.create", new { name = taken }));
            Assert.Equal(EngineErrors.ReservedName, err.Error.Code);
        }

        // 子级不受限：只有根级会被虚根占用名挡住
        var parent = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        var child = await engine.ExecuteAsync<FolderDto>(
            "folders.create", new { name = "@root", parent_id = parent.Data!.FolderId });
        Assert.Equal("@root", child.Data!.Name);

        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", null);
        Assert.Single(contents.SubFolders);   // 根级一枚也没建出来
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

        // 全库链接**不再随 overview 搬运**（每次刷新搬全库是实测的性能黑洞）：
        // 目录树的叶子改用轻量出口 folders.tree_links（只带 id / 标题 / 地址 / 归属），按节点展开时取
        Assert.Null(overview.TreeLinks);
        var leaves = await engine.QueryAsync<List<TreeLinkDto>>("folders.tree_links", new { folder_id = folder.Data!.FolderId });
        Assert.Single(leaves);
        Assert.Equal("内部链接", leaves[0].Title);
        Assert.Equal(folder.Data!.FolderId, leaves[0].ListId);
        var rootLeaves = await engine.QueryAsync<List<TreeLinkDto>>("folders.tree_links", null);
        Assert.Contains(rootLeaves, l => l.Title == "根级链接" && l.ListId == null);

        // 根级计数与 links.stats.RootLevel 同口径；根分支复用目录页计数
        var stats = await engine.QueryAsync<LinkCountsDto>("links.stats", null);
        var rootOverview = await engine.QueryAsync<FolderContentsDto>("folders.overview", null);
        Assert.Equal(stats.RootLevel, rootOverview.RootLinkCount);
        Assert.Equal(rootOverview.DirectLinkCount, rootOverview.RootLinkCount);

        // 契约：folders.contents 响应形状不变（tree/root_link_count 恒 null）
        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", new { folder_id = folder.Data!.FolderId });
        Assert.Null(contents.Tree);
        Assert.Null(contents.TreeLinks);
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
        Assert.Contains(unit, e => e.EntryType == "link" && e.OriginPath == "@root/A/B");
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
        var dest = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "归档" });
        var staging = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "中转" });
        // 同层唯一（v4）：同名只能存在于不同目录 → 两个「工作」分别建在根级与「中转」下
        var source1 = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        var source2 = await engine.ExecuteAsync<FolderDto>(
            "folders.create", new { name = "工作", parent_id = staging.Data!.FolderId });

        var result = await engine.ExecuteAsync<FolderMoveBatchResult>(
            "folders.move_batch", new { folder_ids = new[] { source1.Data!.FolderId, source2.Data!.FolderId }, target_parent_id = dest.Data!.FolderId });
        Assert.Equal(2, result.Data!.Moved);
        Assert.Single(result.Data!.RenamedNotes);

        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", new { folder_id = dest.Data!.FolderId });
        var names = contents.SubFolders.Select(f => f.Name).ToList();
        Assert.Contains("工作", names);
        Assert.Contains("工作 (2)", names);
    }

    /// <summary>同层唯一命名（v4）：新建/改名/移动/复制四条写名路径都必须自动编号，且不同目录可同名。</summary>
    [Fact]
    public async Task Sibling_Names_Are_Unique_Across_All_Write_Paths()
    {
        var (engine, _, _) = TestHost.Create();

        // ① 新建：同层撞名 → 「(2)」
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        var b = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        Assert.Equal("工作", a.Data!.Name);
        Assert.Equal("工作 (2)", b.Data!.Name);

        // ② 改名：改成自己已有的名字 = 不变（自身不算占用者）；撞上别人 → 继续编号
        var renamedSelf = await engine.ExecuteAsync<FolderDto>("folders.update", new { folder_id = a.Data!.FolderId, name = "工作" });
        Assert.Equal("工作", renamedSelf.Data!.Name);
        var c = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "资料" });
        var renamed = await engine.ExecuteAsync<FolderDto>("folders.update", new { folder_id = c.Data!.FolderId, name = "工作" });
        Assert.Equal("工作 (3)", renamed.Data!.Name);   // 根级已有「工作」「工作 (2)」

        // ③ 移动：源名在目标层已被占用 → 「(2)」（空闲则保持原名，编号只在真冲突时发生）
        var staging = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "中转" });
        var other = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作", parent_id = staging.Data!.FolderId });
        var dest = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "归档" });
        var inside = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作", parent_id = dest.Data!.FolderId });
        var moved = await engine.ExecuteAsync<FolderDto>("folders.move", new { folder_id = other.Data!.FolderId, target_parent_id = dest.Data!.FolderId });
        Assert.Equal("工作 (2)", moved.Data!.Name);

        // ④ 复制：目标层撞名 → 「(3)」（目标层已有「工作」「工作 (2)」）
        var copy = await engine.ExecuteAsync<FolderCopyResult>(
            "folders.copy", new { folder_id = inside.Data!.FolderId, target_parent_id = dest.Data!.FolderId });
        var copied = await engine.QueryAsync<FolderDto>("folders.get", new { folder_id = copy.Data!.NewFolderId });
        Assert.Equal("工作 (3)", copied.Name);
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
        Assert.Equal("@root/F", trashed.Data!.OriginPath);
        Assert.Contains("trash.changed", trashed.Changes!.Events);

        var restored = await engine.ExecuteAsync<TrashRestoreResult>("trash.restore", new { id = link.Data!.LinkId });
        Assert.Equal(link.Data!.LinkId, restored.Data!.LinkId);
        Assert.False(restored.Data!.FellBackToRoot);

        var got = await engine.QueryAsync<LinkDto>("links.get", new { id = link.Data!.LinkId });
        Assert.Equal(folder.Data!.FolderId, got.ListId);   // 缺省 to = origin：落回删除前所在目录
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
            "trash.restore", new { id = link.Data!.LinkId, to = "origin" });
        Assert.False(restored.Data!.FellBackToRoot);
        Assert.Equal(folder.Data!.FolderId, restored.Data!.ListId);

        var got = await engine.QueryAsync<LinkDto>("links.get", new { id = link.Data!.LinkId });
        Assert.Equal(folder.Data!.FolderId, got.ListId);

        // 原目录已不存在 → 回落根，且结果如实回报（不假装还原到了原位）
        await engine.ExecuteAsync<LinkTrashResult>("links.trash", new { id = link.Data!.LinkId });
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = folder.Data!.FolderId });
        var fallback = await engine.ExecuteAsync<TrashRestoreResult>(
            "trash.restore", new { id = link.Data!.LinkId, to = "origin" });
        Assert.True(fallback.Data!.FellBackToRoot);
        Assert.Null(fallback.Data!.ListId);

        // to = "root" 显式落根；to 越界 → LP.VAL.003（校验先于存在性检查）
        await engine.ExecuteAsync<LinkTrashResult>("links.trash", new { id = link.Data!.LinkId });
        var toRoot = await engine.ExecuteAsync<TrashRestoreResult>(
            "trash.restore", new { id = link.Data!.LinkId, to = "root" });
        Assert.False(toRoot.Data!.FellBackToRoot);
        Assert.Null(toRoot.Data!.ListId);
        var bad = await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<TrashRestoreResult>(
            "trash.restore", new { id = link.Data!.LinkId, to = "everywhere" }));
        Assert.Equal(EngineErrors.EnumOutOfRange, bad.Error.Code);
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
        Assert.Contains("metadata not fetched", created.Changes!.HumanSummary);

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
    public async Task RestoreBatch_Mixed_Links_And_Units()
    {
        var (engine, _, _) = TestHost.Create();
        var home = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" })).Data!;
        var l1 = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://1.example", title = "1", list_id = home.FolderId })).Data!;
        var l2 = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://2.example", title = "2" })).Data!;
        await engine.ExecuteAsync<object>("links.trash", new { id = l1.LinkId });
        await engine.ExecuteAsync<object>("links.trash", new { id = l2.LinkId });

        // 单元：整目录删除（含 1 链接）
        var doomed = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "资料" })).Data!;
        var inner = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://in.example", title = "内", list_id = doomed.FolderId })).Data!;
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = doomed.FolderId });

        var restored = await engine.ExecuteAsync<TrashRestoreBatchResult>("trash.restore_batch",
            new { link_ids = new[] { l1.LinkId, l2.LinkId }, folder_ids = new[] { doomed.FolderId } });
        Assert.Equal(3, restored.Data!.RestoredLinks);      // 2 独立 + 1 随单元
        Assert.Equal(1, restored.Data!.RestoredUnits);
        Assert.Equal(1, restored.Data!.RestoredFolders);
        // C6：一条 ChangeSet 齐发三事件（两页各自 300ms 防抖只刷一次）
        Assert.Contains("folders.changed", restored.Changes!.Events);
        Assert.Contains("links.changed", restored.Changes!.Events);
        Assert.Contains("trash.changed", restored.Changes!.Events);
        Assert.Empty(restored.Data!.FellBackToRoot);
        Assert.Empty(restored.Data!.Renamed);
        Assert.Equal(0, restored.Data!.DuplicateUrls);

        // 缺省回原位置：l1 回「工作」、l2 回根、单元回根、夹内链接回夹
        Assert.Equal(home.FolderId, (await engine.QueryAsync<LinkDto>("links.get", new { id = l1.LinkId })).ListId);
        Assert.Null((await engine.QueryAsync<LinkDto>("links.get", new { id = l2.LinkId })).ListId);
        Assert.Contains(await engine.QueryAsync<List<FolderDto>>("folders.tree", null),
            f => f.FolderId == doomed.FolderId && f.ParentId == null);
        Assert.Equal(doomed.FolderId, (await engine.QueryAsync<LinkDto>("links.get", new { id = inner.LinkId })).ListId);
        Assert.Empty(await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null));

        // 空批量 → LP.VAL.001
        var empty = await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<TrashRestoreBatchResult>(
            "trash.restore_batch", new { link_ids = Array.Empty<string>(), folder_ids = Array.Empty<string>() }));
        Assert.Equal(EngineErrors.RequiredParam, empty.Error.Code);
    }

    [Fact]
    public async Task RestoreBatch_Link_Returns_Into_Same_Batch_Unit()
    {
        // C2：链接 origin = 同批还原的单元 → 先单元后链接 → 按 ID 落回该目录
        var (engine, _, _) = TestHost.Create();
        var unit = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "U" })).Data!;
        var link = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://u.example", title = "U", list_id = unit.FolderId })).Data!;
        await engine.ExecuteAsync<object>("links.trash", new { id = link.LinkId });
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = unit.FolderId });

        var restored = await engine.ExecuteAsync<TrashRestoreBatchResult>("trash.restore_batch",
            new { link_ids = new[] { link.LinkId }, folder_ids = new[] { unit.FolderId } });
        Assert.Equal(1, restored.Data!.RestoredLinks);
        Assert.Equal(unit.FolderId, (await engine.QueryAsync<LinkDto>("links.get", new { id = link.LinkId })).ListId);
    }

    [Fact]
    public async Task Restore_Link_From_Inside_Unit_Falls_Back_To_Root()
    {
        // A12：链接在单元内、单独还原"到原位置"——原目录（所属单元）在回收站 → 落根 + 如实回报
        var (engine, _, _) = TestHost.Create();
        var unit = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "单元" })).Data!;
        var link = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://a12.example", title = "内链", list_id = unit.FolderId })).Data!;
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = unit.FolderId });

        var restored = await engine.ExecuteAsync<TrashRestoreResult>("trash.restore", new { id = link.LinkId });
        Assert.True(restored.Data!.FellBackToRoot);
        Assert.Null(restored.Data!.ListId);
        Assert.Contains(await engine.QueryAsync<List<TrashFolderDto>>("trash.tree", null),
            t => t.TrashFolderId == unit.FolderId);   // 单元不受影响
    }

    [Fact]
    public async Task RestoreBatch_Two_Same_Name_Units_Numbered()
    {
        // B12：批内两个同名单元共享占用表 → 「资料」「资料 (2)」
        var (engine, _, _) = TestHost.Create();
        var u1 = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "资料" })).Data!;
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = u1.FolderId });
        var u2 = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "资料" })).Data!;
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = u2.FolderId });

        var restored = await engine.ExecuteAsync<TrashRestoreBatchResult>("trash.restore_batch",
            new { folder_ids = new[] { u1.FolderId, u2.FolderId } });
        Assert.Equal(2, restored.Data!.RestoredUnits);
        var rename = Assert.Single(restored.Data!.Renamed);
        Assert.Equal("资料", rename.From);
        Assert.Equal("资料 (2)", rename.To);

        var root = await engine.QueryAsync<FolderContentsDto>("folders.contents", null);
        var names = root.SubFolders.Select(f => f.Name).ToList();
        Assert.Contains("资料", names);
        Assert.Contains("资料 (2)", names);
    }

    [Fact]
    public async Task RestoreBatch_Reports_Duplicate_Urls()
    {
        // D7：落点已有同 URL 链接 → 计数如实提示（不阻断、不合并、忠实保留重复）
        var (engine, _, _) = TestHost.Create();
        var link = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://dup.example", title = "原" })).Data!;
        await engine.ExecuteAsync<object>("links.trash", new { id = link.LinkId });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://dup.example", title = "已在根" });

        var restored = await engine.ExecuteAsync<TrashRestoreBatchResult>("trash.restore_batch",
            new { link_ids = new[] { link.LinkId } });
        Assert.Equal(1, restored.Data!.RestoredLinks);
        Assert.Equal(1, restored.Data!.DuplicateUrls);
        var stats = await engine.QueryAsync<LinkCountsDto>("links.stats", null);
        Assert.Equal(2, stats.Total);
    }

    [Fact]
    public async Task RestoreBatch_Missing_Id_Fails_Whole_Batch()
    {
        // C3：任一项不存在 → 硬失败（预检先于任何变更）+ 指明失败 id
        var (engine, _, _) = TestHost.Create();
        var link = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://rollback.example", title = "R" })).Data!;
        await engine.ExecuteAsync<object>("links.trash", new { id = link.LinkId });

        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<TrashRestoreBatchResult>(
            "trash.restore_batch", new { link_ids = new[] { link.LinkId, "no-such-id" } }));
        Assert.Equal(EngineErrors.EntityNotFound, ex.Error.Code);
        Assert.Contains("no-such-id", ex.Error.Message);

        Assert.Contains(await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null), e => e.Id == link.LinkId);
        Assert.Equal(0, (await engine.QueryAsync<LinkCountsDto>("links.stats", null)).Total);
    }

    [Fact]
    public async Task RestoreBatch_SubUnit_Conflict_Rolls_Back_Whole_Batch()
    {
        // 坏数据防御：处理中途发现主表已有同 ID（子单元）→ 硬失败 → 已还原的项整批回滚，绝不覆盖既有实体
        var (engine, factory, _) = TestHost.Create();
        var parent = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "父单元" })).Data!;
        var child = (await engine.ExecuteAsync<FolderDto>("folders.create",
            new { name = "子单元", parent_id = parent.FolderId })).Data!;
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = parent.FolderId });

        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Add(new LinkPocket.Data.Folder { FolderId = child.FolderId, Name = "冒名子" });
            await ctx.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<TrashRestoreBatchResult>(
            "trash.restore_batch", new { folder_ids = new[] { parent.FolderId } }));
        Assert.Equal(EngineErrors.DbError, ex.Error.Code);
        Assert.Contains(child.FolderId, ex.Error.Message);

        // 整批回滚：父未落地、单元仍在回收站、坏数据行未被覆盖
        var tree = await engine.QueryAsync<List<FolderDto>>("folders.tree", null);
        Assert.DoesNotContain(tree, f => f.FolderId == parent.FolderId);
        Assert.Contains(tree, f => f.Name == "冒名子");
        Assert.Contains(await engine.QueryAsync<List<TrashFolderDto>>("trash.tree", null),
            t => t.TrashFolderId == parent.FolderId);
    }

    [Fact]
    public async Task RestoreUnit_Explicit_Target_And_To_Mutual_Exclusion()
    {
        var (engine, _, _) = TestHost.Create();
        var dest = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "目标" })).Data!;
        var doomed = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "单元" })).Data!;
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = doomed.FolderId });

        // 「to」与「target_parent_id」互斥 → LP.VAL.002
        var exclusive = await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<object>(
            "trash.restore_unit", new { unit_id = doomed.FolderId, to = "origin", target_parent_id = dest.FolderId }));
        Assert.Equal(EngineErrors.TypeMismatch, exclusive.Error.Code);

        // 显式落点生效（不回原位置=根，而是落进「目标」）
        var restored = await engine.ExecuteAsync<LinkPocket.Modules.Trash.TrashRestoreUnitResult>(
            "trash.restore_unit", new { unit_id = doomed.FolderId, target_parent_id = dest.FolderId });
        Assert.Equal(dest.FolderId, restored.Data!.Landing);
        Assert.False(restored.Data!.FellBackToRoot);

        // 显式落点不存在 → LP.STATE.001（错就报错，不静默改落点）
        var doomed2 = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "单元2" })).Data!;
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = doomed2.FolderId });
        var missing = await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<object>(
            "trash.restore_unit", new { unit_id = doomed2.FolderId, target_parent_id = "no-such-folder" }));
        Assert.Equal(EngineErrors.EntityNotFound, missing.Error.Code);
    }

    [Fact]
    public async Task RestoreUnit_Step_By_Step_Parent_Then_Child_Lands_Inside()
    {
        // B7：先删子、后删父 → 先还原父、再还原子（原父已在主表）→ 子按 ID 落回父内
        var (engine, _, _) = TestHost.Create();
        var parent = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "P" })).Data!;
        var child = (await engine.ExecuteAsync<FolderDto>("folders.create",
            new { name = "A", parent_id = parent.FolderId })).Data!;
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = child.FolderId });    // 单元 A（原父 = P）
        await engine.ExecuteAsync<object>("folders.delete", new { folder_id = parent.FolderId });   // 单元 P

        await engine.ExecuteAsync<LinkPocket.Modules.Trash.TrashRestoreUnitResult>(
            "trash.restore_unit", new { unit_id = parent.FolderId });
        var restored = await engine.ExecuteAsync<LinkPocket.Modules.Trash.TrashRestoreUnitResult>(
            "trash.restore_unit", new { unit_id = child.FolderId });

        Assert.Equal(parent.FolderId, restored.Data!.Landing);
        var tree = await engine.QueryAsync<List<FolderDto>>("folders.tree", null);
        Assert.Equal(parent.FolderId, tree.Single(f => f.FolderId == child.FolderId).ParentId);
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

    /// <summary>导入撞名（与既有文件夹同名）→ **自动编号**并如实报告数量——绝不整包失败。</summary>
    [Fact]
    public async Task Import_Same_Name_Folder_Is_Auto_Numbered()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "Folder <One>" });   // 与样例文件里的文件夹同名

        var htmlPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbm_{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(htmlPath, SampleHtml);

        var imported = await engine.ExecuteAsync<JsonElement>("bookmarks.import", new { file_path = htmlPath });

        Assert.Equal(1, imported.Data.GetProperty("folders_created").GetInt32());
        Assert.Equal(1, imported.Data.GetProperty("folders_renamed").GetInt32());
        Assert.Contains("same-named entries were auto-numbered", imported.Changes!.HumanSummary);

        var tree = await engine.QueryAsync<List<FolderDto>>("folders.tree", null);
        Assert.Contains(tree, f => f.Name == "Folder <One>");
        Assert.Contains(tree, f => f.Name == "Folder <One> (2)");
        // 层级未串位：文件里的 Inner 书签落进**编号后**的那个文件夹
        var inner = (await engine.QueryAsync<List<LinkDto>>(
            "links.find_by_url", new { url = "https://inner.example" })).Single();
        Assert.Equal(tree.Single(f => f.Name == "Folder <One> (2)").FolderId, inner.ListId);
    }

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
        Assert.Contains(imported.Changes!.Warnings!, w => w.Contains("structure is incomplete"));
    }
}

public class BackupModuleTests
{
    /// <summary>
    /// 增量导入撞名 → **自动编号**而非整包失败（v4 唯一索引下的必现路径：
    /// 备份是外部输入，增量模式与既有数据共存；不编号会让唯一索引拒绝整个导入）。
    /// 同时如实报告编号数量（可观测：folders_renamed）。
    /// </summary>
    [Fact]
    public async Task Incremental_Import_Auto_Numbers_Same_Name_Folders()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://x.example", title = "X", list_id = folder.Data!.FolderId });

        var backupPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbk_{Guid.NewGuid():N}.lpbackup");
        await engine.ExecuteAsync<object>("backup.export", new { output_path = backupPath });

        // 增量导入到**同一个库**：备份里的「工作」与库里的「工作」撞名
        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExecuteAsync<object>("backup.import", new { file_path = backupPath, replace = false }));
        var token = ex.Error.Details!.Value.GetProperty("confirm_token").GetString();
        var imported = await engine.ExecuteAsync<JsonElement>("backup.import",
            new { file_path = backupPath, replace = false }, new CallOptions(ConfirmToken: token));

        Assert.Equal(1, imported.Data.GetProperty("folders_created").GetInt32());
        Assert.Equal(1, imported.Data.GetProperty("folders_renamed").GetInt32());
        Assert.Contains("same-named entries were auto-numbered", imported.Changes!.HumanSummary);

        var tree = await engine.QueryAsync<List<FolderDto>>("folders.tree", null);
        Assert.Contains(tree, f => f.Name == "工作");
        Assert.Contains(tree, f => f.Name == "工作 (2)");
        // 书签未串位：新导入的链接落在编号后的那个文件夹里（两个「工作」各 1 条）
        var stats = await engine.QueryAsync<LinkCountsDto>("links.stats", null);
        Assert.Equal(2, stats.Total);
        Assert.Equal(2, stats.ByFolder.Count);
    }

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
        Assert.Contains("duplicate folder key", ex.Error.Message);
    }

    /// <summary>
    /// 造一个包供导入路径做负面测试。
    /// </summary>
    /// <param name="version">manifest 的 <c>version</c> 字段；缺省 = **当前格式标识**
    /// <c>lpbackup/2.0</c>（硬编码成 "2.0" 会让用例在版本收严之后测不到自己那条规则）。</param>
    private static void BuildBackup(string path, byte[] dataBytes, string version = "lpbackup/2.0")
    {
        using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        var dataEntry = archive.CreateEntry("data.json");
        using (var s = dataEntry.Open()) s.Write(dataBytes, 0, dataBytes.Length);

        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dataBytes)).ToLowerInvariant();
        var manifest = $"{{\"version\":\"{version}\",\"data_sha256\":\"{sha}\",\"statistics\":{{\"total_folders\":0,\"total_links\":0}}}}";
        var manifestEntry = archive.CreateEntry("manifest.json");
        var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifest);
        using (var s = manifestEntry.Open()) s.Write(manifestBytes, 0, manifestBytes.Length);
    }

    /// <summary>导入前先过破坏性确认门（两阶段）拿到令牌，再执行并断言抛出的错误。</summary>
    private static async Task<EngineException> ImportExpectingFailureAsync(
        IEngine engine, string path, bool replace = false)
    {
        var gate = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExecuteAsync<object>("backup.import", new { file_path = path, replace }));
        Assert.Equal(EngineErrors.ConfirmRequired, gate.Error.Code);
        var token = gate.Error.Details!.Value.GetProperty("confirm_token").GetString();
        return await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExecuteAsync<object>("backup.import", new { file_path = path, replace },
                new CallOptions(ConfirmToken: token)));
    }

    [Theory]
    [InlineData("2.0")]              // 历史标识：现在必须拒绝（前缀宽松匹配是缺陷）
    [InlineData("2")]                // 缺家族名
    [InlineData("lpbackup/3.0")]     // 更高主版本（本实现还没有 3.x 的读法）
    [InlineData("lpbackup/1.0")]     // 更旧的主版本：本实现没有 1.x 的升级路径 ⇒ 拒绝
                                     //（加主版本时把旧主版本加进 `BackupIO.ReadableMajors` 并实现升级换算 —— 备份是跨版本迁移通道）
    [InlineData("lpbackup/2.1")]     // 更高次版本（本实现读不了更新的包）
    [InlineData("lpbackup/2")]       // 缺次版本 → 视作 2.0，可读
    [InlineData("lpbackup/2.0.1")]   // 三段式非法
    [InlineData("")]
    public async Task Backup_Format_Version_Gate(string version)
    {
        var (engine, _, _) = TestHost.Create();
        var path = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbk_{Guid.NewGuid():N}.lpbackup");
        var data = """{"folders":[],"links":[]}""";
        BuildBackup(path, System.Text.Encoding.UTF8.GetBytes(data), version);

        var accepted = version is "lpbackup/2" or "lpbackup/2.0";
        if (accepted)
        {
            var gate = await Assert.ThrowsAsync<EngineException>(() =>
                engine.ExecuteAsync<object>("backup.import", new { file_path = path }));
            var token = gate.Error.Details!.Value.GetProperty("confirm_token").GetString();
            var ok = await engine.ExecuteAsync<object>("backup.import",
                new { file_path = path }, new CallOptions(ConfirmToken: token));
            Assert.True(ok.Ok, $"版本「{version}」应可读");
        }
        else
        {
            var ex = await ImportExpectingFailureAsync(engine, path);
            Assert.Equal(EngineErrors.InvalidPath, ex.Error.Code);
            Assert.Contains("unsupported backup version", ex.Error.Message);
        }
    }

    [Fact]
    public async Task Import_Unknown_Parent_Key_Rejected_And_Nothing_Written()
    {
        // 外部输入的references are incomplete = 层级会静默被拍平（旧实现回落根级）→ 现行整包拒绝，且**一个字节都不写库**。
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "既有" });

        var path = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbk_{Guid.NewGuid():N}.lpbackup");
        var data = """
        {"folders":[{"key":"f1","name":"孤儿","parent":"f404","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}],
         "links":[{"folder":"f404","url":"https://x.example","title":"X","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}]}
        """;
        BuildBackup(path, System.Text.Encoding.UTF8.GetBytes(data));

        var ex = await ImportExpectingFailureAsync(engine, path);
        Assert.Equal(EngineErrors.InvalidPath, ex.Error.Code);
        Assert.Contains("references are incomplete", ex.Error.Message);

        var tree = await engine.QueryAsync<List<FolderDto>>("folders.tree", null);
        Assert.Single(tree);                       // 只有既有那个，孤儿没被拍平落根
        Assert.Equal("既有", tree[0].Name);
    }

    [Fact]
    public async Task Import_Malformed_Timestamp_Rejected()
    {
        // 旧实现解析失败静默回落 DateTime.UtcNow（把损坏数据伪装成"刚刚创建"）→ 现行明确拒绝。
        var (engine, _, _) = TestHost.Create();
        var path = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbk_{Guid.NewGuid():N}.lpbackup");
        var data = """
        {"folders":[{"key":"f1","name":"甲","created_at":"昨天","updated_at":"2026-01-01T00:00:00Z"}],"links":[]}
        """;
        BuildBackup(path, System.Text.Encoding.UTF8.GetBytes(data));

        var ex = await ImportExpectingFailureAsync(engine, path);
        Assert.Equal(EngineErrors.InvalidPath, ex.Error.Code);
        Assert.Contains("unparseable timestamp", ex.Error.Message);
        Assert.Empty((await engine.QueryAsync<List<FolderDto>>("folders.tree", null)));
    }

    [Fact]
    public async Task Export_Overwrites_Existing_Backup_Atomically()
    {
        // 导出必须能在**目标文件已存在**时原子覆盖，且不留临时文件
        // （旧实现先 File.Delete 再打包：打包失败 = 旧备份与新备份同时丢失）。
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        var path = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbk_{Guid.NewGuid():N}.lpbackup");

        await engine.ExecuteAsync<object>("backup.export", new { output_path = path });
        var first = new FileInfo(path).Length;
        Assert.True(first > 0);

        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B" });
        var second = await engine.ExecuteAsync<object>("backup.export", new { output_path = path });
        Assert.True(second.Ok);

        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        Assert.NotNull(archive.GetEntry("data.json"));
        Assert.NotNull(archive.GetEntry("manifest.json"));

        // 临时文件（`*.tmp-*`）不许留在目录里
        var leftovers = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*");
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task Import_Replace_Failure_Keeps_Original_Data_Atomic()
    {
        // replace=true 的"清空 + 导入"必须是**同一个事务**。
        //
        // ⚠️ 这条用例抓的是一个**真的会丢数据**的缺陷：引擎的非干跑路径不开外层事务，而
        // `ClearAllDataAsync` 在没有外层事务时会**自建事务并当场提交** —— 于是"清空"已永久落库，
        // 之后导入任何一步失败（取消 / 约束冲突 / 磁盘满）都会留下**空库**且没有回滚。
        // 失败点用**导入中途取消**（`ct` 在书签循环里逐条检查）：包里有 2 万条书签，导入必然跑过取消点。
        var (engine, _, _) = TestHost.Create();
        var keep = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "原有目录" });
        await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://keep.example", title = "原有书签", list_id = keep.Data!.FolderId });
        // 第二个根级文件夹：起跑前根级应有 2 个 —— 这样"被清空"与"被完整还原"在断言上可区分
        // （只数一个容易把"只剩一个"读成"没被清"）
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "原有目录二" });

        var path = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbk_{Guid.NewGuid():N}.lpbackup");
        var sb = new System.Text.StringBuilder();
        sb.Append("""{"folders":[{"key":"f1","name":"导入的目录","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}],"links":[""");
        for (var i = 0; i < 20000; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($$"""{"folder":"f1","url":"https://new.example/{{i}}","title":"导入书签 {{i}}","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}""");
        }
        sb.Append("]}");
        BuildBackup(path, System.Text.Encoding.UTF8.GetBytes(sb.ToString()));

        var gate = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExecuteAsync<object>("backup.import", new { file_path = path, replace = true }));
        var token = gate.Error.Details!.Value.GetProperty("confirm_token").GetString();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            engine.ExecuteAsync<object>("backup.import",
                new { file_path = path, replace = true }, new CallOptions(ConfirmToken: token), cts.Token));

        // 硬判据：失败之后**原有数据必须原样还在**（旧实现在这里会是空库）
        // ⚠️ 只看根级 `folders.contents`：它列出的是**根级文件夹**，链接要看子目录内容
        //    （`folders.contents` 的 Links = 根级直连书签，不含子目录里的书签）。
        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", null);
        Assert.Equal(2, contents.SubFolders.Count);          // 清空 + 导入都回滚了（只剩 1 个就是被清过）
        Assert.Contains(contents.SubFolders, f => f.Name == "原有目录");
        Assert.Contains(contents.SubFolders, f => f.Name == "原有目录二");
        Assert.DoesNotContain(contents.SubFolders, f => f.Name == "导入的目录");

        var kept = await engine.QueryAsync<FolderContentsDto>("folders.contents",
            new { folder_id = keep.Data!.FolderId });
        Assert.Contains(kept.Links, l => l.Title == "原有书签");   // 子目录里的那条书签也在
    }

    [Fact]
    public async Task Import_DryRun_Does_Not_Clear_And_Does_Not_Throw()
    {
        // 干跑语义（引擎能力红线）：执行但不提交、零副作用。
        // ⚠️ 这条用例抓的是"处理器自己又开了一层事务"的回归：干跑时引擎**已经**开了显式事务，
        //    处理器再开一层会被 EF/SQLite 拒绝（"does not support nested transactions"）→ 干跑变 LP.SYS.001。
        //    破坏性命令的干跑会跳过确认门，是最容易被走到的入口，必须有覆盖。
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "原有目录" });

        var path = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbk_{Guid.NewGuid():N}.lpbackup");
        var data = """
        {"folders":[{"key":"f1","name":"导入的目录","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}],"links":[]}
        """;
        BuildBackup(path, System.Text.Encoding.UTF8.GetBytes(data));

        var dry = await engine.ExecuteAsync<JsonElement>("backup.import",
            new { file_path = path, replace = true }, new CallOptions(DryRun: true));
        Assert.True(dry.Ok);
        Assert.Equal(1, dry.Data.GetProperty("folders_created").GetInt32());

        // 零副作用：原有目录还在、导入的目录没进来
        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", null);
        Assert.Single(contents.SubFolders);
        Assert.Equal("原有目录", contents.SubFolders[0].Name);
    }
}

public class MaintenanceModuleTests
{
    [Fact]
    public async Task Schema_Version_And_Diagnostics()
    {
        var (engine, _, _) = TestHost.Create();
        var version = await engine.QueryAsync<JsonElement>("maintenance.schema_version", null);
        // 全新建库 = 完整版本链（v2 基线 + v3..v7 演进），版本表落最高版本
        // 加一条迁移脚本必须同时改这里：这是"有人偷偷加了迁移却没人核对"的绊线
        Assert.Equal(7, version.GetProperty("schema_version").GetInt32());

        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        var diag = await engine.QueryAsync<JsonElement>("diagnostics.collect", null);
        Assert.Equal(1, diag.GetProperty("counts").GetProperty("folders").GetInt32());

        // runtime 段：组合根已接线 → 必须是引擎真实读数而不是 null/假值
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
        Assert.Equal(0, diag.GetProperty("counts").GetProperty("standalone_trash_links").GetInt32());
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
        Assert.Equal(1, diag.GetProperty("counts").GetProperty("standalone_trash_links").GetInt32());
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
