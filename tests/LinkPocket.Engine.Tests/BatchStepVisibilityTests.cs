using System.Text.Json;
using LinkPocket.Composition;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 批/宏的**步骤间可见性**回归网（2026-09-26 实数据事故）：
/// 事务批 = 单 UoW + 统一提交，"先移动、再删除"这类脚本里删除步骤按**数据库现状**做筛选
///（folders.delete 的 cascade 读 links 表）——看不到前一步未落库的移动 ⇒ 把整批链接扫进回收站。
/// 修法 = 每步成功后 flush（只写事务不提交）+ 外层显式事务收口原子性。
/// </summary>
/// <remarks>
/// 事故形状（AI 生成的真实脚本）：links.query（取文件夹内 id）→ links.move_batch（移去根级）
/// → folders.delete（删空文件夹）。修复前：move 报 moved/affected=1630、cascade 报 trashed_links=1630，
/// 根目录空空、数据全躺在回收站里。
/// </remarks>
public class BatchStepVisibilityTests
{
    [Fact]
    public async Task 批内先移动再删除_移动必须生效_链接不被cascade扫走()
    {
        var (engine, dbPath) = CreateHost();
        try
        {
            var folder = await CreateFolderAsync(engine, "待拆文件夹");
            await CreateLinksAsync(engine, folder, 12);
            await engine.ExecuteAsync<int>("undo.clear", null);

            var report = await engine.Batch!.RunAsync(Script(folder, "批内拆解"));
            Assert.True(report.Ok, "批应成功");

            using var conn = Open(dbPath);
            Assert.Equal(12L, Scalar(conn, "select count(*) from links where folder_id is null"));   // 移动真的生效
            Assert.Equal(0L, Scalar(conn, "select count(*) from trash_links"));                     // 没有被 cascade 扫走
            Assert.Equal(0L, Scalar(conn, $"select count(*) from folders where id = '{folder}'"));  // 文件夹确实删了
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 宏内先移动再删除_同样按步骤顺序生效()
    {
        var (engine, dbPath) = CreateHost();
        try
        {
            var folder = await CreateFolderAsync(engine, "待拆文件夹");
            await CreateLinksAsync(engine, folder, 12);
            await engine.ExecuteAsync<int>("undo.clear", null);

            await engine.ExecuteAsync<JsonElement>("macro.save", new
            {
                name = "批内拆解宏",
                script = JsonSerializer.SerializeToElement(Script(folder, "批内拆解宏"), EngineJson.ScriptOptions),
            });
            var run = await engine.ExecuteAsync<JsonElement>("macro.run", new { name = "批内拆解宏" });
            Assert.True(run.Ok, "宏应成功");

            using var conn = Open(dbPath);
            Assert.Equal(12L, Scalar(conn, "select count(*) from links where folder_id is null"));
            Assert.Equal(0L, Scalar(conn, "select count(*) from trash_links"));
            Assert.Equal(0L, Scalar(conn, $"select count(*) from folders where id = '{folder}'"));
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 宏中途失败_前面步骤的flush一起回滚()
    {
        var (engine, dbPath) = CreateHost();
        try
        {
            var folder = await CreateFolderAsync(engine, "待拆文件夹");
            var ids = await CreateLinksAsync(engine, folder, 12);
            await engine.ExecuteAsync<int>("undo.clear", null);

            // 两步宏：① 移动（会 flush 进事务）② 删不存在的文件夹（必失败 ⇒ Abort）
            var script = new BatchScript("半路失败", [
                new BatchStep("m", "links.move_batch",
                    JsonSerializer.SerializeToElement(new { link_ids = ids, target_list_id = (string?)null })),
                new BatchStep("d", "folders.delete",
                    JsonSerializer.SerializeToElement(new { folder_id = "nonexistent-folder-id" })),
            ]);
            await engine.ExecuteAsync<JsonElement>("macro.save", new
            {
                name = "半路失败宏",
                script = JsonSerializer.SerializeToElement(script, EngineJson.ScriptOptions),
            });

            await Assert.ThrowsAsync<EngineException>(
                () => engine.ExecuteAsync<JsonElement>("macro.run", new { name = "半路失败宏" }));

            // 第一步的移动必须随失败一起回滚（flush 只写事务；"abort 整体回滚"的承诺不破）
            using var conn = Open(dbPath);
            Assert.Equal(0L, Scalar(conn, "select count(*) from links where folder_id is null"));
            Assert.Equal(12L, Scalar(conn, $"select count(*) from links where folder_id = '{folder}'"));
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 批干跑_同形状脚本零副作用()
    {
        var (engine, dbPath) = CreateHost();
        try
        {
            var folder = await CreateFolderAsync(engine, "待拆文件夹");
            await CreateLinksAsync(engine, folder, 12);
            await engine.ExecuteAsync<int>("undo.clear", null);

            var report = await engine.Batch!.DryRunAsync(Script(folder, "干跑拆解"));
            Assert.True(report.Ok, "干跑应成功（执行但不提交）");

            using var conn = Open(dbPath);
            Assert.Equal(0L, Scalar(conn, "select count(*) from links where folder_id is null"));   // 干跑不许落库
            Assert.Equal(12L, Scalar(conn, $"select count(*) from links where folder_id = '{folder}'"));
            Assert.Equal(1L, Scalar(conn, $"select count(*) from folders where id = '{folder}'"));
            Assert.Equal(0L, Scalar(conn, "select count(*) from trash_links"));
        }
        finally { Cleanup(dbPath); }
    }

    // ── 事故形状的脚本与宿主 ─────────────────────────────────────

    /// <summary>事故里的三步脚本：query（取文件夹内 id）→ move_batch（去根级）→ delete（删文件夹）。</summary>
    private static BatchScript Script(string folderId, string name) => new(name, [
        new BatchStep("q", "links.query", JsonSerializer.SerializeToElement(new
        {
            filter = new[] { new { field = "folder_id", op = "eq", value = folderId } },
            fields = new[] { "id" },
            page = new { index = 1, size = 0 },   // size 0 = 不分页（取全部）
        })),
        new BatchStep("m", "links.move_batch",
            JsonSerializer.SerializeToElement(new { link_ids = "{q.items[*].id}" })),
        new BatchStep("d", "folders.delete",
            JsonSerializer.SerializeToElement(new { folder_id = folderId })),
    ]);

    private static (EngineCore Engine, string DbPath) CreateHost()
    {
        var path = Path.Combine(TempArea.Resolve(), $"lpbatch_vis_{Guid.NewGuid():N}.db");
        var composed = EngineComposer.Compose(path, new ComposeOptions
        {
            SqlAudit = false,
            SqlIdempotency = false,
            IncludeOrchestration = true,   // batch / macro / undo 都从这里来
            BuildWire = false,             // batch 走 Engine.Batch 直连，不需要 wire
        });
        return (composed.Engine, path);
    }

    private static async Task<string> CreateFolderAsync(EngineCore engine, string name)
        => (await engine.ExecuteAsync<FolderDto>("folders.create", new { name })).Data!.FolderId;

    private static async Task<List<string>> CreateLinksAsync(EngineCore engine, string folderId, int count)
    {
        var ids = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var link = await engine.ExecuteAsync<LinkDto>("links.create",
                new { url = $"https://batch-vis.test/{i}", title = $"链接{i}", list_id = folderId });
            ids.Add(link.Data!.LinkId);
        }
        return ids;
    }

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Cleanup(string path)
    {
        try { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
        catch { /* 临时文件交给系统清理 */ }
    }
}
