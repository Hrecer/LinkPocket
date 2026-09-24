using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>一次对账快照：实体集合（键 = <c>type + NUL + id</c>，与台账分组键一致）。
/// 实体缺席 = 键不存在（执行前未创建 / 执行后已离开主表）；字段值一律 <see cref="FieldValue"/> 形态
/// （JSON null = 字段值为空；快照里没有"不适用"——不适用由实体缺席表达）。</summary>
public sealed class AiSnapshot(IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement?>> entities)
{
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement?>> Entities { get; } = entities;
}

/// <summary>对账结论：兜底字段 + 不一致实体 + 引擎未上报的观测（键与 <see cref="AiSnapshot"/> 一致）。</summary>
public sealed class ReconcileOutcome
{
    public static readonly ReconcileOutcome Empty = new()
    {
        Reconciled = new Dictionary<string, IReadOnlyList<AiFieldChange>>(StringComparer.Ordinal),
        Mismatched = new HashSet<string>(StringComparer.Ordinal),
        Unreported = new HashSet<string>(StringComparer.Ordinal),
    };

    /// <summary>引擎 touched 无 diff、对账观测到字段变化 → 台账兜底（Source=Reconciled）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AiFieldChange>> Reconciled { get; init; } =
        new Dictionary<string, IReadOnlyList<AiFieldChange>>(StringComparer.Ordinal);

