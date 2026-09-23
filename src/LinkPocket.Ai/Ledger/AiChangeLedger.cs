using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 变更台账（**唯一实现**）：把一次工具调用的引擎结果转成逐条变更记录。
///
/// <para><b>粒度优先级</b>（AI-ASSISTANT §7.1）：引擎字段级 diff = 权威（分组到实体，Source=EngineDiff）；
/// 只在 touched 里、没有 diff 的实体 = 兜底（Source=EntityOnly，界面如实标注"仅实体级"）。</para>
///
/// <para><b>名称/路径在变更发生时刻解析</b>：台账记的是当时的事实——实体后来被改名/删除不改写历史。
/// 解析用两次引擎查询（<c>folders.tree</c> 一次 + <c>links.query</c> 一次），失败只降级为"没有名称"
/// （记日志、绝不因此让回合失败）。已离开主表的实体（删除类）用 diff 的 Before 值当名称与位置。</para>
/// </summary>
internal sealed class AiChangeLedger(EngineClient client, CallerRef caller)
{
    /// <summary>单次调用的名称解析规模上限（超过部分保持 ID 显示，不无限扩大查询）。</summary>
    private const int ResolveIdLimit = 200;

    private CallOptions Options => new(Caller: caller);

    /// <summary>查询结果的 JSON 口径（与引擎 wire 的 snake_case 一致；null 字段不出现）。</summary>
    private static readonly JsonSerializerOptions RowJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>从一次工具调用的结果产出台账条目；<paramref name="maxChanges"/> 之外的条目丢弃并如实标注截断。
    /// <paramref name="nextSeq"/> 给出会话级序号（条目按时间线排序的唯一依据）。</summary>
    public async Task<List<AiChange>> BuildAsync(string turnId, string callId, string command,
        CommandResult<JsonElement> result, int maxChanges, bool undoable, Func<int> nextSeq, CancellationToken ct)
    {
        if (result.Changes is not { } changeSet) return [];
        var entries = CollectEntities(changeSet);
        if (entries.Count == 0) return [];

        var omitted = Math.Max(0, entries.Count - maxChanges);
        if (omitted > 0) entries = entries.Take(maxChanges).ToList();

        var names = await TryResolveNamesAsync(entries, ct);
        var now = DateTimeOffset.UtcNow;
        var changes = new List<AiChange>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var kind = ClassifyChange(command);
            var identity = IdentityOf(names, entry);
            changes.Add(new AiChange(
                ChangeId: $"d-{Guid.NewGuid():N}",
                Seq: nextSeq(),
                TurnId: turnId,
                CallId: callId,
                Command: command,
                Kind: kind,
                EntityType: entry.Type,
                EntityId: entry.Id,
                EntityName: identity.Name,
                EntityPath: identity.Path,
                EntityExists: ResolveExistence(entry, kind, identity),
                Fields: entry.Fields,
                Outcome: AiChangeOutcome.Applied,
                ErrorCode: null,
                Undoable: undoable,
                Source: entry.Fields is null ? AiChangeSource.EntityOnly : AiChangeSource.EngineDiff,
                Truncated: omitted > 0 && i == entries.Count - 1,
                Omitted: omitted > 0 && i == entries.Count - 1 ? omitted : 0,
                At: now,
                CorrelationId: null,
                BatchId: null));
        }

