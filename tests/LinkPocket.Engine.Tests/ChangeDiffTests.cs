using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 字段级 diff（E2）：契约聚合（嵌套 → 事件负载 / 批报告）与载荷投影（wire / 审计 changes_json）——
/// 上限 2000 条 + 自描述截断标记（ENGINE-API §1）。
/// </summary>
public class ChangeDiffTests
{
    [Fact]
    public async Task 顶层结果_原样返回处理器上报的字段级diff()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var engine = TestEnv.CreateEngine(factory, new PatchFolderHandler());
            var result = await engine.ExecuteAsync<JsonElement>("test.patch_folder",
                new { name = "旧名", new_name = "新名" });

            var diff = Assert.IsAssignableFrom<IReadOnlyList<FieldChange>>(result.Changes!.Diff);
            var entry = Assert.Single(diff);
            Assert.Equal("folder", entry.Type);
            Assert.Equal("field", entry.Field);
            Assert.Equal("旧名", entry.Before!.Value.GetString());
            Assert.Equal("新名", entry.After!.Value.GetString());
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 嵌套diff_按发生顺序并入父事件负载()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var engine = TestEnv.CreateEngine(factory, new PatchFolderHandler(), new NestedPatchHandler());
            var payloads = new List<JsonElement>();
            using var sub = engine.Events.Subscribe(e => payloads.Add(e.Data!.Value.Clone()));

            await engine.ExecuteAsync<JsonElement>("test.nested_patch", new { name = "甲" });

            var payload = Assert.Single(payloads);
            var diff = payload.GetProperty("diff").EnumerateArray().ToList();
            Assert.Equal(2, diff.Count);
            // 顺序 = 嵌套步骤的发生顺序（不去重：同一实体两次 = 两条）
            Assert.Equal("step-1", diff[0].GetProperty("after").GetString());
            Assert.Equal("step-2", diff[1].GetProperty("after").GetString());
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 批报告_事务批聚合各步diff()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var engine = TestEnv.CreateEngine(factory, new PatchFolderHandler());
            var batch = new BatchEngine(engine);
            var report = await batch.RunAsync(new BatchScript("diff 批",
            [
                new BatchStep("a", "test.patch_folder", JsonSerializer.SerializeToElement(new { name = "甲", new_name = "甲2" })),
                new BatchStep("b", "test.patch_folder", JsonSerializer.SerializeToElement(new { name = "乙", new_name = "乙2" })),
            ]));

            Assert.True(report.Ok);
            var diff = Assert.IsAssignableFrom<IReadOnlyList<FieldChange>>(report.Changes!.Diff);
            Assert.Equal(2, diff.Count);
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 批报告_独立批同样聚合_各步走顶层管道_不经父缓冲()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var engine = TestEnv.CreateEngine(factory, new PatchFolderHandler());
            var batch = new BatchEngine(engine);
            var report = await batch.RunAsync(new BatchScript("独立 diff 批",
            [
                new BatchStep("a", "test.patch_folder", JsonSerializer.SerializeToElement(new { name = "甲", new_name = "甲2" })),
                new BatchStep("b", "test.patch_folder", JsonSerializer.SerializeToElement(new { name = "乙", new_name = "乙2" })),
            ], BatchScope.Independent));

            Assert.True(report.Ok);
            Assert.Equal(2, report.Changes!.Diff!.Count);
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 载荷投影_diff超上限_裁剪为前2000条并带自描述截断标记()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var engine = TestEnv.CreateEngine(factory, new BigDiffHandler());
            var wire = new EngineWire(engine);

            var response = await wire.HandleAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "engine.execute",
                @params = new { command = "test.big_diff" },
            }));

            using var doc = JsonDocument.Parse(response);
            var changes = doc.RootElement.GetProperty("result").GetProperty("changes");
            Assert.Equal(2000, changes.GetProperty("diff").GetArrayLength());
            Assert.True(changes.GetProperty("diff_truncated").GetBoolean());
            Assert.Equal(500, changes.GetProperty("diff_omitted").GetInt32());
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public void 审计载荷_与wire同一投影_裁剪后写库且带截断标记()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var writer = new SqlAuditWriter(() => factory.CreateDbContext());
            writer.Write(new AuditEntry(
                DateTimeOffset.Now, "test.big_diff", "corr-1", new CallerRef(CallerKind.Test, null), 1,
                Success: true, ErrorCode: null,
                Changes: new ChangeSet([new EntityRef("folder", "F1")], ["folders.changed"], "big",
                    Warnings: null, Diff: BigDiffHandler.MakeDiff(2005)),
                DryRun: false, IsNested: false, StackTrace: null));

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder($"Data Source={path}").ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT changes_json FROM audit_log WHERE command = 'test.big_diff'";
            var json = (string)cmd.ExecuteScalar()!;

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(2000, doc.RootElement.GetProperty("diff").GetArrayLength());
            Assert.True(doc.RootElement.GetProperty("diff_truncated").GetBoolean());
            Assert.Equal(5, doc.RootElement.GetProperty("diff_omitted").GetInt32());
        }
        finally { TestEnv.Cleanup(path); }
    }
}

/// <summary>产出单条字段级 diff 的测试命令（模拟"读到旧值 → 赋值"的处理器形态）。</summary>
internal sealed class PatchFolderHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } =
        new("test.patch_folder", "test", "改名（测试）",
            [ParamSpec.Req<string>("name", "旧名"), ParamSpec.Req<string>("new_name", "新名")], CommandCaps.Mutation);

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
        => Task.FromResult(CommandResult.Ok(
            JsonSerializer.SerializeToElement(new { ok = true }),
            ChangeSet.Of(new EntityRef("folder", "F1"), "folders.changed", "renamed",
                diff:
                [
                    new FieldChange("folder", "F1", "field",
                        FieldValue.Str(args.GetProperty("name").GetString()),
                        FieldValue.Str(args.GetProperty("new_name").GetString())),
                ])));
}

/// <summary>嵌套派发两次 PatchFolderHandler：验证嵌套 diff 按发生顺序并入父变更集。</summary>
internal sealed class NestedPatchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } =
        new("test.nested_patch", "test", "嵌套改名（测试）",
            [ParamSpec.Req<string>("name", "名称")], CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = args.GetProperty("name").GetString();
        await ctx.DispatchNestedAsync("test.patch_folder", new { name, new_name = "step-1" });
        await ctx.DispatchNestedAsync("test.patch_folder", new { name, new_name = "step-2" });
        return CommandResult.Ok(JsonSerializer.SerializeToElement(new { ok = true }));
    }
}

/// <summary>产出超上限 diff 的测试命令（载荷裁剪 + 截断标记用）。</summary>
internal sealed class BigDiffHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } =
        new("test.big_diff", "test", "大 diff（测试）", [], CommandCaps.Mutation);

    public static IReadOnlyList<FieldChange> MakeDiff(int count)
        => Enumerable.Range(0, count)
            .Select(i => new FieldChange("folder", $"F{i}", "name", FieldValue.Str("旧"), FieldValue.Str("新")))
            .ToArray();

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
        => Task.FromResult(CommandResult.Ok(
            JsonSerializer.SerializeToElement(new { ok = true }),
            ChangeSet.Of(new EntityRef("folder", "F1"), "folders.changed", "big", diff: MakeDiff(2500))));
}
