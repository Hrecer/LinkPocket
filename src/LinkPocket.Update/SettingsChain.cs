using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LinkPocket.Contracts;

namespace LinkPocket.Update;

/// <summary>
/// 设置转换步骤链（UPDATE.md §5.1）：空链直通；有步则取"`from ≥ 当前版本` 且 `to ≤ 目标版本`"的步按升序逐层执行。
/// **加载时全量校验**（坏步 = 整包拒绝，绝不半途）；转换前对触及文件做小快照，失败恢复快照；
/// 收尾按"设置版本清单"对账——版本对不上目标的一律丢弃（设置可重置，不阻塞更新）。
/// </summary>
public static class SettingsChain
{
    public static void Run(string dataRoot, string packageDir, string stagingDir,
        string? fromVersion, string? toVersion, UpdateReport report, Action<string> emit)
    {
        var steps = LoadSteps(PackageLayout.StepsDir(packageDir));
        var applicable = Resolve(steps, fromVersion, toVersion);
        string? snapshotDir = null;
        try
        {
            if (applicable.Count > 0 && Directory.Exists(dataRoot))
            {
                snapshotDir = SnapshotTouched(dataRoot, stagingDir, applicable);
                foreach (var step in applicable) ApplyStep(dataRoot, step, report, emit);
            }

            EnforceManifest(dataRoot, packageDir, report, emit);
        }
        catch
        {
            if (snapshotDir != null) RestoreSnapshot(snapshotDir, dataRoot, emit);
            throw;
        }
    }

    // —— 加载与校验（动手前全量解析：坏包在这里拒绝，数据一个字节都没动）——

    private static List<StepFile> LoadSteps(string stepsDir)
    {
        var result = new List<StepFile>();
        if (!Directory.Exists(stepsDir)) return result;

        foreach (var file in Directory.EnumerateFiles(stepsDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UpdateFailure($"cannot read the update step '{Path.GetFileName(file)}': {ex.Message}");
            }

            result.Add(ParseStep(Path.GetFileName(file), text));
        }

        var duplicates = result
            .GroupBy(s => (s.From, s.To))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.From}->{g.Key.To}")
            .ToList();
        if (duplicates.Count > 0)
            throw new UpdateFailure($"duplicate update steps for: {string.Join(", ", duplicates)}");