        if (omitted > 0)
            LpLog.Warn($"change ledger truncated: {omitted} entry(ies) omitted for '{command}'", category: "ai.ledger");
        return changes;
    }

    // ── 实体收集：diff 分组（权威） + touched 兜底 ──────────────────────

    private sealed record EntityEntry(string Type, string Id, IReadOnlyList<AiFieldChange>? Fields);

    private static List<EntityEntry> CollectEntities(ChangeSet changeSet)
    {
        var entries = new List<EntityEntry>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var fieldsByEntry = new List<List<AiFieldChange>>();

        if (changeSet.Diff is { Count: > 0 } diff)
        {
            foreach (var field in diff)
            {
                var key = $"{field.Type}\u0000{field.Id}";
                if (!index.TryGetValue(key, out var at))
                {
                    index[key] = entries.Count;
                    fieldsByEntry.Add([]);
                    entries.Add(new EntityEntry(field.Type, field.Id, fieldsByEntry[^1]));
                    at = entries.Count - 1;
                }
                fieldsByEntry[at].Add(new AiFieldChange(field.Field, field.Before, field.After));
            }
        }

        foreach (var touched in changeSet.Touched)
        {
            if (touched.Id == "*") continue;   // 通配实体（如 audit.prune）不是具体对象，不入台账
            if (index.ContainsKey($"{touched.Type}\u0000{touched.Id}")) continue;
            entries.Add(new EntityEntry(touched.Type, touched.Id, null));
        }

        return entries;
    }

    // ── 名称与路径解析（失败降级，绝不让回合失败）────────────────────────

    private sealed class Identity
    {
        public string? Name { get; init; }
        public string? Path { get; init; }
        public bool? Found { get; init; }
    }

    private async Task<Dictionary<string, Identity>> TryResolveNamesAsync(List<EntityEntry> entries, CancellationToken ct)
    {
        var identities = new Dictionary<string, Identity>(StringComparer.Ordinal);
        try
        {
            var folderIds = new HashSet<string>(StringComparer.Ordinal);
            var linkIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry.Type == "folder") folderIds.Add(entry.Id);
                else if (entry.Type == "link") linkIds.Add(entry.Id);

                // 位置字段（folder_id / parent_id）的两侧值同样要解析成路径：删除类条目靠它回答"原位置"
                foreach (var field in entry.Fields ?? [])
                {
                    if (field.Field is not ("folder_id" or "parent_id")) continue;
                    if (AsString(field.Before) is { } before) folderIds.Add(before);
                    if (AsString(field.After) is { } after) folderIds.Add(after);
                }
            }

            var folderMap = new Dictionary<string, (string Name, string? ParentId)>(StringComparer.Ordinal);
            if (folderIds.Count > 0)
            {
                var folders = await client.QueryAsync<List<FolderDto>>("folders.tree", null, Options, ct).ConfigureAwait(false);
                foreach (var folder in folders)
                    folderMap[folder.FolderId] = (folder.Name, folder.ParentId);
            }

            var linkMap = new Dictionary<string, (string? Title, string? Url, string? FolderId)>(StringComparer.Ordinal);
            if (linkIds.Count > 0)
            {
                var ids = linkIds.Take(ResolveIdLimit).ToArray();
                // 结果类型由模块声明（未知给 object），序列化口径 = 引擎的 snake_case
                var data = await client.QueryAsync<object>("links.query", new
                {
                    filter = new[] { new { field = "id", op = "in", value = ids } },
                    fields = new[] { "id", "title", "url", "list_id" },
                    page = new { index = 1, size = 0 },
                }, Options, ct).ConfigureAwait(false);
                var rows = data is null ? default : JsonSerializer.SerializeToElement(data, RowJson);
                if (rows.ValueKind == JsonValueKind.Object && rows.TryGetProperty("items", out var items)
                    && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in items.EnumerateArray())
                    {
                        if (AsString(row, "id") is not { } id) continue;
                        linkMap[id] = (AsString(row, "title"), AsString(row, "url"), AsString(row, "list_id"));
                    }
                }
            }

            foreach (var entry in entries)
            {
                var key = $"{entry.Type}\u0000{entry.Id}";
                identities[key] = entry.Type switch
                {
                    "folder" => FolderIdentity(entry, folderMap),
                    "link" => LinkIdentity(entry, folderMap, linkMap),
                    _ => new Identity(),
                };
            }

            return identities;
        }
        catch (Exception ex) when (ex is EngineException or AiException or JsonException)
        {
            // 名称解析是展示增强：失败只降级（ID 仍然可读），绝不因此让回合失败
            LpLog.Warn("change ledger name resolution failed (entries keep plain IDs)", ex, category: "ai.ledger");
            return identities;
        }

        static Identity FolderIdentity(EntityEntry entry,
            Dictionary<string, (string Name, string? ParentId)> folderMap)
        {
            var found = folderMap.TryGetValue(entry.Id, out var live);
            var name = found ? live.Name : BeforeValue(entry, "name");   // 删除类：用 diff 的 Before 快照
            var parentId = found ? live.ParentId : BeforeValue(entry, "parent_id");
            return new Identity
            {
                Name = name,
                Path = FolderPath(parentId, folderMap),                  // 位置 = 容器目录
                Found = found,
            };
        }

        static Identity LinkIdentity(EntityEntry entry,
            Dictionary<string, (string Name, string? ParentId)> folderMap,
            Dictionary<string, (string? Title, string? Url, string? FolderId)> linkMap)
        {
            var live = linkMap.TryGetValue(entry.Id, out var row);
            var name = live
                ? (string.IsNullOrWhiteSpace(row.Title) ? row.Url : row.Title)
                : (BeforeValue(entry, "title") ?? BeforeValue(entry, "url"));
            var folderId = live ? row.FolderId : BeforeValue(entry, "folder_id");
            return new Identity
            {
                Name = name,
                Path = FolderPath(folderId, folderMap),
                Found = live,
            };
        }

        // 路径只沿"活着的目录"向上走（删除类条目的原父可能也已被删 → 如实给 @unknown）
        static string? FolderPath(string? folderId, Dictionary<string, (string Name, string? ParentId)> folderMap)
        {
            var segments = new List<string>();
            var cursor = folderId;
            var guard = 0;
            while (cursor != null && guard++ < 1000)
            {
                if (!folderMap.TryGetValue(cursor, out var node)) return BookmarkPath.UnknownToken;
                segments.Insert(0, node.Name);
                cursor = node.ParentId;
            }
            return BookmarkPath.Build(BookmarkPath.RootToken, segments);
        }
    }

    private static Identity IdentityOf(Dictionary<string, Identity> map, EntityEntry entry)
        => map.TryGetValue($"{entry.Type}\u0000{entry.Id}", out var identity) ? identity : new Identity();

    private static string? BeforeValue(EntityEntry entry, string field)
        => entry.Fields?.FirstOrDefault(f => f.Field == field) is { Before: { } value } ? AsString(value) : null;

    private static string? AsString(JsonElement? value)
        => value?.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            _ => null,
        };

    private static string? AsString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ResolveExistence(EntityEntry entry, AiChangeKind kind, Identity identity)
        => identity.Found ?? kind is not (AiChangeKind.Delete or AiChangeKind.Purge);

    // ── 变更分类（命令 → 台账分类）────────────────────────────────────

    /// <summary>变更分类：命令名蕴含的动作语义（找不到专门语义的按"诊断/其它"如实归类）。</summary>
    private static AiChangeKind ClassifyChange(string command) => command switch
    {
        // dedup.apply 的唯一语义 = 把多余项移入回收站（实体级已由嵌套 links.trash 的 diff 描述）
        "dedup.apply" => AiChangeKind.Delete,
        _ when command.Contains(".create", StringComparison.Ordinal) => AiChangeKind.Create,
        _ when command.Contains(".copy", StringComparison.Ordinal) => AiChangeKind.Create,
        _ when command.Contains(".trash", StringComparison.Ordinal) => AiChangeKind.Delete,
        _ when command.Contains(".delete", StringComparison.Ordinal) => AiChangeKind.Delete,
        _ when command.Contains(".purge", StringComparison.Ordinal) => AiChangeKind.Purge,
        _ when command.Contains("restore", StringComparison.Ordinal) => AiChangeKind.Restore,
        _ when command.Contains(".move", StringComparison.Ordinal) => AiChangeKind.Move,
        _ when command.Contains("import", StringComparison.Ordinal) => AiChangeKind.Import,
        _ when command.Contains("export", StringComparison.Ordinal) => AiChangeKind.Export,
        _ when command.Contains("update", StringComparison.Ordinal) => AiChangeKind.Update,
        _ => AiChangeKind.Diagnostic,
    };
}
