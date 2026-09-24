using System.Text.Json;
using LinkPocket.Composition;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// P3+-4（G18）同事务按 ID 点查**读己之写**（ENGINE-API §5 / TRASH §4.4）：
/// 仓储 <c>Find*/Remove*</c> 合并 change tracker —— 本单元未提交的新增按 ID 可见、
/// 已删不可见、"建了又删"净效果为零；列表/范围查询与跨上下文（读池语义）**不合并**，
/// 仍只见提交边界的一致快照。
/// 正例核心 = §13.3 挂账场景：事务批 <c>folders.create → links.move_batch {target_list_id:"{a.id}"}</c>
/// （修复前误报 <c>LP.STATE.001 target folder … does not exist</c> 整批 abort）；
/// 负例证明点查**不放水**：真不存在的目标仍 LP.STATE.001 整批回滚。
/// </summary>
public class UnitOfWorkVisibilityTests
{
    // ── 仓储级：读己之写（不起引擎，直接驱动 EfUnitOfWork）──────────────

    /// <summary>未提交的新增：按 ID 点查可见（读己之写）；列表查询与跨上下文仍只见提交边界。</summary>
    [Fact]
    public async Task 未提交新增_按ID点查可见_列表与跨上下文仍只见提交边界()
    {
        var (_, factory, dbPath) = TestHost.Create();
        try
        {
            await using var uow = new EfUnitOfWork(factory.CreateDbContext());
            var folder = await uow.Folders.AddAsync(new Folder { Name = "未提交夹" }, default);

            // 点查 = 读己之写：本单元内可见（修复点）
            Assert.NotNull(await uow.Folders.FindAsync(new FolderId(folder.FolderId), default));

            // 列表查询不合并：同上下文的 AsNoTracking 列表只见提交边界（口径不变式）
            var listed = await uow.Folders.ListAllAsync(default);
            Assert.DoesNotContain(listed, f => f.FolderId == folder.FolderId);

            // 跨上下文 = 读池语义：另一短上下文看不到未提交新增（一致快照不变式）
            await using var other = new EfUnitOfWork(factory.CreateDbContext());
            Assert.Null(await other.Folders.FindAsync(new FolderId(folder.FolderId), default));
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>本单元已删（未提交）：按 ID 点查不可见；其它上下文在提交前仍看到旧行（一致快照）。</summary>
    [Fact]
    public async Task 本单元已删_按ID点查不可见_跨上下文提交前仍见旧行()
    {
        var (_, factory, dbPath) = TestHost.Create();
        string folderId;
        await using (var seed = new EfUnitOfWork(factory.CreateDbContext()))
        {
            var folder = await seed.Folders.AddAsync(new Folder { Name = "将被删" }, default);
            folderId = folder.FolderId;
            await seed.CommitAsync(default);
        }

        try
        {
            await using var uow = new EfUnitOfWork(factory.CreateDbContext());
            await uow.Folders.RemoveAsync(new FolderId(folderId), default);
            Assert.Null(await uow.Folders.FindAsync(new FolderId(folderId), default));   // 读己之写：已删不可见

            await using var other = new EfUnitOfWork(factory.CreateDbContext());
            Assert.NotNull(await other.Folders.FindAsync(new FolderId(folderId), default)); // 未提交的删除对其它上下文不可见
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>同单元"建了又删"：Remove 在未提交新增上 = 取消暂存，提交后净效果为零。</summary>
    [Fact]
    public async Task 建了又删_取消暂存_提交后净效果为零()
    {
        var (_, factory, dbPath) = TestHost.Create();
        try
        {
            await using (var uow = new EfUnitOfWork(factory.CreateDbContext()))
            {
                var folder = await uow.Folders.AddAsync(new Folder { Name = "昙花一现" }, default);
                await uow.Folders.RemoveAsync(new FolderId(folder.FolderId), default);
                Assert.Null(await uow.Folders.FindAsync(new FolderId(folder.FolderId), default));
                await uow.CommitAsync(default);
            }

            await using var fresh = new EfUnitOfWork(factory.CreateDbContext());
            Assert.Equal(0, await fresh.Folders.CountAsync(default));
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>回收站快照同口径：未提交快照按 ID 可见；Remove 后不可见（Local 优先路径）。</summary>
    [Fact]
    public async Task 回收站快照_未提交按ID可见_Remove后不可见()
    {
        var (_, factory, dbPath) = TestHost.Create();
        try
        {
            await using var uow = new EfUnitOfWork(factory.CreateDbContext());
            var linkId = EntityIds.NewLinkId();
            _ = await uow.Trash.AddLinkAsync(
                new TrashedLink { LinkId = linkId, Url = "https://vis.example/x" }, default);

            Assert.NotNull(await uow.Trash.FindLinkAsync(new LinkId(linkId), default));   // 待增可见

            await uow.Trash.RemoveLinkAsync(new LinkId(linkId), default);
            Assert.Null(await uow.Trash.FindLinkAsync(new LinkId(linkId), default));      // 取消暂存后不可见
        }
        finally { Cleanup(dbPath); }
    }

    // ── 批级：§13.3 挂账场景（全量组合 = 真实模块 + 编排层）─────────────

    /// <summary>正例：事务批 folders.create → links.move_batch {target_list_id:"{a.id}"} 整批成功（修复前 LP.STATE.001 abort）。</summary>
    [Fact]
    public async Task 事务批_先建夹后按ID校验目标_整批成功()
    {
        var dbPath = TempDb();
        try
        {
            var composed = EngineComposer.Compose(dbPath);
            var created = await composed.Client.ExecuteAsync<LinkDto>(
                "links.create", new { url = "https://vis.example/batch", title = "批内搬运种子" });
            var linkId = created.Data!.LinkId;

            var result = await composed.Client.ExecuteAsync<JsonElement>("batch.run", new
            {
                script = new
                {
                    name = "同批可见性",
                    scope = "transactional",
                    steps = new object[]
                    {
                        new { @ref = "a", command = "folders.create", args = new { name = "同批夹" } },
                        new { @ref = "b", command = "links.move_batch", args = new
                        {
                            link_ids = new[] { linkId },
                            target_list_id = "{a.id}",
                        } },
                    },
                },
            });

            Assert.True(result.Data.GetProperty("ok").GetBoolean(),
                $"批应成功：{result.Data.GetRawText()}");
            Assert.All(result.Data.GetProperty("steps").EnumerateArray(),
                step => Assert.True(step.GetProperty("ok").GetBoolean(),
                    $"每步应成功：{step.GetRawText()}"));
            var folderId = result.Data.GetProperty("steps")[0]
                .GetProperty("data").GetProperty("id").GetString();
            Assert.False(string.IsNullOrEmpty(folderId));

            // 提交后经读路径复核（读池 = 独立上下文）：链接确实落进同批新建的夹
            var moved = await composed.Client.QueryAsync<LinkDto>("links.get", new { id = linkId });
            Assert.Equal(folderId, moved.ListId);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>负例对照（判据必须能红）：目标夹真不存在 → 仍 LP.STATE.001、整批回滚、夹不落库。</summary>
    [Fact]
    public async Task 事务批_目标夹真不存在_仍报错且整批回滚()
    {
        var dbPath = TempDb();
        try
        {
            var composed = EngineComposer.Compose(dbPath);
            var created = await composed.Client.ExecuteAsync<LinkDto>(
                "links.create", new { url = "https://vis.example/abort", title = "回滚对照种子" });
            var linkId = created.Data!.LinkId;

            var ex = await Assert.ThrowsAsync<EngineException>(() =>
                composed.Client.ExecuteAsync<JsonElement>("batch.run", new
                {
                    script = new
                    {
                        name = "目标不存在",
                        scope = "transactional",
                        steps = new object[]
                        {
                            new { @ref = "a", command = "folders.create", args = new { name = "会被回滚的夹" } },
                            new { @ref = "b", command = "links.move_batch", args = new
                            {
                                link_ids = new[] { linkId },
                                target_list_id = "999999999999",   // 真不存在（12 位合法 ID 形状）
                            } },
                        },
                    },
                }));
            Assert.Equal(EngineErrors.BatchAborted, ex.Error.Code);
            Assert.Contains("does not exist", ex.Error.Message, StringComparison.Ordinal);

            // 整批回滚：step a 建的夹不落库（点查放水 ≠ 校验放水）
            var folders = await composed.Client.QueryAsync<List<FolderDto>>(
                "folders.find", new { name = "会被回滚的夹" });
            Assert.Empty(folders);
        }
        finally { Cleanup(dbPath); }
    }

    private static string TempDb()
        => Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpvis_{Guid.NewGuid():N}.db");

    private static void Cleanup(string path)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        catch { /* 池清理失败不阻断文件删除 */ }
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 句柄滞留 → 系统清理 */ }
    }
}
