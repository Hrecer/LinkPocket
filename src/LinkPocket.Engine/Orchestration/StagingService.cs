using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// Staging 服务（IStagingService）：AI 文件准备区。
/// StageAsync = 拷入 + SHA256 + 登记；TransformAsync = 纯函数算子管道（filter_links /
/// rename_folder / map_field / strip_prefix / dedupe / reencode），产物仍 staged，dry_run 只出预览；
/// CommitAsync = 把 staged 文件转交正式命令（如 bookmarks.import，file_path 自动并入参数）。
/// 规范化形态 = 链接对象数组 JSON：<c>[{ "url", "title", "description", "favicon_url", "folder" }]</c>。
/// </summary>
public sealed class StagingService : IStagingService
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, StagedFile> _files = new(StringComparer.Ordinal);
    private readonly Func<IEngine>? _engineAccessor;

    public StagingService(string? root = null, Func<IEngine>? engineAccessor = null)
    {
        // 缺省根 = 系统临时目录；`LP_TEMP_ROOT` 可把它指到工作区内（CI/测试用，避免在用户目录留临时文件）
        _root = root ?? Path.Combine(TempArea.Resolve(), "linkpocket-staging");
        _engineAccessor = engineAccessor;
        Directory.CreateDirectory(_root);
    }

    /// <summary>Staging 根目录（diagnostics / 运维面用）。</summary>
    public string Root => _root;

    public Task<StagedFile> StageAsync(string sourcePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new EngineException(EngineErrors.Of(EngineErrors.InvalidPath,
                $"file does not exist: {sourcePath}", details: JsonSerializer.SerializeToElement(new { @param = "source_path" })));

        var stagingId = Guid.NewGuid().ToString("N");
        var fileName = Path.GetFileName(sourcePath);
        var fullPath = Path.Combine(_root, $"{stagingId}__{fileName}");
        File.Copy(sourcePath, fullPath, overwrite: true);

        var staged = new StagedFile(
            StagingId: stagingId,
            FileName: fileName,
            FullPath: fullPath,
            SizeBytes: new FileInfo(fullPath).Length,
            Sha256: Sha256Hex.OfFile(fullPath),
            StagedAt: DateTimeOffset.Now);
        _files[stagingId] = staged;
        return Task.FromResult(staged);
    }

    public Task<IReadOnlyList<StagedFile>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<StagedFile> list = [.. _files.Values.OrderBy(f => f.StagedAt)];
        return Task.FromResult(list);
    }

    public Task<bool> DiscardAsync(string stagingId, CancellationToken ct)
    {
        if (!_files.TryRemove(stagingId, out var staged))
            return Task.FromResult(false);
        try
        {
            File.Delete(staged.FullPath);
        }
        catch (FileNotFoundException)
        {
            // 文件已不存在 = 丢弃目标已达成（幂等语义）；其余 IO 异常照抛
        }
        catch (DirectoryNotFoundException)
        {
            // 同上：staging 根目录已被外部清理
        }
        return Task.FromResult(true);
    }

    public async Task<StagingTransformReport> TransformAsync(string stagingId, IReadOnlyList<TransformOp> ops,
        bool dryRun, CancellationToken ct)
    {
        if (!_files.TryGetValue(stagingId, out var staged))
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"staged file does not exist: {stagingId}"));

        var bytes = await File.ReadAllBytesAsync(staged.FullPath, ct);
        var applied = new List<string>();
        JsonElement root = default;
        var parsed = false;
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        foreach (var op in ops)
        {
            ct.ThrowIfCancellationRequested();
            if (op.Op == "reencode")
            {
                // reencode 必须以「未解析 JSON」前的字节流为输入：一旦任一 JSON 算子已解析 root，
                // 最终落盘走 root 的序列化、bytes 会被整体忽略 → 该算子沦为静默空操作。
                // 这里显式拒绝而非假装生效（曾静默吞掉：applied 记了但内容没变）。
                if (parsed)
                    throw new EngineException(EngineErrors.Of(EngineErrors.TypeMismatch,
                        "reencode must appear before the first JSON transform operator (filter_links/rename_folder/map_field/strip_prefix/dedupe)"));
                var from = GetString(op.Args, "from") ?? "utf-8";
                var source = Encoding.GetEncoding(from);
                var text = source.GetString(bytes);
                bytes = encoding.GetBytes(text);
                applied.Add($"reencode({from}→utf-8)");
                continue;
            }

            if (!parsed)
            {
                var text = encoding.GetString(StripBom(bytes, encoding));
                root = JsonSerializer.Deserialize<JsonElement>(text);
                parsed = true;
            }

            var (result, count) = op.Op switch
            {
                "filter_links" => ApplyFilter(root, op.Args),
                "rename_folder" => ApplyRenameFolder(root, op.Args),
                "map_field" => ApplyMapField(root, op.Args),
                "strip_prefix" => ApplyStripPrefix(root, op.Args),
                "dedupe" => ApplyDedupe(root, op.Args),
                _ => throw new EngineException(EngineErrors.Of(EngineErrors.EnumOutOfRange,
                    $"unknown transform operator '{op.Op}' (available: filter_links/rename_folder/map_field/strip_prefix/dedupe/reencode)",
                    details: JsonSerializer.SerializeToElement(new { op = op.Op }))),
            };
            root = result;
            applied.Add(op.Args.ValueKind == JsonValueKind.Object
                ? $"{op.Op}({op.Args.GetRawText()})"
                : op.Op);
        }

        var itemsBefore = await CountItemsAsync(staged, ct);
        var itemsAfter = parsed && root.ValueKind == JsonValueKind.Array ? root.GetArrayLength() : itemsBefore;
        var preview = dryRun && parsed ? JsonSerializer.Serialize(root, WriteOptions) : null;

        if (!dryRun)
        {
            var content = parsed
                ? JsonSerializer.Serialize(root, WriteOptions)
                : encoding.GetString(bytes);
            await File.WriteAllTextAsync(staged.FullPath, content, encoding, ct);
            _files[stagingId] = staged with
            {
                SizeBytes = new FileInfo(staged.FullPath).Length,
                Sha256 = Sha256Hex.OfFile(staged.FullPath),
            };
        }

        return new StagingTransformReport(stagingId, dryRun, itemsBefore, itemsAfter, applied, preview);
    }

    public async Task<CommandResult> CommitAsync(string stagingId, string targetCommand,
        object? extraArgs = null, CallOptions? options = null, CancellationToken ct = default)
    {
        if (_engineAccessor is null)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.Internal,
                "StagingService has no engine accessor and cannot commit outside the pipeline (run it through the staging.commit command)"));
        var merged = BuildCommitArgs(stagingId, extraArgs);
        var r = await _engineAccessor().ExecuteAsync<object>(targetCommand, merged, options, ct);
        return new CommandResult(r.Data, r.Changes, r.AuditRef);
    }

    public Task<string> ReadTextAsync(string stagingId, CancellationToken ct)
    {
        if (!_files.TryGetValue(stagingId, out var staged))
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"staged file does not exist: {stagingId}"));
        return File.ReadAllTextAsync(staged.FullPath, ct);
    }

    /// <summary>构造转交正式命令的参数：file_path = staged 完整路径 + extraArgs 逐属性并入。</summary>
    internal JsonElement BuildCommitArgs(string stagingId, object? extraArgs)
    {
        if (!_files.TryGetValue(stagingId, out var staged))
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"staged file does not exist: {stagingId}"));

        var target = new System.Text.Json.Nodes.JsonObject
        {
            ["file_path"] = staged.FullPath,
        };
        if (EngineJson.ToJsonElement(extraArgs) is { ValueKind: JsonValueKind.Object } extra)
        {
            foreach (var prop in extra.EnumerateObject())
                if (prop.Name != "file_path")
                    target[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
        }
        return JsonSerializer.SerializeToElement(target);
    }

    /// <summary>暂存文件落盘/预览的序列化口径：不转义非 ASCII（保持中文可读），无缩进。</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private async Task<int> CountItemsAsync(StagedFile staged, CancellationToken ct)
    {
        try
        {
            var text = await File.ReadAllTextAsync(staged.FullPath, ct);   // StreamReader 自动剥离 BOM
            var el = JsonSerializer.Deserialize<JsonElement>(text);
            return el.ValueKind == JsonValueKind.Array ? el.GetArrayLength() : 0;
        }
        catch (JsonException)
        {
            return 0;   // 非规范化 JSON（如 Netscape HTML）无"项"计数
        }
    }

    /// <summary>剥离编码前导符（BOM）——外部来源的暂存文件常带 UTF-8 BOM（检测用标准 UTF-8 前导符，
    /// 与目标落盘编码无关）。</summary>
    private static byte[] StripBom(byte[] bytes, Encoding encoding)
    {
        var preamble = Encoding.UTF8.GetPreamble();   // EF BB BF
        if (preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble))
            return bytes[preamble.Length..];
        var own = encoding.GetPreamble();
        if (own.Length > 0 && bytes.AsSpan().StartsWith(own))
            return bytes[own.Length..];
        return bytes;
    }

    private static JsonElement[] AsItems(JsonElement root)
        => root.ValueKind == JsonValueKind.Array
            ? [.. root.EnumerateArray()]
            : throw new EngineException(EngineErrors.Of(EngineErrors.TypeMismatch,
                "transform operators require the staged content to be a JSON array of link objects ([{url,title,...}])"));

    private static string? GetString(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static (JsonElement Root, int Count) ApplyFilter(JsonElement root, JsonElement args)
    {
        var field = GetString(args, "field") ?? "url";
        var op = GetString(args, "op") ?? "contains";
        var value = GetString(args, "value") ?? string.Empty;
        var kept = AsItems(root).Where(item => Matches(item, field, op, value)).ToArray();
        return (JsonSerializer.SerializeToElement(kept), kept.Length);
    }

    private static bool Matches(JsonElement item, string field, string op, string value)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(field, out var v))
            return false;
        var text = v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString();
        return op switch
        {
            "eq" => string.Equals(text, value, StringComparison.OrdinalIgnoreCase),
            "contains" => text.Contains(value, StringComparison.OrdinalIgnoreCase),
            "starts" => text.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            _ => throw new EngineException(EngineErrors.Of(EngineErrors.EnumOutOfRange,
                $"filter_links supports only eq/contains/starts, got '{op}'")),
        };
    }

    private static (JsonElement Root, int Count) ApplyRenameFolder(JsonElement root, JsonElement args)
    {
        var from = GetString(args, "from") ?? throw Required("from");
        var to = GetString(args, "to") ?? throw Required("to");
        var items = AsItems(root).Select(item => WithField(item, "folder", to,
            predicate: existing => string.Equals(existing, from, StringComparison.Ordinal))).ToArray();
        return (JsonSerializer.SerializeToElement(items), items.Length);
    }

    private static (JsonElement Root, int Count) ApplyMapField(JsonElement root, JsonElement args)
    {
        var from = GetString(args, "from") ?? throw Required("from");
        var to = GetString(args, "to") ?? throw Required("to");
        var items = AsItems(root).Select(item => RenameKey(item, from, to)).ToArray();
        return (JsonSerializer.SerializeToElement(items), items.Length);
    }

    private static (JsonElement Root, int Count) ApplyStripPrefix(JsonElement root, JsonElement args)
    {
        var field = GetString(args, "field") ?? "url";
        var prefix = GetString(args, "prefix") ?? string.Empty;
        var items = AsItems(root).Select(item => StripPrefix(item, field, prefix)).ToArray();
        return (JsonSerializer.SerializeToElement(items), items.Length);
    }

    private static (JsonElement Root, int Count) ApplyDedupe(JsonElement root, JsonElement args)
    {
        var by = GetString(args, "by") ?? "url";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = AsItems(root).Where(item =>
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(by, out var v))
                return true;   // 缺键不参与去重，保守保留
            var key = v.ToString();
            return seen.Add(key);
        }).ToArray();
        return (JsonSerializer.SerializeToElement(kept), kept.Length);
    }

    private static JsonElement WithField(JsonElement item, string field, string value, Func<string?, bool> predicate)
    {
        if (item.ValueKind != JsonValueKind.Object) return item;
        var current = item.TryGetProperty(field, out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        if (!predicate(current)) return item;
        var target = new System.Text.Json.Nodes.JsonObject();
        foreach (var prop in item.EnumerateObject())
            target[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
        target[field] = value;
        return JsonSerializer.SerializeToElement(target);
    }

    private static JsonElement RenameKey(JsonElement item, string from, string to)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(from, out _)) return item;
        var target = new System.Text.Json.Nodes.JsonObject();
        foreach (var prop in item.EnumerateObject())
        {
            if (prop.Name == from) target[to] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
            else target[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
        }
        return JsonSerializer.SerializeToElement(target);
    }

    private static JsonElement StripPrefix(JsonElement item, string field, string prefix)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty(field, out var v)
            || v.ValueKind != JsonValueKind.String
            || v.GetString() is not { } text
            || !text.StartsWith(prefix, StringComparison.Ordinal))
            return item;

        var target = new System.Text.Json.Nodes.JsonObject();
        foreach (var prop in item.EnumerateObject())
        {
            if (prop.Name == field)
                target[prop.Name] = text[prefix.Length..];
            else
                target[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
        }
        return JsonSerializer.SerializeToElement(target);
    }

    private static EngineException Required(string name)
        => new(EngineErrors.Of(EngineErrors.RequiredParam,
            $"required parameter '{name}' is missing",
            details: JsonSerializer.SerializeToElement(new { @param = name })));
}
