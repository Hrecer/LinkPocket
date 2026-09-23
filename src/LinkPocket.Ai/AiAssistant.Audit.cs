using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 引擎审计面（<see cref="IAiAssistant.QueryEngineAuditAsync"/>）：AI 页「引擎审计」页签的数据源，
/// **只读** 引擎的 <c>audit.query</c>（不建第二套审计、不缓存改写）。
///
/// <para><b>关联口径</b>：一个回合的全部引擎调用共用一条 correlation（<c>ai:&lt;turnId&gt;</c>，
/// 与"一次用户动作 = 一条关联"的既有口径同构）；本会话范围 = 最近若干回合合并
/// （引擎的 audit.query 没有"跨关联"过滤，合并是唯一诚实的做法，边界如实写在这里与界面提示上）。</para>
/// </summary>
public sealed partial class AiAssistant
{
    /// <summary>本会话范围的回合上限（按最近 N 个回合合并；界面提示同一口径）。</summary>
    private const int MaxAuditTurns = 20;

    /// <summary>回合关联键（工具调用执行与台账解析查询都用它，审计/日志同一把钥匙）。</summary>
    internal static string TurnCorrelation(string turnId) => $"ai:{turnId}";

    public async Task<AiAuditPage> QueryEngineAuditAsync(AiAuditQuery query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query.SessionId);
        var file = _sessionStore.Load(query.SessionId) ?? throw NotFound(query.SessionId);

        var perPage = Math.Clamp(query.PerPage, 1, 200);
        var page = Math.Max(1, query.Page);

        if (query.TurnId is { } turnId)
        {
            if (!file.Turns.Any(t => string.Equals(t.TurnId, turnId, StringComparison.Ordinal)))
                return new AiAuditPage([], 0, 1, 0);
            return await QueryByCorrelationAsync(TurnCorrelation(turnId), query, page, perPage, ct).ConfigureAwait(false);
        }

        // 本会话：最近 N 个回合各查一页后合并（超出的更早回合不在本页签的范围内，如实标注在界面）
        var turns = file.Turns.TakeLast(MaxAuditTurns).Reverse().ToArray();
        if (turns.Length == 0) return new AiAuditPage([], 0, 1, 0);

        var rows = new List<AiEngineCallRow>();
        foreach (var turn in turns)
        {
            var slice = await QueryByCorrelationAsync(TurnCorrelation(turn.TurnId), query, 1, perPage, ct)
                .ConfigureAwait(false);
            rows.AddRange(slice.Items);
        }

        var merged = rows.OrderByDescending(r => r.At).Take(perPage).ToArray();
        return new AiAuditPage(merged, merged.Length, 1, 1);
    }

    private async Task<AiAuditPage> QueryByCorrelationAsync(string correlationId, AiAuditQuery query,
        int page, int perPage, CancellationToken ct)
    {
        var data = await _client.QueryAsync<object>("audit.query", new
        {
            correlation_id = correlationId,
            success = query.Success,
            include_payloads = query.IncludePayloads,
            page,
            per_page = perPage,
        }, new CallOptions(Caller: new CallerRef(CallerKind.Agent, null)), ct).ConfigureAwait(false);

        var json = data is null ? default : JsonSerializer.SerializeToElement(data, AuditRowJson);
        if (json.ValueKind != JsonValueKind.Object) return new AiAuditPage([], 0, page, 0);

        var items = new List<AiEngineCallRow>();
        if (json.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in array.EnumerateArray())
            {
                var command = Str(row, "command");
                if (command is null) continue;
                // 命令名搜索在客户端做（引擎侧无模糊参数）；"命令名子串"口径与界面提示一致
                if (query.Search is { Length: > 0 } search
                    && !command.Contains(search, StringComparison.OrdinalIgnoreCase))
                    continue;
                items.Add(new AiEngineCallRow(
                    At: Time(row, "at"),
                    Command: command,
                    Caller: Str(row, "caller") ?? "",
                    CorrelationId: Str(row, "correlation_id") ?? correlationId,
                    Success: Bool(row, "success"),
                    ErrorCode: Str(row, "error_code"),
                    ElapsedMs: Num(row, "elapsed_ms"),
                    DryRun: Bool(row, "dry_run"),
                    IsNested: Bool(row, "is_nested"),
                    BatchId: Str(row, "batch_id"),
                    ArgsJson: Str(row, "args_json"),
                    ChangesJson: Str(row, "changes_json")));
            }
        }

        var total = Num(json, "total");
        var pageCount = Num(json, "page_count");
        return new AiAuditPage(items, (int)total, (int)Math.Max(1, pageCount), (int)Math.Max(1, pageCount));
    }

    private static readonly JsonSerializerOptions AuditRowJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string? Str(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long Num(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var number) ? number : 0;

    private static DateTimeOffset Time(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(value.GetString(), out var at) ? at : DateTimeOffset.MinValue;
}
