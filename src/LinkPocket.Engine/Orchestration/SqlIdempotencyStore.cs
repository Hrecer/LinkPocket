using System.Collections.Concurrent;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Engine;

/// <summary>
/// 幂等键落表存储（方案 3.4，阶段 11）：内存缓存命中优先，未命中查 schema v2 的 idempotency 表
/// （key → 首次结果 JSON，24h 窗口）；Store 双写（内存 + 表）并顺带清理过期行。
/// 落库副本以 JSON 形态还原（Data = JsonElement），与进程内活对象结果在 wire/DTO 层面等价。
/// </summary>
public sealed class SqlIdempotencyStore : IdempotencyStore
{
    private sealed record PersistedResult(JsonElement? Data, ChangeSet? Changes, string? AuditRef);

    private sealed record CacheEntry(CommandResult Result, DateTimeOffset At);

    private readonly Func<LinkPocketDbContext> _dbFactory;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private int _storeCount;

    public SqlIdempotencyStore(Func<LinkPocketDbContext> dbFactory, TimeSpan? window = null)
        : base(window)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _window = window ?? TimeSpan.FromHours(24);
    }

    public override bool TryGet(string key, out CommandResult result)
    {
        result = null!;
        if (_cache.TryGetValue(key, out var cached))
        {
            if (DateTimeOffset.Now - cached.At <= _window)
            {
                result = cached.Result;
                return true;
            }
            _cache.TryRemove(key, out _);
        }

        using var db = _dbFactory();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();

        string? resultJson;
        string atText;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT result_json, at FROM idempotency WHERE key = @key";
            AddParam(command, "@key", key);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return false;
            resultJson = reader.GetString(0);
            atText = reader.GetString(1);
        }   // reader 在此完全关闭 —— Microsoft.Data.Sqlite 不支持同连接多活动结果集（MARS），
            // 之前在用着 reader 时直接对同一连接发 DELETE 会抛
            // 「An open reader is already associated with this command」，过期清理因此永远不生效（审核 1.1）。

        var at = DateTimeOffset.ParseExact(atText, "O",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        if (DateTimeOffset.Now - at > _window)
        {
            // 过期：视为未命中；顺手清掉该行（清理原挂在 Store 的写路径 %64 上，长期只读不写的
            // 幂等命中会让过期行永久滞留且每次 TryGet 都白跑一次 SQL）
            try
            {
                using var del = connection.CreateCommand();
                del.CommandText = "DELETE FROM idempotency WHERE key = @key";
                AddParam(del, "@key", key);
                del.ExecuteNonQuery();
            }
            catch (Exception cleanupEx)
            {
                // 清理尽力而为，不影响「未命中」语义——但必须暴露（观测面纪律：失败禁止静默吞掉）
                System.Diagnostics.Trace.TraceWarning("幂等过期行清理失败（key={0}）：{1}", key, cleanupEx.Message);
            }
            return false;
        }

        var persisted = JsonSerializer.Deserialize<PersistedResult>(resultJson, EngineJson.Options);
        if (persisted is null) return false;

        result = new CommandResult(persisted.Data, persisted.Changes, persisted.AuditRef);
        _cache[key] = new CacheEntry(result, at);
        return true;
    }

    /// <summary>
    /// 落表 + 写内存缓存。<b>表写失败即抛</b>：schema v2 保证 idempotency 表存在，
    /// 静默降级会让调用方误以为"防重已生效"；宁可调用失败，也不假装幂等成立。
    /// </summary>
    public override void Store(string key, CommandResult result)
    {
        var json = JsonSerializer.Serialize(new PersistedResult(
            BatchEngine.ToElement(result.Data), result.Changes, result.AuditRef), EngineJson.Options);
        var at = DateTimeOffset.Now.ToString("O");

        using (var db = _dbFactory())
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO idempotency (key, result_json, at) VALUES (@key, @result_json, @at)
                    ON CONFLICT(key) DO UPDATE SET result_json = excluded.result_json, at = excluded.at
                    """;
                AddParam(command, "@key", key);
                AddParam(command, "@result_json", json);
                AddParam(command, "@at", at);
                command.ExecuteNonQuery();
            }

            // 每 64 次写入顺带清理过期行（防表膨胀；幂等写入本身低频）。
            // Interlocked.Increment 从 1 起计数：`% 64 == 0` 在第 64/128/... 次触发——避免
            // 进程第一次写就白跑一次「表里几乎必然没有过期行」的 DELETE（审核 2.6）。
            if (Interlocked.Increment(ref _storeCount) % 64 == 0)
            {
                using var prune = connection.CreateCommand();
                prune.CommandText = "DELETE FROM idempotency WHERE at < @cutoff";
                AddParam(prune, "@cutoff", DateTimeOffset.Now.Subtract(_window).ToString("O"));
                prune.ExecuteNonQuery();
            }
        }

        _cache[key] = new CacheEntry(result, DateTimeOffset.Now);
    }

    private static void AddParam(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