    /// <summary>引擎 diff 与对账快照不一致的实体 → 台账标注 + <c>ai.ledger</c> 告警（不改写引擎事实）。</summary>
    public IReadOnlySet<string> Mismatched { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>对账观测到变更、引擎连 touched 都未上报 → 只告警不建账（台账只记引擎事实）。</summary>
    public IReadOnlySet<string> Unreported { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// AI 侧变更对账（功能书 §7.1 第三来源）：白名单写命令在**执行前后各读一次快照**自算字段级
/// before → after —— ① 引擎未上报 diff 的实体 → 台账兜底（<see cref="AiChangeSource.Reconciled"/>）；
/// ② 引擎已上报 diff → 降级为**校验**：不一致 = <c>ai.ledger</c> warn + 界面标注
/// "引擎与对账不一致"（<b>引擎 diff 仍是权威，绝不改写事实</b>）。快照读失败 → 对账缺席
/// （如实降级为原口径，不阻断回合）。
///
/// <para><b>白名单（13 条，目标实体可从入参/结果唯一定位）</b>：links.create/update/trash/move_batch ·
/// folders.create/update/move/move_batch/delete/copy · trash.restore/restore_unit/restore_batch。
/// batch/macro（目标藏在脚本里）、backup/bookmarks.import（文件驱动全库）、visit_*（计数不入 diff 字段集）
/// 等不入白名单——引擎 diff 覆盖或快照无从定位，如实不对账。</para>
///
/// <para><b>可观测字段集 = 引擎 diff 字段集（ENGINE-API §1）∩ DTO 读面</b>：
/// link = url/title/description/folder_id/is_important；folder = name/parent_id。
/// 两条**读面边界**（不参与对账，既不判不一致也不兜底）：① <c>favicon_url</c>——DTO 投影把 null
/// 归一为 ""（Mappings），与引擎"两侧都空不入 diff"的守卫不可区分；② <c>folder.description</c>——
/// 任何查询 DTO 都不暴露该字段。字符串比较按 <c>null ≡ ""</c> 归一（读面不可区分的两值不算不一致）。</para>
///
/// <para><b>调用纪律</b>：快照读不带会话 ID（Kind=Agent、SessionId=null）——限流保护的是工具调用风暴
/// （由 MaxToolCalls/MaxChanges 兜底），台账簿记读不消耗会话额度；correlation 传入回合关联，
/// 日志仍对齐回合时间轴。快照侧**任何**故障（含引擎把取消包装成的 LP.CANCELLED）都降级为对账缺席：
/// 绝不让已提交命令的台账构建被快照拖掉；回合的取消收尾由下一次引擎/流式调用重新校验令牌完成。</para>
/// </summary>
public sealed class AiReconciler(EngineClient client, CallerRef caller)
{
    /// <summary>对账白名单（13 条写命令）——非白名单命令零快照开销。</summary>
    private static readonly IReadOnlySet<string> Whitelist = new HashSet<string>(StringComparer.Ordinal)
    {
        "links.create", "links.update", "links.trash", "links.move_batch",
        "folders.create", "folders.update", "folders.move", "folders.move_batch",
        "folders.delete", "folders.copy",
        "trash.restore", "trash.restore_unit", "trash.restore_batch",
    };

    /// <summary>可观测字段集（= DTO 读面；见类注释的两条读面边界）。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ObservableFields =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["link"] = ["url", "title", "description", "folder_id", "is_important"],
            ["folder"] = ["name", "parent_id"],
        };

    /// <summary>links.query 读面序列化口径：snake_case、**保留 JSON null**（空值必须与"缺键"可区分）。</summary>
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static bool IsWhitelisted(string command) => Whitelist.Contains(command);

    /// <summary>台账分组键（type + NUL + id）——快照/结论/台账三处共用同一把钥匙。</summary>
    public static string Key(string type, string id) => $"{type}\u0000{id}";

    // ── 快照采集（执行前 = 目标在入参；执行后 = 入参 ∪ 结果派生的新 ID）──────────

    /// <summary>执行前快照：只按入参定位目标（创建类此时目标还不存在 → 有效空快照）。</summary>
    public Task<AiSnapshot?> CaptureBeforeAsync(string command, JsonElement args, string correlationId,
        CancellationToken ct)
        => CaptureAsync(command, args, resultData: null, isAfter: false, correlationId, ct);

    /// <summary>执行后快照：入参目标 ∪ 结果派生 ID（create/copy 的新实体）。</summary>
    public Task<AiSnapshot?> CaptureAfterAsync(string command, JsonElement args, JsonElement? resultData,
        string correlationId, CancellationToken ct)
        => CaptureAsync(command, args, resultData, isAfter: true, correlationId, ct);

    private async Task<AiSnapshot?> CaptureAsync(string command, JsonElement args, JsonElement? resultData,
        bool isAfter, string correlationId, CancellationToken ct)
    {
        var targets = Targets(command, args, isAfter ? resultData : null);
        if (targets.Count == 0) return new AiSnapshot(new Dictionary<string, IReadOnlyDictionary<string, JsonElement?>>(StringComparer.Ordinal));

        var entities = new Dictionary<string, IReadOnlyDictionary<string, JsonElement?>>(StringComparer.Ordinal);
        try
        {
            foreach (var group in targets.GroupBy(t => t.Type))
            {
                if (group.Key == "link")
                {
                    foreach (var fields in await ReadLinksAsync(group.Select(g => g.Id).Distinct().ToArray(), correlationId, ct))
                        entities[Key("link", fields.Key)] = fields.Value;
                }
                else
                {
                    foreach (var fields in await ReadFoldersAsync(group.Select(g => g.Id).Distinct().ToArray(), correlationId, ct))
                        entities[Key("folder", fields.Key)] = fields.Value;
                }
            }
            return new AiSnapshot(entities);
        }
        catch (Exception ex)
        {
            // 快照是展示增强：任何读失败（含引擎把取消包装成的 LP.CANCELLED——下一次引擎调用会重新
            // 校验令牌）都只降级为"对账缺席"（回到引擎 diff/实体级原口径），绝不让回合失败、
            // 更不能让**已提交命令的台账构建**被快照故障拖掉（那会漏记变更）。
            LpLog.Warn($"reconcile snapshot unavailable for '{command}' (reconcile skipped)", ex, category: "ai.ledger");
            return null;
        }
    }

    /// <summary>目标定位（按命令）：入参 ID +（执行后）结果派生的新 ID。缺键/形状不符 = 空（命令自身校验会报错）。
    /// public 纯函数：白名单命令 → 读面 ID 的映射是判据的一部分，测试跨程序集直接断言（不开内部可见性后门）。</summary>
    public static IReadOnlyList<(string Type, string Id)> Targets(string command, JsonElement args, JsonElement? resultData)
    {
        var targets = new List<(string, string)>();
        void Add(string type, string? id)
        {
            if (!string.IsNullOrEmpty(id)) targets.Add((type, id!));
        }
        void AddMany(string type, string prop)
        {
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var arr)
                && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) Add(type, item.GetString());
        }
        string? Arg(string prop)
            => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var v)
               && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? Result(string prop)
            => resultData is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty(prop, out var v)
               && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        switch (command)
        {
            case "links.create":
                if (resultData is not null) Add("link", Result("id"));
                break;
            case "links.update":
            case "links.trash":
                Add("link", Arg("id"));
                break;
            case "links.move_batch":
                AddMany("link", "link_ids");
                break;
            case "folders.create":
                if (resultData is not null) Add("folder", Result("id"));
                break;
            case "folders.update":
            case "folders.move":
            case "folders.delete":
                Add("folder", Arg("folder_id"));
                break;
            case "folders.move_batch":
                AddMany("folder", "folder_ids");
                break;
            case "folders.copy":
                Add("folder", Arg("folder_id"));                       // 源（应无变化）
                if (resultData is not null) Add("folder", Result("new_folder_id"));   // 副本（创建型）
                break;
            case "trash.restore":
                Add("link", Arg("id"));
                break;
            case "trash.restore_unit":
                Add("folder", Arg("unit_id"));
                break;
            case "trash.restore_batch":
                AddMany("link", "link_ids");
                AddMany("folder", "folder_ids");
                break;
        }
        return targets;
    }

    private async Task<Dictionary<string, IReadOnlyDictionary<string, JsonElement?>>> ReadLinksAsync(
        IReadOnlyList<string> ids, string correlationId, CancellationToken ct)
    {
        var data = await client.QueryAsync<object>("links.query", new
        {
            filter = new[] { new { field = "id", op = "in", value = ids } },
            fields = new[] { "id", "url", "title", "description", "list_id", "is_important" },
            page = new { index = 1, size = 0 },
        }, new CallOptions(CorrelationId: correlationId, Caller: caller), ct).ConfigureAwait(false);

        var map = new Dictionary<string, IReadOnlyDictionary<string, JsonElement?>>(StringComparer.Ordinal);
        var json = JsonSerializer.SerializeToElement(data, SnapshotJson);
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array) return map;

        foreach (var row in items.EnumerateArray())
        {
            if (!row.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
            // folder_id 由读面的 list_id 映射（diff 字段口径以 ENGINE-API §1 为准）
            map[idEl.GetString()!] = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
            {
                ["url"] = Str(row, "url"),
                ["title"] = Str(row, "title"),
                ["description"] = Str(row, "description"),
                ["folder_id"] = Str(row, "list_id"),
                ["is_important"] = Bool(row, "is_important"),
            };
        }
        return map;
    }

    private async Task<Dictionary<string, IReadOnlyDictionary<string, JsonElement?>>> ReadFoldersAsync(
        IReadOnlyList<string> ids, string correlationId, CancellationToken ct)
    {
        var folders = await client.QueryAsync<List<FolderDto>>(
            "folders.tree", null, new CallOptions(CorrelationId: correlationId, Caller: caller), ct)
            .ConfigureAwait(false);

        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var map = new Dictionary<string, IReadOnlyDictionary<string, JsonElement?>>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (!wanted.Contains(folder.FolderId)) continue;
            map[folder.FolderId] = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
            {
                ["name"] = FieldValue.Str(folder.Name),
                ["parent_id"] = FieldValue.Str(folder.ParentId),
                // description 不入：任何查询 DTO 都不暴露该字段（读面边界，见类注释）
            };
        }
        return map;
    }

    private static JsonElement? Str(JsonElement row, string prop)
        => row.TryGetProperty(prop, out var v) ? v.Clone() : null;

    private static JsonElement? Bool(JsonElement row, string prop)
        => row.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.Clone()
            : null;

    // ── 对账（纯函数：diff 自算 + 与引擎 diff 比对）────────────────────────────

    /// <summary>
    /// 对账主函数：对每个快照目标自算字段级 diff，与引擎 diff 比对产出结论。
    /// ① 引擎有**可观测** diff 条目 → 逐条校验（值不一致 / 引擎有而对账无 / 对账有而引擎无 → Mismatched）；
    /// ② 引擎 touched 无 diff → 对账有变化 → Reconciled 兜底；
    /// ③ 引擎连 touched 都没有 → Unreported（只告警不建账）。
    /// 引擎只报了不可观测字段（folder.description 等）→ 视同无可观测 diff（不判不一致）。
    /// </summary>
    public static ReconcileOutcome Compute(string command, AiSnapshot before, AiSnapshot after, ChangeSet? engine)
    {
        var engineDiff = new Dictionary<string, List<FieldChange>>(StringComparer.Ordinal);
        if (engine?.Diff is { Count: > 0 } diff)
            foreach (var g in diff.GroupBy(f => Key(f.Type, f.Id)))
                engineDiff[g.Key] = g.ToList();

        var touched = new HashSet<string>(StringComparer.Ordinal);
        if (engine?.Touched is { Count: > 0 } touchedRefs)
            foreach (var r in touchedRefs) touched.Add(Key(r.Type, r.Id));

        var reconciled = new Dictionary<string, IReadOnlyList<AiFieldChange>>(StringComparer.Ordinal);
        var mismatched = new HashSet<string>(StringComparer.Ordinal);
        var unreported = new HashSet<string>(StringComparer.Ordinal);

        var keys = before.Entities.Keys.Union(after.Entities.Keys);
        foreach (var key in keys)
        {
            var parts = key.Split('\u0000');
            var (type, id) = (parts[0], parts[1]);
            before.Entities.TryGetValue(key, out var b);
            after.Entities.TryGetValue(key, out var a);
            var observed = DiffSnapshot(type, id, b, a);

            engineDiff.TryGetValue(key, out var engineEntries);
            var observable = engineEntries?.Where(e => ObservableFields[type].Contains(e.Field)).ToList()
                             ?? new List<FieldChange>();

            if (engineEntries is { Count: > 0 })
            {
                // 校验模式（引擎 diff = 权威口径，对账只当校验器）：
                // ① 引擎报的每条**可观测**字段，对账必须同值确认（读不到的字段如 folder.description 不参与）；
                // ② 对账观测到的每个字段变化，引擎必须也报了（引擎漏报 → 不一致；值由①覆盖比对）。
                foreach (var e in observable)
                {
                    var mine = observed.FirstOrDefault(x => x.Field == e.Field);
                    if (mine is not null)
                    {
                        if (!ValuesEqual(e.Before, mine.Before) || !ValuesEqual(e.After, mine.After))
                        {
                            mismatched.Add(key);
                            break;
                        }
                    }
                    else if (!ValuesEqual(e.Before, e.After))
                    {
                        // 对账没观测到该字段变化，但引擎报了**读面可区分**的变化 → 真不一致；
                        // 引擎报的变化若归一后不可区分（null ↔ ""——DTO 投影的固有盲区）→ 视为一致，不误报。
                        mismatched.Add(key);
                        break;
                    }
                }
                if (!mismatched.Contains(key))
                    foreach (var m in observed)
                        if (engineEntries.All(e => e.Field != m.Field))
                        {
                            mismatched.Add(key);
                            break;
                        }
            }
            else if (observed.Count > 0)
            {
                if (touched.Contains(key)) reconciled[key] = observed;
                else unreported.Add(key);   // 引擎未上报该实体：只告警（台账只记引擎事实）
            }
        }

        foreach (var key in mismatched)
            LpLog.Warn($"reconcile mismatch: engine diff disagrees with snapshot for '{command}' ({key.Replace('\u0000', '/')})",
                category: "ai.ledger");
        foreach (var key in unreported)
            LpLog.Warn($"reconcile observed a change the engine did not report: '{command}' ({key.Replace('\u0000', '/')})",
                category: "ai.ledger");

        return new ReconcileOutcome { Reconciled = reconciled, Mismatched = mismatched, Unreported = unreported };
    }

    /// <summary>
    /// 对账自算 diff（镜像 EntityDiff 语义、但只覆盖可观测字段集）：
    /// 前缺席后在 = 创建（Before 不适用、After 全字段）；前在后缺席 = 删除（After 不适用）；
    /// 两侧都在只报**真变了**的字段（大小写敏感逐字比较）。
    /// </summary>
    internal static IReadOnlyList<AiFieldChange> DiffSnapshot(string type, string id,
        IReadOnlyDictionary<string, JsonElement?>? before, IReadOnlyDictionary<string, JsonElement?>? after)
    {
        var fields = ObservableFields[type];
        var diff = new List<AiFieldChange>(fields.Count);

        if (before is null && after is not null)
        {
            foreach (var f in fields) diff.Add(new AiFieldChange(f, null, after[f]));
            return diff;
        }
        if (before is not null && after is null)
        {
            foreach (var f in fields) diff.Add(new AiFieldChange(f, before[f], null));
            return diff;
        }
        if (before is null) return diff;   // 双缺席：无从比对

        foreach (var f in fields)
        {
            before.TryGetValue(f, out var b);
            after!.TryGetValue(f, out var a);
            if (!ValuesEqual(b, a)) diff.Add(new AiFieldChange(f, b, a));
        }
        return diff;
    }

    /// <summary>值相等（含读面归一）：null ≡ ""（DTO 投影把 null 归一为 ""，读面不可区分）。
    /// C# null = 该时刻字段不适用（与 JSON null 的"字段值为空"严格区分）。</summary>
    internal static bool ValuesEqual(JsonElement? a, JsonElement? b)
    {
        if (!a.HasValue && !b.HasValue) return true;      // 双方不适用
        if (!a.HasValue != !b.HasValue) return false;     // 一侧不适用另一侧有值 = 结构错位
        var av = a!.Value;
        var bv = b!.Value;
        if (av.ValueKind == JsonValueKind.Null && bv.ValueKind == JsonValueKind.Null) return true;
        if (IsReadableEmpty(av) && IsReadableEmpty(bv)) return true;   // null ≡ "" 归一
        if (av.ValueKind != bv.ValueKind) return false;
        return av.ValueKind == JsonValueKind.String
            ? string.Equals(av.GetString(), bv.GetString(), StringComparison.Ordinal)
            : string.Equals(av.GetRawText(), bv.GetRawText(), StringComparison.Ordinal);
    }

    private static bool IsReadableEmpty(JsonElement v)
        => v.ValueKind == JsonValueKind.Null
           || (v.ValueKind == JsonValueKind.String && v.GetString()!.Length == 0);
}
