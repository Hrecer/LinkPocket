using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 编排层：批（事务回滚/continue/dry_run/ref 模板/独立批/状态表）+ 宏/撤销（命令面）
/// + 会话（只读门/限流）+ 目录导出 + Staging 纯函数管道 + 审计/幂等落表。
/// </summary>
public class OrchestrationTests
{
    private static (EngineCore Engine, LinkPocketDbContextFactory Factory, string Db) CreateEngine(
        bool withOrchestration = false, params ICommandHandler[] extra)
    {
        var (factory, path) = TestEnv.CreateDb();
        var engine = TestEnv.CreateEngine(factory, withOrchestration, sessions: null, extra);
        return (engine, factory, path);
    }

    // ===== 批：事务语义 =====

    [Fact]
    public async Task Batch_Transactional_Commits_All_Steps()
    {
        var (engine, _, path) = CreateEngine();
        try
        {
            var batch = new BatchEngine(engine);
            var report = await batch.RunAsync(new BatchScript("成功批",
            [
                new BatchStep("a", "test.add_folder", JsonSerializer.SerializeToElement(new { name = "甲" })),
                new BatchStep("b", "test.add_folder", JsonSerializer.SerializeToElement(new { name = "乙" })),
            ]));

            Assert.True(report.Ok);
            Assert.Equal(2, report.Steps.Count);
            Assert.All(report.Steps, r => Assert.True(r.Ok));
            Assert.Equal(2, await engine.QueryAsync<int>("test.count_folders"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Batch_Transactional_Abort_Rolls_Back_All_Steps()
    {
        var (engine, _, path) = CreateEngine();
        try
        {
            var batch = new BatchEngine(engine);
            var script = new BatchScript("回滚批",
            [
                new BatchStep("a", "test.add_folder", JsonSerializer.SerializeToElement(new { name = "会被回滚" })),
                new BatchStep("b", "test.fail", JsonEmpty()),
            ]);

            var ex = await Assert.ThrowsAsync<EngineException>(() => batch.RunAsync(script));
            Assert.Equal(EngineErrors.BatchAborted, ex.Error.Code);
            Assert.Equal(0, await engine.QueryAsync<int>("test.count_folders"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Batch_Continue_Policy_Keeps_Good_Steps()
    {
        var (engine, _, path) = CreateEngine();
        try
        {
            var batch = new BatchEngine(engine);
            var report = await batch.RunAsync(new BatchScript("continue批",
            [
                new BatchStep("a", "test.add_folder", JsonSerializer.SerializeToElement(new { name = "甲" }), ErrorPolicy.Continue),
                new BatchStep("bad", "test.fail", JsonEmpty(), ErrorPolicy.Continue),
                new BatchStep("c", "test.add_folder", JsonSerializer.SerializeToElement(new { name = "乙" }), ErrorPolicy.SkipAndLog),
            ]));

            Assert.False(report.Ok);
            Assert.False(report.Steps[1].Ok);
            Assert.Equal(EngineErrors.EntityNotFound, report.Steps[1].ErrorCode);
            Assert.True(report.Steps[2].Ok);
            Assert.Equal(2, await engine.QueryAsync<int>("test.count_folders"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Batch_DryRun_Produces_No_Writes_And_No_Events()
    {
        var (engine, _, path) = CreateEngine();
        try
        {
            var events = new List<DomainEvent>();
            engine.Events.Subscribe(events.Add);

            var batch = new BatchEngine(engine);
            var report = await batch.DryRunAsync(new BatchScript("dry批",
            [
                new BatchStep("a", "test.add_folder", JsonSerializer.SerializeToElement(new { name = "不落库" })),
            ]));

            Assert.True(report.Ok);
            Assert.Equal(0, await engine.QueryAsync<int>("test.count_folders"));
            Assert.Empty(events);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Batch_Ref_Template_Passes_Earlier_Results()
    {
        var (engine, _, path) = CreateEngine(extra: new EchoHandler());
        try
        {
            var batch = new BatchEngine(engine);
            var report = await batch.RunAsync(new BatchScript("模板批",
            [
                new BatchStep("first", "test.echo", JsonEmpty()),
                new BatchStep("second", "test.echo", JsonSerializer.SerializeToElement(new
                {
                    whole = "{first}",
                    id_ref = "{first.data_id}",
                    inline = "前缀-{first.data_id}-后缀",
                    not_a_ref = "{not-a-ref}",
                })),
            ]));

            Assert.True(report.Ok);
            var echoed = ((JsonElement?)report.Steps[1].Data)!.Value.GetProperty("echo");
            Assert.Equal("TEMPLATE_ID", echoed.GetProperty("id_ref").GetString());
            Assert.Equal("TEMPLATE_ID", echoed.GetProperty("whole").GetProperty("data_id").GetString());
            Assert.Equal("前缀-TEMPLATE_ID-后缀", echoed.GetProperty("inline").GetString());
            Assert.Equal("{not-a-ref}", echoed.GetProperty("not_a_ref").GetString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Batch_Independent_Scope_Commits_Earlier_Steps()
    {
        var (engine, _, path) = CreateEngine();
        try
        {
            var batch = new BatchEngine(engine);
            var script = new BatchScript("独立批",
            [
                new BatchStep("a", "test.add_folder", JsonSerializer.SerializeToElement(new { name = "独立甲" })),
                new BatchStep("b", "test.fail", JsonEmpty()),
            ], BatchScope.Independent);

            var ex = await Assert.ThrowsAsync<EngineException>(() => batch.RunAsync(script));
            Assert.Equal(EngineErrors.BatchAborted, ex.Error.Code);
            Assert.Equal(1, await engine.QueryAsync<int>("test.count_folders"));   // 已执行步骤保持生效
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Batch_Status_Reflects_Lifecycle()
    {
        var (engine, _, path) = CreateEngine();
        try
        {
            var batch = new BatchEngine(engine);
            var report = await batch.RunAsync(new BatchScript("状态批",
            [
                new BatchStep("a", "test.add_folder", JsonSerializer.SerializeToElement(new { name = "甲" })),
            ]));

            var status = batch.GetStatus(report.BatchId);
            Assert.NotNull(status);
            Assert.Equal("completed", status.State);
            Assert.Equal(1, status.CompletedSteps);
            Assert.Null(batch.GetStatus("不存在的批"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Batch_Template_Unknown_Ref_Throws()
    {
        var (engine, _, path) = CreateEngine();
        try
        {
            var batch = new BatchEngine(engine);
            var script = new BatchScript("坏引用批",
            [
                new BatchStep("a", "test.add_folder", JsonSerializer.SerializeToElement(new { name = "{ghost.id}" })),
            ]);

            var ex = await Assert.ThrowsAsync<EngineException>(() => batch.RunAsync(script));
            Assert.Equal(EngineErrors.TypeMismatch, ex.Error.Code);
        }
        finally { Cleanup(path); }
    }

    // ===== 宏（命令面）=====

    [Fact]
    public async Task Macro_Save_Run_Delete_RoundTrip()
    {
        var (engine, _, path) = CreateEngine(withOrchestration: true);
        try
        {
            var saved = await engine.ExecuteAsync<JsonElement>("macro.save", new
            {
                name = "技能甲",
                script = JsonSerializer.SerializeToElement(new BatchScript("宏脚本",
                [
                    new BatchStep("mf", "test.add_folder", JsonSerializer.SerializeToElement(new { name = "宏建的" })),
                ]), EngineJson.ScriptOptions),
            });
            Assert.True(saved.Ok);

            var list = await engine.QueryAsync<JsonElement>("macro.list");
            Assert.Contains(list.GetProperty("macros").EnumerateArray(),
                m => m.GetProperty("name").GetString() == "技能甲");

            var run = await engine.ExecuteAsync<JsonElement>("macro.run", new { name = "技能甲" });
            Assert.True(run.Ok);
            Assert.Equal(1, await engine.QueryAsync<int>("test.count_folders"));

            var got = await engine.QueryAsync<JsonElement>("macro.get", new { name = "技能甲" });
            Assert.Equal("宏脚本", got.GetProperty("name").GetString());

            await engine.ExecuteAsync<JsonElement>("macro.delete", new { name = "技能甲" });
            var gone = await Assert.ThrowsAsync<EngineException>(
                () => engine.QueryAsync<JsonElement>("macro.get", new { name = "技能甲" }));
            Assert.Equal(EngineErrors.EntityNotFound, gone.Error.Code);
        }
        finally { Cleanup(path); }
    }

    // ===== 撤销 / 重做（命令面）=====

    [Fact]
    public async Task Undo_Undo_Redo_Lifecycle()
    {
        TrashPairHandlers.Reset();
        var (engine, _, path) = CreateEngine(withOrchestration: true, new TrashPairHandlers(), new TrashCreateHandler(), new TrashRestorePairHandler());
        try
        {
            var id = await engine.ExecuteAsync<JsonElement>("test.create", new { name = "撤销对象" });
            var itemId = id.Data.GetProperty("id").GetString()!;
            Assert.Empty(await UndoListAsync(engine));   // test.create 无 UndoInverse，不入栈

            await engine.ExecuteAsync<object>("test.trash", new { id = itemId });
            var entries = await UndoListAsync(engine);
            Assert.Single(entries);
            Assert.Equal("test.trash", entries[0].Command);
            Assert.Equal("test.restore", entries[0].InverseCommand);

            await engine.ExecuteAsync<JsonElement>("undo.undo", null);   // 撤销 = restore
            Assert.Equal(0, TrashPairHandlers.State);

            await engine.ExecuteAsync<JsonElement>("undo.redo", null);   // 重做 = 重放 trash
            Assert.Equal(1, TrashPairHandlers.State);
        }
        finally { TrashPairHandlers.Reset(); Cleanup(path); }
    }

    [Fact]
    public async Task Undo_Clear_Wipes_Stack()
    {
        TrashPairHandlers.Reset();
        var (engine, _, path) = CreateEngine(withOrchestration: true, new TrashPairHandlers(), new TrashCreateHandler(), new TrashRestorePairHandler());
        try
        {
            var id = await engine.ExecuteAsync<JsonElement>("test.create", new { name = "甲" });
            await engine.ExecuteAsync<object>("test.trash", new { id = id.Data.GetProperty("id").GetString() });
            Assert.Single(await UndoListAsync(engine));

            await engine.ExecuteAsync<int>("undo.clear", null);
            Assert.Empty(await UndoListAsync(engine));
        }
        finally { TrashPairHandlers.Reset(); Cleanup(path); }
    }

    private static async Task<IReadOnlyList<UndoEntry>> UndoListAsync(EngineCore engine)
    {
        var list = await engine.QueryAsync<JsonElement>("undo.list");
        return list.GetProperty("entries").EnumerateArray()
            .Select(e => new UndoEntry(
                e.GetProperty("id").GetString()!,
                DateTimeOffset.Parse(e.GetProperty("at").GetString()!),
                e.GetProperty("command").GetString()!,
                e.GetProperty("args").Clone(),
                e.GetProperty("inverse_command").GetString()!,
                e.GetProperty("inverse_args").Clone(),
                new CallerRef(CallerKind.Test, e.GetProperty("caller").GetProperty("session_id").GetString())))
            .ToList();
    }

    // ===== 会话（能力门）=====

    [Fact]
    public async Task Session_Readonly_Rejects_Mutations_Allows_Queries()
    {
        var (factory, path) = TestEnv.CreateDb();
        var sessions = new SessionManager();
        var engine = TestEnv.CreateEngine(factory, withOrchestration: false, sessions);
        try
        {
            var session = await sessions.BeginAsync(new SessionProfile(SessionKind.AgentReadonly));
            try
            {
                var count = await engine.QueryAsync<int>("test.count_folders",
                    null, new CallOptions(Caller: new CallerRef(CallerKind.Agent, session.SessionId)));
                Assert.Equal(0, count);

                var ex = await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<JsonElement>(
                    "test.add_folder", new { name = "越权" },
                    new CallOptions(Caller: new CallerRef(CallerKind.Agent, session.SessionId))));
                Assert.Equal(EngineErrors.ReadonlySession, ex.Error.Code);
            }
            finally { await sessions.EndAsync(session.SessionId); }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Session_RateLimit_Trips_And_Reports_Retry()
    {
        var (factory, path) = TestEnv.CreateDb();
        var sessions = new SessionManager();
        var engine = TestEnv.CreateEngine(factory, withOrchestration: false, sessions);
        try
        {
            var session = await sessions.BeginAsync(new SessionProfile(SessionKind.Agent, RateLimitPerMinute: 2));
            try
            {
                var caller = new CallerRef(CallerKind.Agent, session.SessionId);
                await engine.QueryAsync<int>("test.count_folders", null, new CallOptions(Caller: caller));
                await engine.QueryAsync<int>("test.count_folders", null, new CallOptions(Caller: caller));

                var ex = await Assert.ThrowsAsync<EngineException>(
                    () => engine.QueryAsync<int>("test.count_folders", null, new CallOptions(Caller: caller)));
                Assert.Equal(EngineErrors.RateLimited, ex.Error.Code);
                Assert.True(ex.Error.Retryable);
                Assert.True(ex.Error.Details!.Value.GetProperty("retry_after_ms").GetInt32() > 0);
            }
            finally { await sessions.EndAsync(session.SessionId); }
        }
        finally { Cleanup(path); }
    }

    // ===== 目录导出 =====

    [Fact]
    public void Catalog_Exports_All_Three_Formats()
    {
        var registry = new CommandRegistry();
        registry.Register(new EchoHandler());
        var catalog = new EngineCatalog(registry, includeBatch: true);

        var manifest = catalog.Manifest();
        Assert.Equal(1 + BatchEngine.Descriptors.Count, manifest.Commands.Count);
        Assert.Contains(manifest.Commands, c => c.Name == "batch.run");

        var tools = catalog.Export(ManifestFormat.FunctionCalling);
        Assert.Contains("batch.run", tools);
        Assert.Contains("test.echo", tools);

        var openapi = catalog.Export(ManifestFormat.OpenApiLite);
        Assert.Contains("3.1.0-lite", openapi);

        var docs = catalog.Export(ManifestFormat.MarkdownDocs);
        Assert.Contains("`batch.dry_run`", docs);
        Assert.Contains("| 命令 | 类型 | 能力 | 参数 | 说明 |", docs);
    }

    // ===== Staging 纯函数管道 =====

    [Fact]
    public async Task Staging_Transform_Pipeline_DryRun_And_Persist()
    {
        var root = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpstaging_{Guid.NewGuid():N}");
        var source = Path.Combine(root, "src.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source,
            """[{"url":"https://a.example/1","title":"甲","folder":"旧"},{"url":"https://a.example/1","title":"重复"},{"url":"https://a.example/2","title":"乙","folder":"旧"}]""");
        try
        {
            var staging = new StagingService(root);
            var staged = await staging.StageAsync(source, default);
            Assert.Equal(64, staged.Sha256.Length);

            var dry = await staging.TransformAsync(staged.StagingId,
            [
                new TransformOp("dedupe", JsonSerializer.SerializeToElement(new { by = "url" })),
                new TransformOp("rename_folder", JsonSerializer.SerializeToElement(new { from = "旧", to = "新" })),
            ], dryRun: true, ct: default);
            Assert.Equal(3, dry.ItemsBefore);
            Assert.Equal(2, dry.ItemsAfter);
            Assert.True(dry.DryRun);
            Assert.NotNull(dry.PreviewJson);

            var live = await staging.TransformAsync(staged.StagingId,
            [
                new TransformOp("dedupe", JsonSerializer.SerializeToElement(new { by = "url" })),
                new TransformOp("rename_folder", JsonSerializer.SerializeToElement(new { from = "旧", to = "新" })),
            ], dryRun: false, ct: default);
            Assert.Equal(2, live.ItemsAfter);
            Assert.False(live.DryRun);

            var text = await staging.ReadTextAsync(staged.StagingId, default);
            Assert.Contains("新", text);
            Assert.DoesNotContain("重复", text);

            Assert.True(await staging.DiscardAsync(staged.StagingId, default));
            Assert.False(await staging.DiscardAsync(staged.StagingId, default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ===== 审计 / 幂等落表 =====

    [Fact]
    public async Task Audit_And_Idempotency_Persist_To_Sqlite()
    {
        var (engine, factory, path) = CreateEngine();
        try
        {
            var sql = new SqlAuditWriter(() => factory.CreateDbContext());
            sql.Write(new AuditEntry(DateTimeOffset.Now, "test.add_folder", "corr-1", CallerRef.Test,
                5, Success: true, ErrorCode: null, Changes: null, DryRun: false, IsNested: false,
                StackTrace: null, ArgsJson: "{\"name\":\"甲\"}", BatchId: "batch-1"));

            var idem = new SqlIdempotencyStore(() => factory.CreateDbContext());
            idem.Store("key-1", new CommandResult("第一次", null, "audit-1"));
            Assert.True(idem.TryGet("key-1", out var cached));   // 内存命中
            Assert.Equal("第一次", cached.Data);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            await db.OpenAsync();
            Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM audit_log WHERE correlation_id = 'corr-1' AND batch_id = 'batch-1'"));
            Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM idempotency WHERE key = 'key-1'"));

            var fresh = new SqlIdempotencyStore(() => factory.CreateDbContext());
            Assert.True(fresh.TryGet("key-1", out var fromTable));   // 表命中（跨实例，Data 以 JSON 形态还原）
            Assert.Equal("第一次", fromTable.Data?.ToString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Idempotency_Sql_Store_Restores_Typed_Result_Across_Instances()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var key = "typed-idem-1";

            // 实例 1：写库（活对象命中路径）
            var registry1 = new CommandRegistry();
            registry1.RegisterAll(new ICommandHandler[] { new CountFoldersHandler(), new AddFolderHandler() });
            var engine1 = new EngineCore(registry1, () => new EfUnitOfWork(factory.CreateDbContext()),
                idempotency: new SqlIdempotencyStore(() => factory.CreateDbContext()));
            var first = await engine1.ExecuteAsync<string>("test.add_folder", new { name = "甲" },
                new CallOptions(IdempotencyKey: key));
            Assert.False(string.IsNullOrEmpty(first.Data));

            // 实例 2（全新 store，内存缓存为空）：幂等命中必须走 SQL 还原（Data = JsonElement）。
            // 旧代码 (T?)JsonElement 强转会抛 InvalidCastException，跨重启幂等必炸。
            var registry2 = new CommandRegistry();
            registry2.RegisterAll(new ICommandHandler[] { new CountFoldersHandler(), new AddFolderHandler() });
            var engine2 = new EngineCore(registry2, () => new EfUnitOfWork(factory.CreateDbContext()),
                idempotency: new SqlIdempotencyStore(() => factory.CreateDbContext()));
            var second = await engine2.ExecuteAsync<string>("test.add_folder", new { name = "甲" },
                new CallOptions(IdempotencyKey: key));
            Assert.Equal(first.Data, second.Data);

            // 幂等命中 → 不重复执行（库里只有一个「甲」）
            Assert.Equal(1, await engine2.QueryAsync<int>("test.count_folders"));
        }
        finally { Cleanup(path); }
    }

    // ===== 辅助 =====

    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");
    private static JsonElement JsonEmpty() => EmptyObject;

    private static async Task<long> ScalarAsync(Microsoft.Data.Sqlite.SqliteConnection db, string sql)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result ?? 0L);
    }

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

/// <summary>回声命令：返回收到的参数 + 固定 data_id（模板断言用）。</summary>
internal sealed class EchoHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } =
        new("test.echo", "test", "回声", [], CommandCaps.Mutation);

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
        => Task.FromResult(CommandResult.Ok(
            JsonSerializer.SerializeToElement(new { data_id = "TEMPLATE_ID", echo = args.Clone() })));
}

/// <summary>test.create（占位创建；无逆向不入撤销栈）。</summary>
internal sealed class TrashCreateHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } =
        new("test.create", "test", "创建占位对象", [], CommandCaps.Mutation);

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
        => Task.FromResult(CommandResult.Ok(JsonSerializer.SerializeToElement(new { id = "obj-1" })));
}

/// <summary>test.trash（Reversible，逆向 test.restore；内存 State 1 = 在回收站）。</summary>
internal sealed class TrashPairHandlers : ICommandHandler
{
    public static int State;

    public static void Reset() => State = 0;

    public CommandDescriptor Descriptor { get; } =
        new("test.trash", "test", "入回收站（可逆，逆向 restore）", [],
            CommandCaps.Mutation | CommandCaps.Reversible, UndoInverse: "test.restore");

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        State = 1;
        return Task.FromResult(CommandResult.Ok("trashed",
            ChangeSet.Of(new EntityRef("test", "x"), "test.changed", "已入回收站")));
    }
}

/// <summary>test.restore（test.trash 的逆向；State 0 = 已还原）。</summary>
internal sealed class TrashRestorePairHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } =
        new("test.restore", "test", "从回收站还原", [],
            CommandCaps.Mutation | CommandCaps.Reversible, UndoInverse: "test.trash");

    public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        TrashPairHandlers.State = 0;
        return Task.FromResult(CommandResult.Ok("restored",
            ChangeSet.Of(new EntityRef("test", "x"), "test.changed", "已还原")));
    }
}
