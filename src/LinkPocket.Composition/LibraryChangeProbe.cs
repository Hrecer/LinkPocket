using Microsoft.Data.Sqlite;

namespace LinkPocket.Composition;

/// <summary>
/// 跨进程变更探针：判断"**别的进程**是不是刚写过这个库"。
/// <para>原理 = SQLite 原生的 <c>PRAGMA data_version</c>：该值对**本连接**恒不变，
/// 而**任何其它连接**提交时会递增 —— 正是"CLI / 外部 Agent（MCP）刚写库"的判据。
/// 零 schema 改动、零引擎改动，单条 pragma（微秒级）。</para>
/// <para>为什么不用文件监视（看 <c>linkpocket.db-wal</c>）：WAL 每次提交都写文件（**含本进程自己的写**），
/// 噪声极大；且本机 G 盘 overlay 的文件事件本身不稳（见 WARNINGS 第 2/15 条），会重演"假红"。</para>
/// <para>已知误报：本进程自己的写也可能 bump（连接池会换底层连接）→ 后果只是**多刷一次**
/// （刷新是幂等"重查"，见 BEHAVIOR-CONTRACT §1.5）。</para>
/// <para>连接口径：读写模式打开（WAL 库上只读连接在缺少 <c>-shm</c> 时会打不开），
/// 但**只执行 pragma**，不参与任何写路径；长连接随宿主持有，退出时 <see cref="Dispose"/>。</para>
/// </summary>
public sealed class LibraryChangeProbe : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private long _last;

    /// <param name="databasePath">库文件路径（调用方保证已存在——探针在引擎装配之后构造）。</param>
    public LibraryChangeProbe(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connection = new SqliteConnection(
            new SqliteConnectionStringBuilder($"Data Source={Path.GetFullPath(databasePath)}")
            {
                Mode = SqliteOpenMode.ReadWrite,
            }.ToString());
        _connection.Open();
        _last = Query();
    }

    /// <summary>当前 data_version 读数（诊断/测试用）。</summary>
    public long Current
    {
        get { lock (_gate) { return Query(); } }
    }

    /// <summary>自上次调用以来**别的连接**提交过 → true，并记住新值；否则 false。</summary>
    public bool HasChanged()
    {
        lock (_gate)
        {
            var value = Query();
            if (value == _last) return false;
            _last = value;
            return true;
        }
    }

    private long Query()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA data_version;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose() => _connection.Dispose();
}
