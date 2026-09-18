using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 三份审核报告（Engine / Data / Contracts）修复的针对性回归测试：
/// 1.1 幂等过期清理不再因 reader 未关闭而静默失败；1.2 会话 End 后无法再绕过能力门；
/// 1.3 撤销协调器并发取 id 不丢条目；2.1 + 3.1 目录/AI 工具清单的数组参数类型机读化。
/// </summary>
public class AuditReviewFixesTests
{
    // ===== 审核 1.1：SqlIdempotencyStore.TryGet 的 reader-DELETE 组合 =====

    [Fact]
    public async Task Idempotency_Expired_Row_Is_Cleaned_Without_Throwing()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var writer = new SqlIdempotencyStore(() => factory.CreateDbContext(), window: TimeSpan.FromHours(24));
            writer.Store("expired-key", new CommandResult("首次结果", null, "audit-1"));
            Assert.True(writer.TryGet("expired-key", out _));   // 内存命中（未过期）

            // 新实例（内存缓存为空）+ 负窗口 → 走表路径且必然判定过期
            var expiredWindow = new SqlIdempotencyStore(() => factory.CreateDbContext(), window: TimeSpan.FromSeconds(-1));
            Assert.False(expiredWindow.TryGet("expired-key", out _));   // 表命中但过期 → 未命中，且不抛（reader 已关闭再 DELETE）

            // 过期行应已被清理：表里查不到
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using var probe = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            await probe.OpenAsync();
            await using var cmd = probe.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM idempotency WHERE key = 'expired-key'";
            Assert.Equal(0, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        }
        finally { Cleanup(path); }
    }

    // ===== 审核 1.2：SessionManager.EndAsync 后能力门不得失效 =====

    [Fact]
    public async Task Session_After_End_Never_Passes_Enforce()
    {
        var (factory, path) = TestEnv.CreateDb();
        var sessions = new SessionManager();
        var engine = TestEnv.CreateEngine(factory, withOrchestration: false, sessions);
        try
        {
            var session = await sessions.BeginAsync(new SessionProfile(SessionKind.AgentReadonly));
            await sessions.EndAsync(session.SessionId);

            // End 后：带旧 SessionId 的查询也被拒（不能靠 End + 重放绕过能力门）
            var caller = new CallerRef(CallerKind.Agent, session.SessionId);
            Assert.Null(sessions.Get(session.SessionId));   // Get 同步返回 null
            var ex = await Assert.ThrowsAsync<EngineException>(() => engine.QueryAsync<int>(
                "test.count_folders", null, new CallOptions(Caller: caller)));
            Assert.Equal(EngineErrors.EntityNotFound, ex.Error.Code);
        }
        finally { Cleanup(path); }
    }

    // ===== 审核 1.3：UndoCoordinator 并发 TakeUndoAsync(id) + Record 不丢条目 =====

    [Fact]
    public async Task UndoCoordinator_Concurrent_TakeById_And_Record_Keeps_All_Entries()
    {
        var undo = new UndoCoordinator();
        var descriptor = new CommandDescriptor("test.x", "test", "d", [], CommandCaps.Mutation, UndoInverse: "test.y");
        static System.Text.Json.JsonElement Args() => JsonSerializer.Deserialize<JsonElement>("{}");

        // 先铺 8 条，取 ids
        for (var i = 0; i < 8; i++) undo.Record(descriptor, Args(), CallerRef.Test);
        var ids = (await undo.ListAsync(default)).Select(e => e.Id).ToArray();
        Assert.Equal(8, ids.Length);

        // 并发：一半调用按 id 取走，一半新登记——任一窗口丢条目都会让终态计数不足
        var tasks = new List<Task>();
        for (var i = 0; i < 4; i++)
        {
            var id = ids[i];
            tasks.Add(undo.TakeUndoAsync(id, default));
            tasks.Add(Task.Run(() => undo.Record(descriptor, Args(), CallerRef.Test)));
        }
        await Task.WhenAll(tasks);

        var remaining = await undo.ListAsync(default);
        // 8 - 4(取走) + 4(新登记) = 8
        Assert.Equal(8, remaining.Count);
    }

    // ===== 审核 2.1 + 3.1：目录/AI 工具清单的数组参数类型 =====

    [Fact]
    public void ParamSpec_TypeName_Is_Normalized_For_Generics()
    {
        // CLR 泛型参数名（typeof(string).Name == "String"）递归展平，但去掉反引号与 `1 畸形后缀
        Assert.Equal("IReadOnlyList<String>", ParamSpec.Req<IReadOnlyList<string>>("tags", "标签").TypeName);
        Assert.Equal("IReadOnlyList<String>", ParamSpec.Opt<IReadOnlyList<string>>("tags", "标签").TypeName);
        Assert.Equal("String", ParamSpec.Req<string>("name", "名称").TypeName);
        Assert.Equal("JsonElement", ParamSpec.Req<JsonElement>("script", "脚本").TypeName);
    }

    [Fact]
    public void Catalog_Exports_Array_Parameter_With_Items()
    {
        var registry = new CommandRegistry();
        var arrays = new ArraysHandler();
        registry.Register(arrays);
        var catalog = new EngineCatalog(registry, includeBatch: false);

        var tools = catalog.Export(ManifestFormat.FunctionCalling);
        Assert.Contains("\"type\": \"array\"", tools);
        Assert.Contains("\"items\"", tools);
        // 泛型畸形名不再出现
        Assert.DoesNotContain("IReadOnlyList`1", tools);

        var openapi = catalog.Export(ManifestFormat.OpenApiLite);
        Assert.Contains("array", openapi);
    }

    // ===== 辅助 =====

    private static void Cleanup(string path)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* 临时文件交给系统清理 */ }
    }
}

/// <summary>测试命令：带集合参数（目录导出断言数组类型）。</summary>
internal sealed class ArraysHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "test.arrays", "test", "集合参数断言",
        [ParamSpec.Req<IReadOnlyList<string>>("tags", "标签列表")], CommandCaps.Mutation);

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
        => Task.FromResult(CommandResult.Ok("ok"));
}