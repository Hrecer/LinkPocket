using System.Data;
using System.Data.Common;
using System.Globalization;
using LinkPocket.Kernel;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Data;

/// <summary>
/// 审计存储 EF 实现（**读侧 + 保留策略**）：audit_log 由原生 SQL 建表（v2 基线 + v6 补列），
/// 无 EF 实体映射，故与 <c>SqlAuditWriter</c> 同形态走原生参数化 SQL（不吃变更跟踪）。
///
/// <para><b>时间列口径</b>：<c>at</c> = 带本地偏移的 ISO-8601 文本（写入侧 <c>DateTimeOffset.Now.ToString("O")</c>）；
/// 过滤边界在本实现里**归一成本地偏移**后再做字符串比较——字符串序 = 时刻序，且 <c>idx_audit_at</c> 可用。
/// 时区/夏令时变更属已知边界（与全库时间列的既有口径一致）。</para>
/// </summary>
public sealed class EfAuditRepository : IAuditRepository
{
    private const string ColumnsWithPayloads =
        "id, at, session_id, caller, command, elapsed_ms, success, error_code, batch_id, correlation_id, " +
        "is_nested, dry_run, args_truncated, args_json, changes_json, stack_trace";

    private const string ColumnsSlim =
        "id, at, session_id, caller, command, elapsed_ms, success, error_code, batch_id, correlation_id, " +
        "is_nested, dry_run, args_truncated, NULL, NULL, NULL";

    private readonly LinkPocketDbContext _db;

    public EfAuditRepository(LinkPocketDbContext db) => _db = db;

    public async Task<AuditPage> QueryAsync(
        AuditQueryFilter filter, int skip, int take, bool includePayloads, CancellationToken ct)
    {
        var conditions = new List<string>();
        var parameters = new List<(string Name, object? Value)>();

        void Eq(string column, string name, object? value)
        {
            conditions.Add($"{column} = @{name}");
            parameters.Add((name, value));
        }

        if (!string.IsNullOrEmpty(filter.Command)) Eq("command", "command", filter.Command);
        if (!string.IsNullOrEmpty(filter.Caller)) Eq("caller", "caller", filter.Caller);
        if (!string.IsNullOrEmpty(filter.SessionId)) Eq("session_id", "session_id", filter.SessionId);
        if (!string.IsNullOrEmpty(filter.CorrelationId)) Eq("correlation_id", "correlation_id", filter.CorrelationId);
        if (!string.IsNullOrEmpty(filter.BatchId)) Eq("batch_id", "batch_id", filter.BatchId);
        if (filter.Success is { } success) Eq("success", "success", success ? 1L : 0L);
        if (filter.IsNested is { } isNested) Eq("is_nested", "is_nested", isNested ? 1L : 0L);
        if (filter.DryRun is { } dryRun) Eq("dry_run", "dry_run", dryRun ? 1L : 0L);
        if (filter.From is { } from)
        {
            conditions.Add("at >= @from");
            parameters.Add(("from", ToAuditText(from)));
        }
        if (filter.To is { } to)
        {
            conditions.Add("at < @to");   // 半开区间：分页与保留同口径
            parameters.Add(("to", ToAuditText(to)));
        }

        var whereSql = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);

        var total = await ExecuteScalarIntAsync(connection, $"SELECT COUNT(*) FROM audit_log{whereSql}", parameters, ct);

        var items = new List<AuditRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {(includePayloads ? ColumnsWithPayloads : ColumnsSlim)} FROM audit_log{whereSql} " +
            "ORDER BY at DESC, id DESC LIMIT @take OFFSET @skip";
        AddParameters(command, parameters);
        AddParameter(command, "take", (long)take);
        AddParameter(command, "skip", (long)skip);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(Map(reader));

        return new AuditPage(items, total);
    }

    public async Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM audit_log WHERE at < @before";
        AddParameter(command, "before", ToAuditText(before));
        return await command.ExecuteNonQueryAsync(ct);
    }

    public Task<int> CountAsync(CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        return CountCoreAsync(connection, ct);
    }

    public async Task<DateTimeOffset?> OldestAtAsync(CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(at) FROM audit_log";
        var value = await command.ExecuteScalarAsync(ct);
        return value is string text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None)
            : null;
    }

    private static async Task<int> CountCoreAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_log";
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteScalarIntAsync(
        DbConnection connection, string sql, List<(string Name, object? Value)> parameters, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static AuditRecord Map(DbDataReader reader) => new(
        Id: reader.GetInt64(0),
        At: DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None),
        SessionId: reader.IsDBNull(2) ? null : reader.GetString(2),
        Caller: reader.GetString(3),
        Command: reader.GetString(4),
        ElapsedMs: reader.GetInt64(5),
        Success: reader.GetInt64(6) != 0,
        ErrorCode: reader.IsDBNull(7) ? null : reader.GetString(7),
        BatchId: reader.IsDBNull(8) ? null : reader.GetString(8),
        CorrelationId: reader.GetString(9),
        IsNested: reader.GetInt64(10) != 0,
        DryRun: reader.GetInt64(11) != 0,
        ArgsTruncated: reader.GetInt64(12) != 0,
        ArgsJson: reader.IsDBNull(13) ? null : reader.GetString(13),
        ChangesJson: reader.IsDBNull(14) ? null : reader.GetString(14),
        StackTrace: reader.IsDBNull(15) ? null : reader.GetString(15));

    /// <summary>把任意时刻归一成本地时区偏移的 ISO-8601 文本（与写入侧 'O' 同形 → 字符串比较 = 时刻比较）。</summary>
    private static string ToAuditText(DateTimeOffset value)
        => value.ToOffset(TimeZoneInfo.Local.GetUtcOffset(value)).ToString("O", CultureInfo.InvariantCulture);

    private static void AddParameters(DbCommand command, List<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters) AddParameter(command, name, value);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
