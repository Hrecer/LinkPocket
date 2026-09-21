using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>
/// links.query（★ 引擎能力，不接 UI）：结构化查询器（标准参数）——
/// filter（字段/操作符白名单）+ sort（白名单下推）+ page（size=0 全量）+ fields 投影。
/// AI 可表达任意过滤；UI 不消费本命令。
/// </summary>
internal sealed class LinkQueryHandler : ICommandHandler
{
    /// <summary>字段 → 允许操作符白名单（字段集合；扩展 = 在此登记字段映射）。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> FieldOps =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["folder_id"] = new HashSet<string>(["eq", "isnull"]),
            ["title"] = new HashSet<string>(["contains"]),
            ["url"] = new HashSet<string>(["contains", "starts"]),
            ["description"] = new HashSet<string>(["contains"]),
            ["created_at"] = new HashSet<string>(["gt", "gte", "lt", "lte", "between"]),
            ["updated_at"] = new HashSet<string>(["gt", "gte", "lt", "lte", "between"]),
            ["last_visited_at"] = new HashSet<string>(["gt", "gte", "lt", "lte", "between", "isnull"]),
            ["visit_count"] = new HashSet<string>(["gt", "gte", "lt", "lte", "between"]),
            ["is_important"] = new HashSet<string>(["eq"]),
        };

    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.query",
        Category: "links",
        Description: "Structured link query: filter ([{field,op,value}]) + sort ([{field,dir}]) + page ({index,size}, size=0 for all) + fields projection",
        Parameters:
        [
            ParamSpec.Opt<JsonElement>("filter", "Filter condition array"),
            ParamSpec.Opt<JsonElement>("sort", "Sort array"),
            ParamSpec.Opt<JsonElement>("page", "Paging {index,size}"),
            ParamSpec.Opt<JsonElement>("fields", "Projected field name array"),
        ],
        Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var correlationId = ctx.CorrelationId;
        var filter = ParseFilter(args, correlationId);
        var sort = ParseSort(args, correlationId);
        var page = ParsePage(args);
        var fields = ParseFields(args, correlationId);
        var ct = ctx.Ct;

        var spec = new LinkQuerySpec { Filter = filter, Sort = sort, Page = page };
        var items = await ctx.Uow.Links.ListAsync(spec, ct);
        var total = page.Size > 0 ? await ctx.Uow.Links.CountAsync(filter, ct) : items.Count;
        var pageCount = page.Size > 0 ? (int)Math.Ceiling(total / (double)page.Size) : 1;

        IReadOnlyList<object> projected = fields == null
            ? items.Select(l => (object)l.ToDto()).ToList()
            : items.Select(l => (object)Project(l.ToDto(), fields)).ToList();

        return CommandResult.Ok(new PagedLinkResult(projected, total, page.Index, pageCount));
    }

    // —— 过滤解析：字段/操作符白名单 + 同字段重复拒绝 ——

    private static LinkFilter ParseFilter(JsonElement args, string correlationId)
    {
        var raw = CommandArgs.Raw(args, "filter");
        if (raw is not { ValueKind: JsonValueKind.Array } array) return new LinkFilter();

        var filter = new LinkFilter();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var cond in array.EnumerateArray())
        {
            var field = GetString(cond, "field", correlationId);
            var op = GetString(cond, "op", correlationId);

            if (!FieldOps.TryGetValue(field, out var ops))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.EnumOutOfRange, $"unknown filter field '{field}'", correlationId: correlationId));
            if (!ops.Contains(op))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.EnumOutOfRange, $"field '{field}' does not support operator '{op}'", correlationId: correlationId));
            if (!seen.Add(field))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.RequiredParam, $"field '{field}' appears twice in filter (one condition per field)", correlationId: correlationId));

            var value = cond.TryGetProperty("value", out var v) ? v : (JsonElement?)null;
            filter = ApplyCondition(filter, field, op, value, correlationId);
        }

        return filter;
    }

    private static LinkFilter ApplyCondition(LinkFilter filter, string field, string op, JsonElement? value, string correlationId)
        => field switch
        {
            "folder_id" => op == "isnull"
                ? filter with { Unfiled = true }
                : filter with { FolderId = new FolderId(RequireString(value, field, correlationId: correlationId)) },
            "title" => filter with { TitleContains = RequireString(value, field, correlationId) },
            "url" => op == "starts"
                ? filter with { UrlStarts = RequireString(value, field, correlationId) }
                : filter with { UrlContains = RequireString(value, field, correlationId) },
            "description" => filter with { DescriptionContains = RequireString(value, field, correlationId) },
            "is_important" => filter with { IsImportant = value is { ValueKind: JsonValueKind.True } },
            "visit_count" => ApplyVisitCount(filter, op, value, correlationId),
            _ => ApplyDate(filter, field, op, value, correlationId),
        };

    private static LinkFilter ApplyVisitCount(LinkFilter filter, string op, JsonElement? value, string correlationId)
    {
        var number = value is { ValueKind: JsonValueKind.Number } el && el.TryGetInt32(out var n)
            ? n
            : throw new EngineException(EngineErrors.Of(
                EngineErrors.TypeMismatch, "the value of a visit_count condition must be an integer", correlationId: correlationId));
        return op switch
        {
            "gte" => filter with { VisitCountMin = number },
            "gt" => filter with { VisitCountMin = number + 1 },
            "lte" => filter with { VisitCountMax = number },
            "lt" => filter with { VisitCountMax = number - 1 },
            "between" => filter with
            {
                VisitCountMin = ParseIntBound(value, 0),
                VisitCountMax = ParseIntBound(value, 1),
            },
            _ => filter,
        };
    }

    private static int ParseIntBound(JsonElement? array, int index)
        => array is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > index && arr[index].TryGetInt32(out var n)
            ? n
            : throw new EngineException(EngineErrors.Of(
                EngineErrors.TypeMismatch, "the value of between must be a [lower, upper] array", correlationId: "unknown"));

    private static LinkFilter ApplyDate(LinkFilter filter, string field, string op, JsonElement? value, string correlationId)
    {
        if (op == "isnull")
            return filter with { NeverVisited = true };   // 仅 last_visited_at 允许（白名单已保证）

        DateTime from;
        DateTime to;
        if (op == "between")
        {
            from = ParseDateBound(value, 0, field, correlationId);
            to = ParseDateBound(value, 1, field, correlationId);
        }
        else
        {
            var single = ParseSingle(value, field, correlationId);
            switch (op)
            {
                case "gte": from = single; to = DateTime.MaxValue; break;
                case "gt": from = single.AddSeconds(1); to = DateTime.MaxValue; break;
                case "lte": from = DateTime.MinValue; to = single; break;
                default: from = DateTime.MinValue; to = single.AddSeconds(-1); break;   // lt
            }
        }

        return field switch
        {
            "created_at" => filter with { CreatedFrom = from, CreatedTo = to },
            "updated_at" => filter with { UpdatedFrom = from, UpdatedTo = to },
            _ => filter with { LastVisitedFrom = from, LastVisitedTo = to },
        };
    }

    private static DateTime ParseDateBound(JsonElement? array, int index, string field, string correlationId)
        => array is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > index
            ? LinkSupport.ParseDate(arr[index].GetString() ?? string.Empty, $"{field}.between[{index}]")
            : throw new EngineException(EngineErrors.Of(
                EngineErrors.TypeMismatch, $"the value of '{field} between' must be a [from, to] array", correlationId: correlationId));

    private static DateTime ParseSingle(JsonElement? value, string field, string correlationId)
        => LinkSupport.ParseDate(
            value is { ValueKind: JsonValueKind.String } el ? el.GetString() ?? string.Empty : string.Empty, field);

    private static string RequireString(JsonElement? value, string field, string correlationId)
        => value is { ValueKind: JsonValueKind.String } el && !string.IsNullOrEmpty(el.GetString())
            ? el.GetString()!
            : throw new EngineException(EngineErrors.Of(
                EngineErrors.TypeMismatch, $"the value of field '{field}' must be a non-empty string", correlationId: correlationId));

    // —— 排序 / 分页 / 投影 ——

    private static IReadOnlyList<SortSpec> ParseSort(JsonElement args, string correlationId)
    {
        var raw = CommandArgs.Raw(args, "sort");
        if (raw is not { ValueKind: JsonValueKind.Array } array) return [];

        var specs = new List<SortSpec>();
        foreach (var clause in array.EnumerateArray())
        {
            var field = GetString(clause, "field", correlationId);
            if (!QueryParsing.LinkSortFields.Contains(field))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.EnumOutOfRange, $"unknown sort field '{field}'", correlationId: correlationId));
            var dirText = GetString(clause, "dir", correlationId);
            var dir = dirText switch
            {
                "asc" => SortDir.Asc,
                "desc" => SortDir.Desc,
                _ => throw new EngineException(EngineErrors.Of(
                    EngineErrors.EnumOutOfRange, $"sort direction must be asc/desc, got '{dirText}'", correlationId: correlationId)),
            };
            specs.Add(new SortSpec(field, dir));
        }

        return specs;
    }

    private static PageSpec ParsePage(JsonElement args)
    {
        var raw = CommandArgs.Raw(args, "page");
        if (raw is not { ValueKind: JsonValueKind.Object } page) return new PageSpec();
        var index = page.TryGetProperty("index", out var i) && i.TryGetInt32(out var idx) ? Math.Max(1, idx) : 1;
        var size = page.TryGetProperty("size", out var s) && s.TryGetInt32(out var sz) ? Math.Max(0, sz) : 0;
        return new PageSpec(index, size);
    }

    private static IReadOnlySet<string>? ParseFields(JsonElement args, string correlationId)
    {
        var raw = CommandArgs.Raw(args, "fields");
        if (raw is not { ValueKind: JsonValueKind.Array } array) return null;

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "url", "title", "description", "favicon_url", "list_id",
            "last_visited_at", "visit_count", "is_important", "created_at", "updated_at",
        };
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var name = item.GetString() ?? string.Empty;
            if (!allowed.Contains(name))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.EnumOutOfRange, $"unknown projection field '{name}'", correlationId: correlationId));
            fields.Add(name);
        }

        return fields;
    }

    /// <summary>fields 投影：LinkDto → snake_case 字段字典（白名单已校验）。</summary>
    private static IReadOnlyDictionary<string, object?> Project(LinkDto dto, IReadOnlySet<string> fields)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fields.Contains("id")) row["id"] = dto.LinkId;
        if (fields.Contains("url")) row["url"] = dto.Url;
        if (fields.Contains("title")) row["title"] = dto.Title;
        if (fields.Contains("description")) row["description"] = dto.Description;
        if (fields.Contains("favicon_url")) row["favicon_url"] = dto.FaviconUrl;
        if (fields.Contains("list_id")) row["list_id"] = dto.ListId;
        if (fields.Contains("last_visited_at")) row["last_visited_at"] = dto.LastVisitedAt;
        if (fields.Contains("visit_count")) row["visit_count"] = dto.VisitCount;
        if (fields.Contains("is_important")) row["is_important"] = dto.IsImportant;
        if (fields.Contains("created_at")) row["created_at"] = dto.CreatedAt;
        if (fields.Contains("updated_at")) row["updated_at"] = dto.UpdatedAt;
        return row;
    }

    private static string GetString(JsonElement element, string name, string correlationId)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } text
            ? text
            : throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, $"the condition is missing the string parameter '{name}'", correlationId: correlationId));
}
