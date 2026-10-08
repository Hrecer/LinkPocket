using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 跨进程变更流的金标准（<see cref="OpsChangeFeed"/>）：审计尾 = 单调整数水位。
/// 断言的是**语义契约**，不是实现细节——
/// ① 只看外部进程调用方的行（界面/应用内助手的行不算"外部变更"）；
/// ② 水位只推进、不重报（否则界面会刷屏提示）；
/// ③ 审计被重建/清空到水位之下时**水位归零**（否则变更流永久哑掉）；
/// ④ 载荷给出事件名（精确失效的判据）与实体/摘要（精确提示的依据），坏载荷如实退化为"无事件"。
/// </summary>
public class OpsChangeFeedTests
{
    [Fact]
    public void 只报外部调用方的行_水位推进且不重复()
    {
        using var fixture = new Fixture();
        var feed = new OpsChangeFeed(fixture.ContextFactory, CallerRef.ExternalAgent.ToString());

        fixture.Insert(caller: "ui:-", command: "links.update");            // 界面自己的写
        fixture.Insert(caller: "agent:s-1", command: "links.update");       // 应用内助手（带会话）
        fixture.Insert(caller: CallerRef.ExternalAgent.ToString(), command: "links.move_batch");

        var polled = feed.Poll(100);

        Assert.Single(polled);
        Assert.Equal("links.move_batch", polled[0].Command);
        Assert.Empty(feed.Poll(100));                                       // 已消费 → 不重报
    }

    [Fact]
    public void 构造时不回放历史()
    {
        using var fixture = new Fixture();
        fixture.Insert(caller: CallerRef.ExternalAgent.ToString(), command: "links.move_batch");

        var feed = new OpsChangeFeed(fixture.ContextFactory, CallerRef.ExternalAgent.ToString());

        Assert.Empty(feed.Poll(100));                                       // 界面只关心"从现在开始变了什么"
    }

    [Fact]
    public void 审计被换成更小的一份_水位归零并继续报新行()
    {
        using var fixture = new Fixture();
        var feed = new OpsChangeFeed(fixture.ContextFactory, CallerRef.ExternalAgent.ToString());

        for (var i = 0; i < 3; i++)
            fixture.Insert(caller: CallerRef.ExternalAgent.ToString(), command: "links.move_batch");
        Assert.Equal(3, feed.Poll(100).Count);
        var high = feed.Watermark;

        // 附属库被换成另一份（恢复备份 / 被别的进程重建）：行数更少 → id 区间整体落在水位之下
        fixture.ReplaceOpsDatabase();
        fixture.Insert(caller: CallerRef.ExternalAgent.ToString(), command: "folders.create");

        var after = feed.Poll(100);

        Assert.True(feed.Watermark < high);                                  // 水位必须归零重来（否则变更流永久哑掉）
        Assert.Single(after);
        Assert.Equal("folders.create", after[0].Command);
    }

    [Fact]
    public void 载荷解析_给事件与实体与摘要_坏载荷退化为无事件()
    {
        using var fixture = new Fixture();
        var feed = new OpsChangeFeed(fixture.ContextFactory, CallerRef.ExternalAgent.ToString());

        var payload = ChangeSetPayload.From(new ChangeSet(
            [new EntityRef("link", "L1"), new EntityRef("link", "L2")],
            ["links.changed"],
            "Moved 2 link(s)")).GetRawText();
        fixture.Insert(caller: CallerRef.ExternalAgent.ToString(), command: "links.move_batch", changesJson: payload);
        fixture.Insert(caller: CallerRef.ExternalAgent.ToString(), command: "links.move_batch", changesJson: "not json at all");

        var polled = feed.Poll(100);

        Assert.Equal(2, polled.Count);
        Assert.Equal(new[] { "links.changed" }, polled[0].Events);
        Assert.Equal(2, polled[0].Touched.Count);
        Assert.Equal("Moved 2 link(s)", polled[0].HumanSummary);
        Assert.True(polled[0].IsEffective);

        Assert.Empty(polled[1].Events);                                      // 坏载荷 → "无事件"（调用方据此整体失效）
        Assert.Null(polled[1].HumanSummary);
    }

    [Fact]
    public void 干跑与嵌套行_不算有效变更()
    {
        using var fixture = new Fixture();
        var feed = new OpsChangeFeed(fixture.ContextFactory, CallerRef.ExternalAgent.ToString());

        fixture.Insert(caller: CallerRef.ExternalAgent.ToString(), command: "links.move_batch", dryRun: true);
        fixture.Insert(caller: CallerRef.ExternalAgent.ToString(), command: "links.move_batch", isNested: true);
        fixture.Insert(caller: CallerRef.ExternalAgent.ToString(), command: "links.move_batch", success: false);

        var polled = feed.Poll(100);

        Assert.Equal(3, polled.Count);                                       // 行照报（失效判据要用它们的事件）
        Assert.All(polled, change => Assert.False(change.IsEffective));       // 但没有一条该弹提示
    }

    /// <summary>临时主库 + 附属库（审计落点）：行用手写 SQL 插入，精确控制 caller / 载荷 / 标志位。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _mainPath;

        public Fixture()
        {
            _mainPath = Path.Combine(TempArea.Resolve(), $"feed-{Guid.NewGuid():N}.db");
            var factory = new OpsDbContextFactory(_mainPath);
            OpsPath = factory.OpsPath;
            ContextFactory = () => factory.CreateDbContext();
        }

        public string OpsPath { get; }

        public Func<Microsoft.EntityFrameworkCore.DbContext> ContextFactory { get; }

        public void Insert(string caller, string command, string? changesJson = null,
            bool success = true, bool dryRun = false, bool isNested = false)
        {
            using var connection = new SqliteConnection($"Data Source={OpsPath}");
            connection.Open();
            using var command2 = connection.CreateCommand();
            command2.CommandText =
                "INSERT INTO audit_log (at, caller, command, elapsed_ms, success, correlation_id, " +
                "dry_run, is_nested, changes_json) VALUES (@at, @caller, @command, 1, @success, @corr, " +
                "@dry, @nested, @changes);";
            command2.Parameters.AddWithValue("@at", DateTimeOffset.Now.ToString("O"));
            command2.Parameters.AddWithValue("@caller", caller);
            command2.Parameters.AddWithValue("@command", command);
            command2.Parameters.AddWithValue("@success", success ? 1 : 0);
            command2.Parameters.AddWithValue("@corr", Guid.NewGuid().ToString("N"));
            command2.Parameters.AddWithValue("@dry", dryRun ? 1 : 0);
            command2.Parameters.AddWithValue("@nested", isNested ? 1 : 0);
            command2.Parameters.AddWithValue("@changes", (object?)changesJson ?? DBNull.Value);
            command2.ExecuteNonQuery();
        }

        /// <summary>把附属库换成另一份（模拟恢复备份 / 被别的进程重建）：行数更少，id 区间整体退到水位之下。</summary>
        public void ReplaceOpsDatabase()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = OpsPath + suffix;
                try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
            }

            _ = new OpsDbContextFactory(_mainPath);   // 重建 schema（id 从头开始）
        }

        public void Dispose()
        {
            OpsDbContextFactory.DeleteDatabaseFiles(_mainPath);
        }
    }
}
