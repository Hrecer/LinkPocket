using LinkPocket.Contracts;
using LinkPocket.Engine;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>links.trash_batch：集合化批量入站，快照必须仍是“单独删除的书签”（挂回收站根、保留原位置）。</summary>
public class LinkTrashBatchTests
{
    [Fact]
    public async Task 批量删除_快照挂回收站根_原位置保留()
    {
        var (engine, _, dbPath) = TestHost.Create();
        try
        {
            var folder = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "目标" })).Data!;
            var ids = new List<string>();
            for (var i = 0; i < 3; i++)
                ids.Add((await engine.ExecuteAsync<LinkDto>("links.create", new
                {
                    url = $"https://batch-trash.example/{i}",
                    title = $"B{i}",
                    list_id = folder.FolderId,
                })).Data!.LinkId);

            var result = await engine.ExecuteAsync<LinkBatchResult>("links.trash_batch", new { link_ids = ids });
            Assert.Equal(3, result.Data!.Affected);

            foreach (var id in ids)
                Assert.Equal(EngineErrors.EntityNotFound,
                    (await Assert.ThrowsAsync<EngineException>(
                        () => engine.QueryAsync<LinkDto>("links.get", new { id }))).Error.Code);

            var trash = await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null);
            Assert.Equal(3, trash.Count);
            Assert.All(trash, e => Assert.Equal(TrashEntryType.Link, e.EntryType));
            Assert.All(trash, e => Assert.Equal("@root/目标", e.OriginPath));
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 根级链接_批量删除_原位置为根_可批量还原()
    {
        var (engine, _, dbPath) = TestHost.Create();
        try
        {
            var ids = new List<string>();
            for (var i = 0; i < 2; i++)
                ids.Add((await engine.ExecuteAsync<LinkDto>("links.create", new
                {
                    url = $"https://root-batch.example/{i}",
                    title = $"R{i}",
                })).Data!.LinkId);

            await engine.ExecuteAsync<LinkBatchResult>("links.trash_batch", new { link_ids = ids });
            var trash = await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null);
            Assert.Equal(2, trash.Count);
            Assert.All(trash, e => Assert.Equal("@root", e.OriginPath));

            await engine.ExecuteAsync<TrashRestoreBatchResult>("trash.restore_batch",
                new { link_ids = ids, to = "origin" });
            foreach (var id in ids)
            {
                var link = await engine.QueryAsync<LinkDto>("links.get", new { id });
                Assert.Null(link.ListId);   // 根级还原
            }
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 跨目录与根级混合_一次批量入站_各自保留原位置()
    {
        var (engine, _, dbPath) = TestHost.Create();
        try
        {
            var a = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" })).Data!;
            var a1 = (await engine.ExecuteAsync<FolderDto>("folders.create",
                new { name = "A1", parent_id = a.FolderId })).Data!;
            var b = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B" })).Data!;

            var inA1 = (await engine.ExecuteAsync<LinkDto>("links.create", new
            {
                url = "https://batch-mixed.example/a1",
                title = "A1 链接",
                list_id = a1.FolderId,
            })).Data!;
            var inB = (await engine.ExecuteAsync<LinkDto>("links.create", new
            {
                url = "https://batch-mixed.example/b",
                title = "B 链接",
                list_id = b.FolderId,
            })).Data!;
            var atRoot = (await engine.ExecuteAsync<LinkDto>("links.create", new
            {
                url = "https://batch-mixed.example/root",
                title = "根链接",
            })).Data!;

            var result = await engine.ExecuteAsync<LinkBatchResult>("links.trash_batch",
                new { link_ids = new[] { inA1.LinkId, inB.LinkId, atRoot.LinkId } });
            Assert.Equal(3, result.Data!.Affected);

            var trash = (await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null))
                .ToDictionary(e => e.Id, StringComparer.Ordinal);
            Assert.Equal(3, trash.Count);
            Assert.Equal("@root/A/A1", trash[inA1.LinkId].OriginPath);
            Assert.Equal("@root/B", trash[inB.LinkId].OriginPath);
            Assert.Equal("@root", trash[atRoot.LinkId].OriginPath);
            Assert.All(trash.Values, e => Assert.Equal(TrashEntryType.Link, e.EntryType));
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 重复与空白ID_去重后只入站一次()
    {
        var (engine, _, dbPath) = TestHost.Create();
        try
        {
            var one = (await engine.ExecuteAsync<LinkDto>("links.create", new
            {
                url = "https://batch-dup.example/1",
                title = "D1",
            })).Data!;
            var two = (await engine.ExecuteAsync<LinkDto>("links.create", new
            {
                url = "https://batch-dup.example/2",
                title = "D2",
            })).Data!;

            var result = await engine.ExecuteAsync<LinkBatchResult>("links.trash_batch",
                new { link_ids = new[] { one.LinkId, one.LinkId, "  ", two.LinkId, " " } });
            Assert.Equal(2, result.Data!.Affected);
            Assert.Equal(2, (await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null)).Count);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 已入站链接再次批量删除_硬失败且不产生重复快照()
    {
        var (engine, _, dbPath) = TestHost.Create();
        try
        {
            var link = (await engine.ExecuteAsync<LinkDto>("links.create", new
            {
                url = "https://batch-repeat.example/1",
                title = "R1",
            })).Data!;
            await engine.ExecuteAsync<LinkBatchResult>("links.trash_batch", new { link_ids = new[] { link.LinkId } });

            var ex = await Assert.ThrowsAsync<EngineException>(
                () => engine.ExecuteAsync<LinkBatchResult>("links.trash_batch", new { link_ids = new[] { link.LinkId } }));
            Assert.Equal(EngineErrors.EntityNotFound, ex.Error.Code);
            Assert.Single(await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null));
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 干跑_执行搬运但整体回滚()
    {
        var (engine, _, dbPath) = TestHost.Create();
        try
        {
            var one = (await engine.ExecuteAsync<LinkDto>("links.create", new
            {
                url = "https://batch-dry.example/1",
                title = "DR1",
            })).Data!;
            var two = (await engine.ExecuteAsync<LinkDto>("links.create", new
            {
                url = "https://batch-dry.example/2",
                title = "DR2",
            })).Data!;

            var dry = await engine.ExecuteAsync<LinkBatchResult>("links.trash_batch",
                new { link_ids = new[] { one.LinkId, two.LinkId } }, new CallOptions(DryRun: true));
            Assert.Equal(2, dry.Data!.Affected);
            Assert.NotNull(await engine.QueryAsync<LinkDto>("links.get", new { id = one.LinkId }));
            Assert.NotNull(await engine.QueryAsync<LinkDto>("links.get", new { id = two.LinkId }));
            Assert.Empty(await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null));
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 空列表_报必填错误_不产生副作用()
    {
        var (engine, _, dbPath) = TestHost.Create();
        try
        {
            var ex = await Assert.ThrowsAsync<EngineException>(
                () => engine.ExecuteAsync<LinkBatchResult>("links.trash_batch", new { link_ids = Array.Empty<string>() }));
            Assert.Equal(EngineErrors.RequiredParam, ex.Error.Code);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 含不存在的链接_整批不写()
    {
        var (engine, _, dbPath) = TestHost.Create();
        try
        {
            var link = (await engine.ExecuteAsync<LinkDto>("links.create", new
            {
                url = "https://batch-trash.example/ok",
                title = "OK",
            })).Data!;

            var ex = await Assert.ThrowsAsync<EngineException>(
                () => engine.ExecuteAsync<LinkBatchResult>("links.trash_batch",
                    new { link_ids = new[] { link.LinkId, "no-such-link" } }));
            Assert.Equal(EngineErrors.EntityNotFound, ex.Error.Code);
            Assert.NotNull((await engine.QueryAsync<LinkDto>("links.get", new { id = link.LinkId })).LinkId);
            Assert.Empty(await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null));
        }
        finally { Cleanup(dbPath); }
    }

    private static void Cleanup(string dbPath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        LinkPocket.Data.OpsDbContextFactory.DeleteDatabaseFiles(dbPath);
    }
}
