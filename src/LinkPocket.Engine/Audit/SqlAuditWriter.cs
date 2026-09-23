using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    private readonly Func<LinkPocketDbContext> _dbFactory;

    public SqlAuditWriter(Func<LinkPocketDbContext> dbFactory)
        => _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));

    public string Write(AuditEntry entry)
    {
        var at = entry.At.ToString("O");
        var argsJson = entry.ArgsJson is { Length: > MaxArgsJsonLength } long_
            ? long_[..MaxArgsJsonLength] : entry.ArgsJson;
        // 变更载荷走唯一投影（与 wire changes / 事件负载同形状）：diff 上限 2000 条 + 自描述截断标记
        var changesJson = entry.Changes is null
            ? null
            : ChangeSetPayload.From(entry.Changes).GetRawText();

        using var db = _dbFactory();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO audit_log
                (at, session_id, caller, command, args_json, elapsed_ms, success, error_code, changes_json,
                 batch_id, correlation_id, dry_run, is_nested, stack_trace, args_truncated)
            VALUES
                (@at, @session_id, @caller, @command, @args_json, @elapsed_ms, @success, @error_code, @changes_json,
                 @batch_id, @correlation_id, @dry_run, @is_nested, @stack_trace, @args_truncated)
            """;
        AddParam(command, "@at", at);
        AddParam(command, "@session_id", entry.Caller.SessionId);
        AddParam(command, "@caller", entry.Caller.ToString());
        AddParam(command, "@command", entry.Command);
        AddParam(command, "@args_json", argsJson);
        AddParam(command, "@elapsed_ms", entry.ElapsedMs);
        AddParam(command, "@success", entry.Success ? 1L : 0L);
        AddParam(command, "@error_code", entry.ErrorCode);
        AddParam(command, "@changes_json", changesJson);
        AddParam(command, "@batch_id", entry.BatchId);
        AddParam(command, "@correlation_id", entry.CorrelationId);
        AddParam(command, "@dry_run", entry.DryRun ? 1L : 0L);
        AddParam(command, "@is_nested", entry.IsNested ? 1L : 0L);
        AddParam(command, "@stack_trace", entry.StackTrace);
        AddParam(command, "@args_truncated", entry.ArgsTruncated ? 1L : 0L);
        command.ExecuteNonQuery();
        return entry.CorrelationId;
    }

    private static void AddParam(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
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
