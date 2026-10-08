using LinkPocket.Composition;
using LinkPocket.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 跨进程变更探针金标准：<c>PRAGMA data_version</c> 的**语义契约**——
/// "别的连接提交"必须被报出来（且只报一次），这是"界面感知 CLI / 外部 Agent 写库"的唯一判据。
/// </summary>
public class LibraryChangeProbeTests
{
    [Fact]
    public void 别的连接提交一次_探针恰好报一次变化()
    {
        var path = Path.Combine(TempArea.Resolve(), $"probe-{Guid.NewGuid():N}.db");
        try
        {
            Exec(path, "CREATE TABLE t (x INTEGER);");   // 建库（探针之前）

            using var probe = new LibraryChangeProbe(path);
            Assert.False(probe.HasChanged());            // 构造时已取基线 → 首次不报变化

            Exec(path, "INSERT INTO t VALUES (1);");     // **另一个连接**提交

            Assert.True(probe.HasChanged());             // 必须报出来
            Assert.False(probe.HasChanged());            // 已记住新值 → 不重复报（否则会刷屏）
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void 无外部提交时_探针安静不动()
    {
        var path = Path.Combine(TempArea.Resolve(), $"probe-{Guid.NewGuid():N}.db");
        try
        {
            Exec(path, "CREATE TABLE t (x INTEGER);");
            using var probe = new LibraryChangeProbe(path);

            Assert.False(probe.HasChanged());
            Assert.False(probe.HasChanged());            // 反复轮询不得产生"假变更"（否则界面会被无谓刷新）
            Assert.False(probe.HasChanged());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static void Exec(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
