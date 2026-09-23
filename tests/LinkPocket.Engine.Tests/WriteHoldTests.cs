using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 写入冻结（写锁，LP.SEC.006）：AI 改数据期间**只放行持锁会话的写**——界面/宿主（不带 SessionId）
/// 与其它会话的写一律被拒；读不受影响；Dispose 释放；超龄由下一次校验强制解除并留痕（不静默）。
/// </summary>
public class WriteHoldTests
{
    [Fact]
    public async Task 写锁_持有期间界面与其它会话的写被拒_持锁会话可写_读不受影响()
    {
        var (factory, path) = TestEnv.CreateDb();
        var sessions = new SessionManager();
        var engine = TestEnv.CreateEngine(factory, withOrchestration: false, sessions);
        try
        {
            var agent = await sessions.BeginAsync(new SessionProfile(SessionKind.Agent));
            var agentCaller = new CallerRef(CallerKind.Agent, agent.SessionId);

            using (sessions.BeginWriteHold(agent.SessionId, "AI turn"))
            {
                // 界面 / 宿主的写（不带 SessionId）：被拒
                var uiWrite = await Assert.ThrowsAsync<EngineException>(
                    () => engine.ExecuteAsync<string>("test.add_folder", new { name = "ui" }));
                Assert.Equal(EngineErrors.WriteFrozenByAgent, uiWrite.Error.Code);
                Assert.Equal(agent.SessionId,
                    uiWrite.Error.Details!.Value.GetProperty("holder_session").GetString());

                // 另一个会话的写：被拒
                var other = await sessions.BeginAsync(new SessionProfile(SessionKind.Agent));
                var otherWrite = await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<JsonElement>(
                    "test.add_folder", new { name = "other" },
                    new CallOptions(Caller: new CallerRef(CallerKind.Agent, other.SessionId))));
                Assert.Equal(EngineErrors.WriteFrozenByAgent, otherWrite.Error.Code);
                await sessions.EndAsync(other.SessionId);

                // 持锁会话的写：放行
                await engine.ExecuteAsync<string>("test.add_folder", new { name = "agent" },
                    new CallOptions(Caller: agentCaller));

                // 读不受影响（界面调用与持锁会话都可读）
                Assert.Equal(1, await engine.QueryAsync<int>("test.count_folders"));
                Assert.Equal(1, await engine.QueryAsync<int>("test.count_folders", null,
                    new CallOptions(Caller: agentCaller)));
            }

            // Dispose 之后恢复
            Assert.Null(sessions.CurrentWriteHold);
            await engine.ExecuteAsync<string>("test.add_folder", new { name = "ui2" });
            Assert.Equal(2, await engine.QueryAsync<int>("test.count_folders"));
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 写锁_超龄由下一次校验强制解除_不静默()
    {
        var (factory, path) = TestEnv.CreateDb();
        var sessions = new SessionManager(writeHoldMaxAge: TimeSpan.Zero);
        var engine = TestEnv.CreateEngine(factory, withOrchestration: false, sessions);
        try
        {
            var session = await sessions.BeginAsync(new SessionProfile(SessionKind.Agent));
            using var hold = sessions.BeginWriteHold(session.SessionId, "AI turn");
            Assert.NotNull(sessions.CurrentWriteHold);   // 快照不改状态

            // 校验路径触发超龄解除 → 写放行（解除已在日志留痕，见 SessionManager.ActiveHold）
            await engine.ExecuteAsync<string>("test.add_folder", new { name = "after-expiry" });
            Assert.Null(sessions.CurrentWriteHold);
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 写锁_批入口同样受冻结约束()
    {
        var (factory, path) = TestEnv.CreateDb();
        var sessions = new SessionManager();
        var engine = TestEnv.CreateEngine(factory, withOrchestration: true, sessions);
        try
        {
            var script = new BatchScript("frozen",
                [new BatchStep("mk", "test.add_folder", JsonSerializer.SerializeToElement(new { name = "批" }),
                    ErrorPolicy.Abort)]);

            var holder = await sessions.BeginAsync(new SessionProfile(SessionKind.Agent));
            using (sessions.BeginWriteHold(holder.SessionId, "AI turn"))
            {
                // 界面/宿主发起的批（不带会话）：被冻结拦住（否则可借批绕过冻结）
                var ex = await Assert.ThrowsAsync<EngineException>(
                    () => engine.Batch!.RunAsync(script, new CallOptions(CorrelationId: "corr-batch-frozen")));
                Assert.Equal(EngineErrors.WriteFrozenByAgent, ex.Error.Code);

                // 持锁会话的批：放行
                var report = await engine.Batch!.RunAsync(script,
                    new CallOptions(Caller: new CallerRef(CallerKind.Agent, holder.SessionId)));
                Assert.True(report.Ok);
            }
        }
        finally { TestEnv.Cleanup(path); }
    }
}
