using LinkPocket.Update;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinkPocket;

/// <summary>
/// 预检的纪元探测必须零残留：只读打开 WAL 库会创建 -wal/-shm 且只读连接不回收——
/// 探针只删自己新建的那两件；**本来就存在的保持原样**（上次未净关停的恢复线索）。
/// </summary>
public class EpochProbeArtifactTests
{
    [Fact]
    public void 预检探测不留下wal残留()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var db = Path.Combine(root, "LinkPocket", "data", "linkpocket.db");
            var install = UpdateTestKit.CreateInstall(root,
                data => CreateWalDb(Path.Combine(data, "linkpocket.db"), 9));
            var package = UpdateTestKit.CreatePackage(root);
            var ctx = UpdateTestKit.CreateContext(root, install, package, UpdateTestKit.CreateReport(root));

            Assert.False(File.Exists(db + "-wal"), "fixture starts clean");
            Assert.False(File.Exists(db + "-shm"), "fixture starts clean");

            Assert.Equal(UpdateMode.Direct, Preflight.Run(ctx, _ => { }).Mode);

            Assert.False(File.Exists(db + "-wal"), "probe must not leave -wal behind");
            Assert.False(File.Exists(db + "-shm"), "probe must not leave -shm behind");
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 预检不碰原有的wal文件()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var db = Path.Combine(root, "LinkPocket", "data", "linkpocket.db");
            var install = UpdateTestKit.CreateInstall(root,
                data => CreateWalDb(Path.Combine(data, "linkpocket.db"), 9));
            var package = UpdateTestKit.CreatePackage(root);
            var ctx = UpdateTestKit.CreateContext(root, install, package, UpdateTestKit.CreateReport(root));

            using (var keeper = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString()))
            {
                keeper.Open();
                using var write = keeper.CreateCommand();
                write.CommandText = "INSERT INTO t(x) VALUES (1);";
                write.ExecuteNonQuery();

                Assert.True(File.Exists(db + "-wal"), "keeper must keep -wal alive");

                Assert.Equal(UpdateMode.Direct, Preflight.Run(ctx, _ => { }).Mode);

                Assert.True(File.Exists(db + "-wal"), "pre-existing -wal must stay");
                Assert.True(File.Exists(db + "-shm"), "pre-existing -shm must stay");
            }

            SqliteConnection.ClearAllPools();
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    private static void CreateWalDb(string path, int userVersion)
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"PRAGMA journal_mode = wal; CREATE TABLE IF NOT EXISTS t(x INTEGER); PRAGMA user_version = {userVersion};";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }
}
