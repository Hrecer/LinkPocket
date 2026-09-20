using LinkPocket.Composition;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using LinkPocket.Modules.Maintenance;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// **correlation 贯通**（S3）：一次用户动作 = 一个 correlation_id，从消费者（<c>EngineClient</c>）一路落到
/// 调用日志 / 引擎里程碑 / 审计行——于是 <c>audit.query { correlation_id }</c> 与
/// <c>logs.query { correlation_id }</c> 用**同一把钥匙**取回同一条链路（"一次拖拽 10 项 = 10 条命令"
/// 由此从十条孤立记录变成一条线索）。
/// <para>⚠️ 与 <see cref="LogCommandTests"/> 同属一个 xunit Collection（LpLog 是进程级静态）；
/// 断言只针对本用例自己造的动作（唯一 correlation），不受并行噪音影响。</para>
/// </summary>
[Collection("日志管道")]
public class EngineCallCorrelationTests
{
    [Fact]
    public async Task 动作作用域_多条命令共用一条相关_审计侧与日志侧都能取齐()
    {
        var dir = TempDir();
        EngineComposer.ConfigureLogging(new LoggingOptions { Directory = dir, MinimumLevel = LogLevel.Info });
        var (composition, _) = TestHost.CreateWithAuditAndClient();
        var client = composition.Client;
        try
        {
            string correlation;
            using (var action = client.BeginAction("建两个目录"))
            {
                correlation = action.CorrelationId;
                Assert.NotEmpty(correlation);

                await client.FolderCreateAsync("甲");
                await client.FolderCreateAsync("乙");

                Assert.Equal(2, action.Calls);      // 计数如实（客户端每完成一次登记一次）
                Assert.Equal(0, action.Failures);
            }

            // 审计侧：两条命令落在同一条 correlation 上（引擎侧审计用的是同一个 CallOptions）
            var audit = await client.QueryAsync<AuditPagedResult>("audit.query", new { correlation_id = correlation });
            Assert.Equal(2, audit.Total);
            Assert.All(audit.Items, row => Assert.Equal(correlation, row.CorrelationId));
            Assert.Contains(audit.Items, row => row.Command == "folders.create");

            // 日志侧：调用记录（含 audit_ref）与动作汇总也带同一条 correlation（首类字段）
            var logs = await client.QueryAsync<LogQueryResult>("logs.query", new { correlation_id = correlation });
            Assert.All(logs.Items, record => Assert.Equal(correlation, record.CorrelationId));
            Assert.Contains(logs.Items, record => record.Category == "engine.call"
                && record.Command == "folders.create" && record.Props!.ContainsKey("audit_ref"));
            Assert.Contains(logs.Items, record => record.Category == EngineCallScope.LogCategory
                && record.Message.Contains("建两个目录"));
        }
        finally
        {
            LpLog.Shutdown();
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task 单独调用_显式相关优先_未传则自动生成且留下调用记录()
    {
        var dir = TempDir();
        EngineComposer.ConfigureLogging(new LoggingOptions { Directory = dir, MinimumLevel = LogLevel.Info });
        var (composition, _) = TestHost.CreateWithAuditAndClient();
        var client = composition.Client;
        try
        {
            // ① 显式传 correlation：原样落到审计（调用方指定的链路键，逐字不改）
            var explicitCall = await client.FolderCreateAsync("丙", o: new CallOptions(CorrelationId: "corr-explicit"));
            Assert.True(explicitCall.Ok);
            var audit = await client.QueryAsync<AuditPagedResult>("audit.query", new { correlation_id = "corr-explicit" });
            Assert.Equal("corr-explicit", Assert.Single(audit.Items).CorrelationId);

            // ② 未传：自动补一条（≠ 复用上一条），并留下"消费者视角"的调用记录
            var autoCall = await client.FolderCreateAsync("丁");
            Assert.True(autoCall.Ok);

            var calls = await client.QueryAsync<LogQueryResult>("logs.query", new { category = "engine.call" });
            var last = calls.Items.Last(record => record.Message == "调用完成：folders.create");
            Assert.False(string.IsNullOrEmpty(last.CorrelationId));
            Assert.NotEqual("corr-explicit", last.CorrelationId);
            Assert.Equal("folders.create", last.Command);          // 命令名 = 首类字段（不是 props）
            Assert.Equal("ui:-", last.Caller);
            Assert.Contains("audit_ref", last.Props!.Keys);        // 审计引用只有消费者侧才知道
            Assert.Equal(true, last.Props!["ok"]);
        }
        finally
        {
            LpLog.Shutdown();
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task 失败调用_计入动作失败数并留错误码()
    {
        var dir = TempDir();
        EngineComposer.ConfigureLogging(new LoggingOptions { Directory = dir, MinimumLevel = LogLevel.Info });
        var (composition, _) = TestHost.CreateWithAuditAndClient();
        var client = composition.Client;
        try
        {
            string correlation;
            using (var action = client.BeginAction("含失败的动作"))
            {
                correlation = action.CorrelationId;
                await client.FolderCreateAsync("戊");
                // 未知 ID → LP.STATE.001（写流，失败同样要留痕并被计入 Failures）
                await Assert.ThrowsAsync<EngineException>(
                    () => client.FolderUpdateAsync("000000000000", name: "不存在"));

                Assert.Equal(2, action.Calls);
                Assert.Equal(1, action.Failures);                  // 失败如实计数（不吞）
            }

            var logs = await client.QueryAsync<LogQueryResult>("logs.query", new { correlation_id = correlation });
            var failed = Assert.Single(logs.Items, record => record.Message.Contains("调用失败"));
            Assert.Equal("folders.update", failed.Command);
            Assert.Equal(EngineErrors.EntityNotFound, failed.Props!["error_code"]);
        }
        finally
        {
            LpLog.Shutdown();
            TryDeleteDir(dir);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(TempArea.Resolve(), "call-corr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 测试清理尽力而为 */ }
    }
}
