using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Engine;

/// <summary>
/// 审计落表写入器：AuditEntry → schema v6 audit_log 表（v6 补列：dry_run / is_nested / stack_trace / args_truncated）。
/// 连接与主事务无重叠：调用点均在主事务提交/回滚之后（成功路径在 Commit 后，失败路径在 UoW 释放后），
/// 因此这里的独立短连接永远不会与在途写事务竞争。
/// 审计失败即抛（不吞、不自愈）——audit_log 由 schema v2 基线创建，表缺失属宿主库状态异常，
/// 必须在冒烟/测试中暴露而非静默降级。
/// </summary>
public sealed class SqlAuditWriter : IAuditWriter
{
    /// <summary>入参快照上限（超长截断并置 args_truncated，防止大参数灌爆审计表）。</summary>
    private const int MaxArgsJsonLength = 4000;

    private readonly Func<DbContext> _dbFactory;

    /// <param name="dbFactory">审计落库的连接来源——**附属库**（操作记录库）上下文工厂，而非用户库：
    /// 审计是运行痕迹，不进用户数据文件。</param>
    public SqlAuditWriter(Func<DbContext> dbFactory)
        => _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));

    private const string InsertSql =
        """
        INSERT INTO audit_log
            (at, session_id, caller, command, args_json, elapsed_ms, success, error_code, changes_json,
             batch_id, correlation_id, dry_run, is_nested, stack_trace, args_truncated)
        VALUES
            (@at, @session_id, @caller, @command, @args_json, @elapsed_ms, @success, @error_code, @changes_json,
             @batch_id, @correlation_id, @dry_run, @is_nested, @stack_trace, @args_truncated)
        """;

    public string Write(AuditEntry entry)
    {
        WriteMany([entry]);
        return entry.CorrelationId;
    }

    /// <summary>
    /// 批量写入：**一条连接、一个事务**。撤销/批处理的事务收口后可能有几十到上千条嵌套审计，
    /// 逐条独立连接（原实现的默认循环）会把"每步一次连接 + 一次提交"变成线性 I/O 开销。
    /// </summary>
    public string? WriteMany(IReadOnlyList<AuditEntry> entries)
    {
        if (entries.Count == 0) return null;

        using var db = _dbFactory();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();
        // 附属库是**运行痕迹**（审计/操作记录），不是用户书签数据：WAL + NORMAL 已能扛应用崩溃，
        // 无需每次提交都 fsync（默认 FULL 在慢盘上每次 5–15ms，正是撤销/批处理收尾的固定开销）。
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = InsertSql;

        var pAt = AddParam(command, "@at");
        var pSession = AddParam(command, "@session_id");
        var pCaller = AddParam(command, "@caller");
        var pCommand = AddParam(command, "@command");
        var pArgs = AddParam(command, "@args_json");
        var pElapsed = AddParam(command, "@elapsed_ms");
        var pSuccess = AddParam(command, "@success");
        var pError = AddParam(command, "@error_code");
        var pChanges = AddParam(command, "@changes_json");
        var pBatch = AddParam(command, "@batch_id");
        var pCorr = AddParam(command, "@correlation_id");
        var pDryRun = AddParam(command, "@dry_run");
        var pNested = AddParam(command, "@is_nested");
        var pStack = AddParam(command, "@stack_trace");
        var pTruncated = AddParam(command, "@args_truncated");

        foreach (var entry in entries)
        {
            var argsJson = entry.ArgsJson is { Length: > MaxArgsJsonLength } long_
                ? long_[..MaxArgsJsonLength] : entry.ArgsJson;
            // 变更载荷走唯一投影（与 wire changes / 事件负载同形状）：diff 上限 2000 条 + 自描述截断标记
            var changesJson = entry.Changes is null
                ? null
                : ChangeSetPayload.From(entry.Changes).GetRawText();

            pAt.Value = entry.At.ToString("O");
            pSession.Value = entry.Caller.SessionId ?? (object)DBNull.Value;
            pCaller.Value = entry.Caller.ToString();
            pCommand.Value = entry.Command;
            pArgs.Value = argsJson ?? (object)DBNull.Value;
            pElapsed.Value = entry.ElapsedMs;
            pSuccess.Value = entry.Success ? 1L : 0L;
            pError.Value = entry.ErrorCode ?? (object)DBNull.Value;
            pChanges.Value = changesJson ?? (object)DBNull.Value;
            pBatch.Value = entry.BatchId ?? (object)DBNull.Value;
            pCorr.Value = entry.CorrelationId;
            pDryRun.Value = entry.DryRun ? 1L : 0L;
            pNested.Value = entry.IsNested ? 1L : 0L;
            pStack.Value = entry.StackTrace ?? (object)DBNull.Value;
            pTruncated.Value = entry.ArgsTruncated ? 1L : 0L;
            command.ExecuteNonQuery();
        }

        tx.Commit();
        return entries[^1].CorrelationId;
    }

    private static System.Data.Common.DbParameter AddParam(System.Data.Common.DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }
}

/// <summary>SHA-256 指纹（Staging/文件校验共用的小工具，UTF-8 字节散列）。</summary>
public static class Sha256Hex
{
    public static string OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string OfText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
