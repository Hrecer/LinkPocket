using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LinkPocket.Data;

/// <summary>
/// 原生 SQL 命令工厂（取页/计数直读路径共用，2026-10-15 A/B 路线）：
/// 走 EF 管的连接与**实例当前事务** —— 批/干跑事务里的直读与同 UoW 的 EF 查询看同一个快照、
/// 读得到本单元未提交的写入（读己之写口径一致）。
/// 直读只服务"形态固定"的读（目录页取数、文件夹索引、计数）；语义面大的查询仍走 EF，
/// 两边的过滤/排序口径由等价性测试锁死（逐字段对照 EF 实体路径）。
/// </summary>
internal static class RawSql
{
    public static async Task<SqliteCommand> CreateAsync(LinkPocketDbContext db, string sql, CancellationToken ct)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var tx = db.Database.CurrentTransaction;
        if (tx is not null) cmd.Transaction = (SqliteTransaction)tx.GetDbTransaction();
        return cmd;
    }
}
