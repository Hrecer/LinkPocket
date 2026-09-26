using System.Text.Json;
using LinkPocket.Composition;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 批量移动的**撤销载荷分组**回归网（2026-09-26）：
/// 一次批量移动的逆向载荷必须**按原目录分组、一目标一步**，而不是"每链接一步"——
/// 1630 条的批量移动若记成 1630 步，撤销就要跑 1630 次嵌套命令（undo.undo 实测 5034ms）。
/// </summary>
/// <remarks>
/// 为什么走编排层：<c>CommandResult</c> 不暴露撤销载荷，"到底记了几步"只有 <c>undo.list</c> 读得到
/// （清单是摘要视图，小条目的步骤内联在清单里，可直接数）。
/// 宿主 = 共享组合根的全量装配（九模块 + 编排），跑的是真实的 <c>links.move_batch</c>。
/// </remarks>
public class UndoPayloadGroupingTests
{
    [Fact]
    public async Task 同源12条批量移动_撤销载荷一步装回原目录()
    {
        var (engine, dbPath) = CreateHost();
        try
        {
            var source = await CreateFolderAsync(engine, "撤销分组源");
            var target = await CreateFolderAsync(engine, "撤销分组目标");
            var ids = await CreateLinksAsync(engine, source, 12);

            await engine.ExecuteAsync<int>("undo.clear", null);   // 只留批量移动这一条，条目数无歧义
            var moved = await engine.ExecuteAsync<LinkBatchResult>("links.move_batch",
                new { link_ids = ids, target_list_id = target });
            Assert.True(moved.Ok, "批量移动应成功");

            var entry = Assert.Single(await UndoListAsync(engine));
            var step = Assert.Single(entry.Steps);                  // 12 条同源 = 1 步（分组前是 12 步）
            Assert.Equal("links.move_batch", step.InverseCommand);  // 逆向必须用 move_batch（只有它能表达"移回根级"）
            Assert.Equal(source, step.InverseTarget);               // 逆向目标 = 原目录
            Assert.Equal(12, step.InverseLinkCount);                // 一步带着全部 12 条

            // 一步撤销真的把 12 条一起放回原目录（直接读库断言，不看它报的 changes）
            await engine.ExecuteAsync<JsonElement>("undo.undo", null);
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "select count(*) from links where folder_id = $f";
            cmd.Parameters.AddWithValue("$f", source);
            Assert.Equal(12L, (long)cmd.ExecuteScalar()!);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task 两个来源目录_撤销载荷按原目录各一步()
    {
        var (engine, dbPath) = CreateHost();
        try
        {
            var sourceA = await CreateFolderAsync(engine, "撤销分组源A");
            var sourceB = await CreateFolderAsync(engine, "撤销分组源B");
            var target = await CreateFolderAsync(engine, "撤销分组目标");
            var ids = new List<string>();
            ids.AddRange(await CreateLinksAsync(engine, sourceA, 6));
            ids.AddRange(await CreateLinksAsync(engine, sourceB, 6));

            await engine.ExecuteAsync<int>("undo.clear", null);
            var moved = await engine.ExecuteAsync<LinkBatchResult>("links.move_batch",
                new { link_ids = ids, target_list_id = target });
            Assert.True(moved.Ok);

            var entry = Assert.Single(await UndoListAsync(engine));
            Assert.Equal(2, entry.Steps.Count);   // 两个来源 = 两步（各带 6 条）：不是每链接一步、也不是一个来源一步混在一起
            Assert.Equal(new[] { 6, 6 }, entry.Steps.Select(s => s.InverseLinkCount).Order().ToArray());
            Assert.Equal(
                new[] { sourceA, sourceB }.Order(StringComparer.Ordinal).ToArray(),
                entry.Steps.Select(s => s.InverseTarget!).Order(StringComparer.Ordinal).ToArray());
        }
        finally { Cleanup(dbPath); }
    }

    // ── 宿主与接线 ────────────────────────────────────────────

    /// <summary>全量宿主（九模块 + 编排；撤销栈纯内存 = 测试彼此隔离）。</summary>
    private static (EngineCore Engine, string DbPath) CreateHost()
    {
        var path = Path.Combine(TempArea.Resolve(), $"lpundo_group_{Guid.NewGuid():N}.db");
        var composed = EngineComposer.Compose(path, new ComposeOptions
        {
            SqlAudit = false,             // 本用例只关心撤销载荷：审计/幂等落库是无关开销
            SqlIdempotency = false,
            IncludeOrchestration = true,  // undo.list / undo.undo 从这里来
            BuildWire = false,
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
                new { url = $"https://undo-group.test/{i}", title = $"链接{i}", list_id = folderId });
            ids.Add(link.Data!.LinkId);
        }
        return ids;
    }

    /// <summary>撤销栈清单：每条目读它的逆向步骤（命令 / 目标 / 条数）。</summary>
    private static async Task<IReadOnlyList<ParsedEntry>> UndoListAsync(EngineCore engine)
    {
        var list = await engine.QueryAsync<JsonElement>("undo.list");
        return list.GetProperty("entries").EnumerateArray().Select(e => new ParsedEntry(
            e.GetProperty("steps").EnumerateArray().Select(s =>
            {
                var args = s.GetProperty("inverse_args");
                var count = args.TryGetProperty("link_ids", out var links) && links.ValueKind == JsonValueKind.Array
                    ? links.GetArrayLength()
                    : 0;
                var target = args.TryGetProperty("target_list_id", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;
                return new ParsedStep(s.GetProperty("inverse_command").GetString()!, target, count);
            }).ToList())).ToList();
    }

    private static void Cleanup(string path)
    {
        try { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
        catch { /* 临时文件交给系统清理 */ }
    }

    private sealed record ParsedEntry(IReadOnlyList<ParsedStep> Steps);
    private sealed record ParsedStep(string InverseCommand, string? InverseTarget, int InverseLinkCount);
}
