using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel.Commands;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// P3-5（嵌套审计补载荷 / G4 收口）：
/// 嵌套步骤的审计子记录与批父条目都带**入参快照**（先脱敏后截断，与顶层同口径）；
/// 嵌套步骤经上下文透传 <c>batch_id</c>、独立批步骤经 <c>CallOptions.BatchId</c> 携带——
/// <c>audit.query {batch_id}</c> 能取齐父条目与每一步（含失败步）。
/// </summary>
public class NestedAuditPayloadTests
{
    private static (EngineCore Engine, InMemoryAuditWriter Audit, string Db) Create()
    {
        var (factory, path) = TestEnv.CreateDb();
        var audit = new InMemoryAuditWriter();
        var engine = TestEnv.CreateEngineWith(factory, withOrchestration: true,
            sessions: null, audit: audit, new MarkHandler(), new UnmarkHandler());
        return (engine, audit, path);
    }

    private static BatchScript Script(params string[] tags)
        => new("批", tags.Select((tag, i) => new BatchStep($"s{i}", "test.mark",
            JsonSerializer.SerializeToElement(new { tag }))).ToArray());

    // ===== 嵌套步骤：入参快照 + batch_id 透传 =====

    [Fact]
    public async Task 事务批_每步嵌套审计_带入参快照与批ID()
    {
        var (engine, audit, path) = Create();
        try
        {
            var report = await engine.Batch!.RunAsync(Script("A", "B"));
            Assert.True(report.Ok);

            var nested = audit.Snapshot().Where(e => e.IsNested).ToArray();
            Assert.Equal(2, nested.Length);
            Assert.All(nested, e =>
            {
                Assert.Equal(report.BatchId, e.BatchId);                          // batch_id 透传到每一步
                Assert.NotNull(e.ArgsJson);                                       // 入参快照不再缺失
                Assert.Contains("tag", e.ArgsJson);
                Assert.False(e.ArgsTruncated);
            });
            Assert.Contains(nested, e => e.ArgsJson!.Contains("\"A\""));
            Assert.Contains(nested, e => e.ArgsJson!.Contains("\"B\""));
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 批父条目_带脚本入参快照与批ID()
    {
        var (engine, audit, path) = Create();
        try
        {
            var report = await engine.Batch!.RunAsync(Script("A"));

            var parent = Assert.Single(audit.Snapshot(), e => !e.IsNested && e.Command == "batch.run");
            Assert.Equal(report.BatchId, parent.BatchId);
            Assert.NotNull(parent.ArgsJson);                                      // 批脚本本身入账
            Assert.Contains("script", parent.ArgsJson);
            Assert.Contains("test.mark", parent.ArgsJson);
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 独立批_步骤走顶层管道_审计经CallOptions携带批ID()
    {
        var (engine, audit, path) = Create();
        try
        {
            var script = new BatchScript("独立批",
            [
                new BatchStep("a", "test.mark", JsonSerializer.SerializeToElement(new { tag = "A" })),
                new BatchStep("b", "test.mark", JsonSerializer.SerializeToElement(new { tag = "B" })),
            ], BatchScope.Independent);
            var report = await engine.Batch!.RunAsync(script);
            Assert.True(report.Ok);

            // 独立批步骤是顶层条目（IsNested=false），batch_id 经 CallOptions 携带
            var steps = audit.Snapshot().Where(e => e.Command == "test.mark").ToArray();
            Assert.Equal(2, steps.Length);
            Assert.All(steps, e =>
            {
                Assert.False(e.IsNested);
                Assert.Equal(report.BatchId, e.BatchId);
                Assert.NotNull(e.ArgsJson);
            });

            var parent = Assert.Single(audit.Snapshot(), e => e.Command == "batch.run");
            Assert.Equal(report.BatchId, parent.BatchId);
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 宏_步骤审计批ID_与父条目同键取齐()
    {
        var (engine, audit, path) = Create();
        try
        {
            await engine.ExecuteAsync<JsonElement>("macro.save", new
            {
                name = "打标宏",
                script = JsonSerializer.SerializeToElement(Script("X", "Y")),
            });
            var run = await engine.ExecuteAsync<JsonElement>("macro.run", new { name = "打标宏" });
            var entries = audit.Snapshot();

            // 宏的批运行键 = macro.run 父条目的 correlation（与 BatchReport.BatchId 同口径）
            var parent = Assert.Single(entries, e => e.Command == "macro.run" && !e.IsNested);
            Assert.NotNull(parent.BatchId);

            var nested = entries.Where(e => e.IsNested && e.Command == "test.mark").ToArray();
            Assert.Equal(2, nested.Length);
            Assert.All(nested, e =>
            {
                Assert.Equal(parent.BatchId, e.BatchId);                           // 同一键取齐父与每一步
                Assert.NotNull(e.ArgsJson);
            });
            _ = run;
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 嵌套失败步骤_同样留痕_带错误码入参与批ID()
    {
        var (engine, audit, path) = Create();
        try
        {
            // Continue 策略：失败步之后批照常完成——失败步必须在审计里查得到（取齐每一步）
            var script = new BatchScript("容错批",
            [
                new BatchStep("ok", "test.mark", JsonSerializer.SerializeToElement(new { tag = "A" })),
                new BatchStep("bad", "test.fail", JsonSerializer.SerializeToElement(new { reason = "boom" }),
                    ErrorPolicy.Continue),
            ]);
            var report = await engine.Batch!.RunAsync(script);
            Assert.False(report.Ok);

            var failed = Assert.Single(audit.Snapshot(), e => e.Command == "test.fail");
            Assert.True(failed.IsNested);                                          // 事务批步骤恒为嵌套子记录
            Assert.False(failed.Success);
            Assert.Equal(EngineErrors.EntityNotFound, failed.ErrorCode);
            Assert.Equal(report.BatchId, failed.BatchId);
            Assert.NotNull(failed.ArgsJson);                                       // 失败步的入参也入账
            Assert.Contains("reason", failed.ArgsJson);
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 非批嵌套_无批上下文_入参照记批ID为空()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var audit = new InMemoryAuditWriter();
            var engine = TestEnv.CreateEngineWith(factory, withOrchestration: false,
                sessions: null, audit: audit);

            var result = await engine.ExecuteAsync<string>("test.nested_add", new { name = "nested-dir" });
            Assert.True(result.Ok);

            var nested = Assert.Single(audit.Snapshot(), e => e.IsNested);
            Assert.NotNull(nested.ArgsJson);                                       // 普通嵌套同样补载荷
            Assert.Contains("nested-dir", nested.ArgsJson);
            Assert.Null(nested.BatchId);                                           // 非编排上下文不伪造批ID
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 嵌套入参_敏感键先脱敏后截断()
    {
        var (engine, audit, path) = Create();
        try
        {
            // 嵌套快照与顶层同口径：LogRedactor 掩码敏感键的值（先脱敏后截断）
            var script = new BatchScript("含密钥批",
            [
                new BatchStep("s0", "test.mark", JsonSerializer.SerializeToElement(
                    new { tag = "A", api_key = "sk-secret-013", token = "tok-013" })),
            ]);
            await engine.Batch!.RunAsync(script);

            var nested = Assert.Single(audit.Snapshot(), e => e.IsNested);
            Assert.NotNull(nested.ArgsJson);
            Assert.DoesNotContain("sk-secret-013", nested.ArgsJson);               // 敏感值不落审计
            Assert.DoesNotContain("tok-013", nested.ArgsJson);
            Assert.Contains("***", nested.ArgsJson);
            Assert.Contains("tag", nested.ArgsJson);                                // 非敏感字段照常入账
        }
        finally { TestEnv.Cleanup(path); }
    }
}