        return result;
    }

    private static StepFile ParseStep(string fileName, string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new UpdateFailure($"update step '{fileName}' is not valid JSON: {ex.Message}");
        }

        if (node is not JsonObject obj) throw new UpdateFailure($"update step '{fileName}' must be a JSON object");

        var from = ParseVersionField(obj, "from", fileName);
        var to = ParseVersionField(obj, "to", fileName);
        if (from >= to) throw new UpdateFailure($"update step '{fileName}': from ({from}) must be older than to ({to})");

        if (obj["changes"] is not JsonArray changes || changes.Count == 0)
            throw new UpdateFailure($"update step '{fileName}': 'changes' must be a non-empty array");

        var parsedChanges = new List<StepChange>();
        foreach (var item in changes)
        {
            if (item is not JsonObject change)
                throw new UpdateFailure($"update step '{fileName}': every change must be a JSON object");

            var file = ReadString(change, "file", fileName, required: true)!;
            var relative = file.Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative.Split('/').Any(segment => segment == ".."))
                throw new UpdateFailure($"update step '{fileName}': '{file}' must be a relative path inside the data directory");

            var expect = ReadOptionalInt(change, "expect_version", fileName);
            var set = ReadOptionalInt(change, "set_version", fileName);

            if (change["ops"] is not JsonArray ops || ops.Count == 0)
                throw new UpdateFailure($"update step '{fileName}': change '{file}' must declare a non-empty 'ops' array");

            var parsedOps = new List<StepOp>();
            foreach (var opNode in ops)
            {
                if (opNode is not JsonObject op)
                    throw new UpdateFailure($"update step '{fileName}': every op must be a JSON object");

                var kind = ReadString(op, "op", fileName, required: true)!;
                switch (kind)
                {
                    case "rename":
                        parsedOps.Add(new StepOp(kind,
                            ReadString(op, "from", fileName, required: true),
                            ReadString(op, "to", fileName, required: true),
                            null, null));
                        break;
                    case "remove":
                        parsedOps.Add(new StepOp(kind, null, null,
                            ReadString(op, "path", fileName, required: true), null));
                        break;
                    case "set_default":
                        var path = ReadString(op, "path", fileName, required: true);
                        if (op["value"] is not { } value)
                            throw new UpdateFailure($"update step '{fileName}': set_default is missing 'value'");
                        parsedOps.Add(new StepOp(kind, null, null, path, value));
                        break;
                    default:
                        throw new UpdateFailure($"update step '{fileName}': unknown op '{kind}' (expected rename | remove | set_default)");
                }
            }

            parsedChanges.Add(new StepChange(relative, expect, set, parsedOps));
        }

        return new StepFile(from, to, parsedChanges);
    }

    private static Version ParseVersionField(JsonObject obj, string field, string fileName)
    {
        var raw = ReadString(obj, field, fileName, required: true);
        if (!Version.TryParse(raw, out var version))
            throw new UpdateFailure($"update step '{fileName}': '{field}' must be a version like 4.0.0 (got '{raw}')");
        return version;
    }

    private static string? ReadString(JsonObject obj, string field, string fileName, bool required)
    {
        var node = obj[field];
        if (node is null)
        {
            if (required) throw new UpdateFailure($"update step '{fileName}': missing '{field}'");
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new UpdateFailure($"update step '{fileName}': '{field}' must be a string");
        }
    }

    private static int? ReadOptionalInt(JsonObject obj, string field, string fileName)
    {
        var node = obj[field];
        if (node is null) return null;
        try
        {
            return node.GetValue<int>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new UpdateFailure($"update step '{fileName}': '{field}' must be an integer");
        }
    }

    /// <summary>链解析：`from ≥ 当前版本` 且 `to ≤ 目标版本`，按版本升序；无文件的版本对直通。有步但版本读不出 = 拒绝（不猜）。</summary>
    private static List<StepFile> Resolve(List<StepFile> steps, string? fromVersion, string? toVersion)
    {
        if (steps.Count == 0) return [];

        if (!Version.TryParse(fromVersion, out var from) || !Version.TryParse(toVersion, out var to))
            throw new UpdateFailure(
                "the package contains conversion steps, but the product version could not be determined "
                + $"(from '{fromVersion ?? "?"}' to '{toVersion ?? "?"}') - refusing to guess");
        if (from > to) throw new UpdateFailure($"downgrading from {from} to {to} is not supported");

        return steps
            .Where(s => s.From >= from && s.To <= to)
            .OrderBy(s => s.From)
            .ThenBy(s => s.To)
            .ToList();
    }

    // —— 应用（逐文件：读 → 校验版本 → 应用 ops → 写回；不可读 / 版本不符 = 丢弃 + 列单）——

    private static void ApplyStep(string dataRoot, StepFile step, UpdateReport report, Action<string> emit)
    {
        emit($"applying settings step {step.From} -> {step.To} ({step.Changes.Count} file change(s))");
        foreach (var change in step.Changes)
        {
            var path = Path.Combine(dataRoot, change.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                Note(report, emit, $"step {step.From}->{step.To}: '{change.File}' is not present; skipped");
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                DropFile(path, $"'{change.File}' is not valid JSON ({ex.Message})", report, emit);
                continue;
            }

            if (node is not JsonObject obj)
            {
                DropFile(path, $"'{change.File}' is not a JSON object", report, emit);
                continue;
            }

            var current = ReadVersion(obj);
            if (change.ExpectVersion is int expected && current != expected)
            {
                DropFile(path, $"'{change.File}' is at version {current?.ToString() ?? "?"} but the step expects {expected}", report, emit);
                continue;
            }

            foreach (var op in change.Ops) ApplyOp(obj, change.File, op, report, emit);
            if (change.SetVersion is int setVersion) WriteVersion(obj, setVersion);
            AtomicFile.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void ApplyOp(JsonObject obj, string file, StepOp op, UpdateReport report, Action<string> emit)
    {
        switch (op.Op)
        {
            case "rename":
            {
                var sourceSegments = op.From!.Split('.');
                var sourceParent = ResolveParent(obj, op.From!);
                if (sourceParent is null || !sourceParent.TryGetPropertyValue(sourceSegments[^1], out var value) || value is null)
                {
                    Note(report, emit, $"'{file}': rename source '{op.From}' not found; skipped");
                    return;
                }

                if (Resolve(obj, op.To!) is not null)
                {
                    Note(report, emit, $"'{file}': rename target '{op.To}' already exists; skipped");
                    return;
                }

                var targetParent = ResolveParent(obj, op.To!);
                if (targetParent is null)
                {
                    Note(report, emit, $"'{file}': rename target parent of '{op.To}' not found; skipped");
                    return;
                }

                sourceParent.Remove(sourceSegments[^1]);
                targetParent[op.To!.Split('.')[^1]] = value;
                return;
            }
            case "remove":
            {
                var segments = op.Path!.Split('.');
                var parent = ResolveParent(obj, op.Path!);
                if (parent is null || !parent.TryGetPropertyValue(segments[^1], out _))
                {
                    Note(report, emit, $"'{file}': remove path '{op.Path}' not found; skipped");
                    return;
                }

                parent.Remove(segments[^1]);
                return;
            }
            case "set_default":
            {
                if (Resolve(obj, op.Path!) is not null) return;   // 已有值 = 不覆盖
                var parent = ResolveParent(obj, op.Path!);
                if (parent is null)
                {
                    Note(report, emit, $"'{file}': set_default parent of '{op.Path}' not found; skipped");
                    return;
                }

                parent[op.Path!.Split('.')[^1]] = JsonNode.Parse(op.Value!.ToJsonString());
                return;
            }
        }
    }

    // —— 收尾对账：设置版本清单（缺席 = 无版本政策）——

    private static void EnforceManifest(string dataRoot, string packageDir, UpdateReport report, Action<string> emit)
    {
        var manifestPath = PackageLayout.ManifestPath(packageDir);
        if (!File.Exists(manifestPath) || !Directory.Exists(dataRoot)) return;

        ManifestFile manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(manifestPath), ManifestJson)
                ?? throw new JsonException("the file is empty");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateFailure($"the settings manifest is malformed: {ex.Message}");
        }

        if (manifest.Version != 1)
            throw new UpdateFailure($"unsupported settings manifest version {manifest.Version} (this updater supports 1)");
        if (manifest.Stores is null)
            throw new UpdateFailure("the settings manifest has no 'stores' array");

        foreach (var store in manifest.Stores)
        {
            if (string.IsNullOrWhiteSpace(store.File))
                throw new UpdateFailure("the settings manifest has a store entry without 'file'");

            var path = Path.Combine(dataRoot, store.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;

            int? current;
            try
            {
                current = JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj ? ReadVersion(obj) : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                DropFile(path, $"'{store.File}' is not readable JSON ({ex.Message})", report, emit);
                continue;
            }

            if (current is null)
            {
                Note(report, emit, $"'{store.File}' has no version field; carried as-is");
                continue;
            }

            if (current.Value != store.Version)
                DropFile(path, $"'{store.File}' is at version {current} but this version expects {store.Version}", report, emit);
        }
    }

    // —— 快照（转换前）与恢复 ——

    private static string SnapshotTouched(string dataRoot, string stagingDir, List<StepFile> steps)
    {
        var snapshotDir = Path.Combine(stagingDir, "settings-before");
        if (Directory.Exists(snapshotDir)) Directory.Delete(snapshotDir, recursive: true);

        foreach (var relative in steps
                     .SelectMany(s => s.Changes)
                     .Select(c => c.File.Replace('/', Path.DirectorySeparatorChar))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var source = Path.Combine(dataRoot, relative);
            if (!File.Exists(source)) continue;
            var destination = Path.Combine(snapshotDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }

        return snapshotDir;
    }

    private static void RestoreSnapshot(string snapshotDir, string dataRoot, Action<string> emit)
    {
        emit("conversion failed: restoring settings from the pre-conversion snapshot");
        foreach (var file in Directory.EnumerateFiles(snapshotDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(snapshotDir, file);
            var destination = Path.Combine(dataRoot, relative);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                emit($"WARNING: snapshot restore failed for {relative}: {ex.Message}");
            }
        }
    }

    // —— JSON 小工具（版本字段读取大小写不敏感；点分路径解析对象；都只在对象层，不穿数组）——

    private static int? ReadVersion(JsonObject obj)
    {
        foreach (var (key, value) in obj)
        {
            if (!key.Equals("version", StringComparison.OrdinalIgnoreCase)) continue;
            if (value is not JsonValue jsonValue) return null;
            if (jsonValue.TryGetValue<int>(out var number)) return number;
            if (jsonValue.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed)) return parsed;
            return null;
        }

        return null;
    }

    private static void WriteVersion(JsonObject obj, int version)
    {
        string? key = null;
        foreach (var candidate in obj.Select(pair => pair.Key))
        {
            if (candidate.Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                key = candidate;
                break;
            }
        }

        obj[key ?? "version"] = JsonValue.Create(version);
    }

    private static JsonNode? Resolve(JsonObject root, string dottedPath)
    {
        JsonNode? current = root;
        foreach (var segment in dottedPath.Split('.'))
        {
            if (current is not JsonObject obj) return null;
            current = obj.TryGetPropertyValue(segment, out var child) ? child : null;
            if (current is null) return null;
        }

        return current;
    }

    private static JsonObject? ResolveParent(JsonObject root, string dottedPath)
    {
        var segments = dottedPath.Split('.');
        JsonNode? current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segments[i], out var child) || child is null)
                return null;
            current = child;
        }

        return current as JsonObject;
    }

    private static void Note(UpdateReport report, Action<string> emit, string text)
    {
        report.Notes.Add(text);
        emit("note: " + text);
    }

    private static void DropFile(string path, string reason, UpdateReport report, Action<string> emit)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            Note(report, emit, $"dropped {reason} (settings can be reconfigured)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Note(report, emit, $"could not drop {reason}: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNameCaseInsensitive = true };

    private sealed class ManifestFile
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("stores")] public List<ManifestStore>? Stores { get; set; }
    }

    private sealed class ManifestStore
    {
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("version")] public int Version { get; set; }
    }
}

internal sealed record StepFile(Version From, Version To, IReadOnlyList<StepChange> Changes);

internal sealed record StepChange(string File, int? ExpectVersion, int? SetVersion, IReadOnlyList<StepOp> Ops);

internal sealed record StepOp(string Op, string? From, string? To, string? Path, JsonNode? Value);
