using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 三份审核报告（Engine / Data / Contracts）修复的针对性回归测试：
/// 幂等过期清理不再因 reader 未关闭而静默失败；会话 End 后无法再绕过能力门；
/// 撤销协调器并发取 id 不丢条目；目录/AI 工具清单的数组参数类型机读化。
/// </summary>
public class AuditReviewFixesTests
{
    // ===== SqlIdempotencyStore.TryGet 的 reader-DELETE 组合 =====

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

    // ===== SessionManager.EndAsync 后能力门不得失效 =====

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

    // ===== UndoCoordinator 并发 TakeUndoAsync(id) + Record 不丢条目 =====

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

    // ===== 撤销栈跨进程持久化：落盘 → 新实例恢复（应用重启后仍可回溯）=====

    [Fact]
    public async Task UndoCoordinator_Journal_Survives_New_Instance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lp-undo-test-{Guid.NewGuid():N}.json");
        try
        {
            var descriptor = new CommandDescriptor("test.x", "test", "d", [], CommandCaps.Mutation, UndoInverse: "test.y");
            static System.Text.Json.JsonElement Args() => JsonSerializer.Deserialize<JsonElement>("{}");

            // 实例 A：登记 3 条 → 落盘
            var a = new UndoCoordinator(path);
            for (var i = 0; i < 3; i++) a.Record(descriptor, Args(), CallerRef.Test);
            var before = await a.ListAsync(default);
            Assert.Equal(3, before.Count);

            // 实例 B（模拟应用重启）：从同一份存档恢复 → 栈还在，条目逐字段一致
            var b = new UndoCoordinator(path);
            var after = await b.ListAsync(default);
            Assert.Equal(before.Count, after.Count);
            Assert.Equal(before.Select(e => e.Id), after.Select(e => e.Id));
            Assert.Equal(before.Select(e => e.Command), after.Select(e => e.Command));
            Assert.All(after, e => Assert.NotEmpty(e.Steps));

            // 实例 B 撤销一条（Take 取走 → 逆向执行成功后 MarkUndone 转入重做栈，与 undo.undo 处理器同序）
            // → 落盘；实例 C 读到的是撤销后的状态（2 条 + 重做 1 条）
            var taken = await b.TakeUndoAsync(null, default);
            Assert.NotNull(taken);
            b.MarkUndone(taken!);
            var c = new UndoCoordinator(path);
            Assert.Equal(2, (await c.ListAsync(default)).Count);
            Assert.Single(await c.ListRedoAsync(default));   // 重做栈 1 条（xUnit2013：计数为 1 时用 Single）
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ===== 目录/AI 工具清单的数组参数类型 =====

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

    // ===== P3-6：ParamSpec 枚举/嵌套元数据透出（塌陷面收窄）=====

    [Fact]
    public void Catalog_Exports_Enum_Metadata_On_Scalar_And_Array_Items()
    {
        var registry = new CommandRegistry();
        registry.Register(new MetaHandler());
        var catalog = new EngineCatalog(registry, includeBatch: false);
        using var doc = JsonDocument.Parse(catalog.Export(ManifestFormat.FunctionCalling));

        var props = doc.RootElement[0].GetProperty("function").GetProperty("parameters").GetProperty("properties");
        // 标量枚举 → 顶层 enum
        var mode = props.GetProperty("mode");
        Assert.Equal("string", mode.GetProperty("type").GetString());
        Assert.Equal(new[] { "alpha", "beta" },
            mode.GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToArray());
        // 集合枚举 → items.enum（enum 挂在元素上，不是数组上）
        var ids = props.GetProperty("ids");
        Assert.Equal("array", ids.GetProperty("type").GetString());
        Assert.Contains("id1", ids.GetProperty("items").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.False(ids.TryGetProperty("enum", out _));
    }

    [Fact]
    public void Catalog_Exports_Schema_Fragment_Instead_Of_Collapsed_Object()
    {
        var registry = new CommandRegistry();
        registry.Register(new MetaHandler());
        var catalog = new EngineCatalog(registry, includeBatch: false);

        var tools = catalog.Export(ManifestFormat.FunctionCalling);
        // Schema 片段整体透出：批脚本参数不再塌陷成裸 object（有 properties/steps/on_error 枚举）
        Assert.Contains("\"steps\"", tools);
        Assert.Contains("\"SkipAndLog\"", tools);
        Assert.Contains("\"transactional\"", tools);

        // OpenAPI 查询参数同样透出片段（且不在 schema 里重复外层 description）
        var openapi = catalog.Export(ManifestFormat.OpenApiLite);
        Assert.Contains("\"SkipAndLog\"", openapi);
    }

    [Fact]
    public void Catalog_BatchDescriptors_Carry_Script_Schema()
    {
        var registry = new CommandRegistry();
        var catalog = new EngineCatalog(registry, includeBatch: true);

        var tools = catalog.Export(ManifestFormat.FunctionCalling);
        // 真实批描述符：script 参数带 ParamSchemas.BatchScript（机器可读 + on_error 正确拼写）
        Assert.Contains("\"on_error\"", tools);
        Assert.Contains("\"SkipAndLog\"", tools);
        Assert.Contains("{ref.path[*]", tools);
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

/// <summary>测试命令（P3-6）：标量枚举 + 集合枚举 + Schema 片段三类元数据的导出断言。</summary>
internal sealed class MetaHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "test.meta", "test", "元数据导出断言",
        [
            ParamSpec.Opt<string>("mode", "Mode", enumValues: ["alpha", "beta"]),
            ParamSpec.Opt<string>("tag", "Tag", enumValues: ["x", "y"]),
            ParamSpec.Opt<IReadOnlyList<string>>("ids", "Ids", enumValues: ["id1"]),
            ParamSpec.Opt<JsonElement>("script", "Batch script", schema: ParamSchemas.BatchScript),
        ],
        CommandCaps.Query);   // Query → OpenAPI 走 query 参数位（断言片段在查询侧也透出）

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
        => Task.FromResult(CommandResult.Ok("ok"));
}