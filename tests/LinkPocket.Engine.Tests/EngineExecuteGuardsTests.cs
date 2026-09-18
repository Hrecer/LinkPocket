using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Engine;
using LinkPocket.Kernel.Commands;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// ExecuteAsync 不变量补强（2026-09-18）：
/// ① 同一 IdempotencyKey 的两并发请求：写闸内二次确认保证只执行一次（曾只在闸外查一次，
///    并发双写会各执行一遍，幂等保证失效）；
/// ② 取消路径也落审计（观测面「所有调用可追溯」不把取消当例外）。
/// </summary>
public class EngineExecuteGuardsTests
{
    private static void CleanupDb(string path)
    {
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch
        {
            // 池清理失败不阻断文件删除
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = path + suffix;
            try { if (File.Exists(file)) File.Delete(file); } catch { /* 句柄滞留 → 系统清理 */ }
        }
    }

    [Fact]
    public async Task 同一幂等键两并发请求_闸内二次确认_只执行一次()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            // SlowWrite 把第一个请求钉在 handler 内（已拿闸、未提交）——第二个请求的闸外初查
            // 必落在提交前（都可 miss），随后只能靠闸内二次确认命中首次结果，从而确定性复现竞态窗口。
            var slow = new SlowWriteHandler();
            var engine = TestEnv.CreateEngine(factory, slow);
            var options = new CallOptions { IdempotencyKey = "concurrent-slow-key" };

            var first = engine.ExecuteAsync<object>("test.slow_write", null, options);
            await slow.Started.Task;                        // 第一个已在写闸内执行中
            var second = engine.ExecuteAsync<object>("test.slow_write", null, options);   // 闸外初查必然 miss → 等闸
            slow.Finish.TrySetResult();                     // 放行第一个：提交 + 幂等记录

            var results = await Task.WhenAll(first, second);
            Assert.All(results, r => Assert.True(r.Ok));

            // SlowWrite 每次执行都会新增一个文件夹：只执行一次 → 库中恰 1 个（修复前为 2）
            Assert.Equal(1, await engine.QueryAsync<int>("test.count_folders", null));
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task 取消调用_写Cancelled审计条目_不丢追溯()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var audit = new InMemoryAuditWriter();
            var slow = new SlowWriteHandler();
            var registry = new CommandRegistry();
            registry.RegisterAll(new ICommandHandler[] { slow });
            var engine = new EngineCore(registry, () => new EfUnitOfWork(factory.CreateDbContext()), audit: audit);

            using var cts = new CancellationTokenSource();
            var task = engine.ExecuteAsync<object>("test.slow_write", null, new CallOptions(), cts.Token);
            await slow.Started.Task;    // 把请求钉在 handler 内（等待中被取消）
            cts.Cancel();

            await Assert.ThrowsAsync<EngineException>(() => task);

            var entry = Assert.Single(audit.Snapshot());
            Assert.False(entry.Success);
            Assert.Equal(EngineErrors.Cancelled, entry.ErrorCode);
        }
        finally
        {
            CleanupDb(path);
        }
    }
}