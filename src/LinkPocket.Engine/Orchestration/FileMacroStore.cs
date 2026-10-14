using System.Text.Json;
using System.Text.Json.Serialization;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 命名批处理（宏）持久化（<see cref="IMacroStore"/> 的唯一实现）：**文件存储**
/// <c>&lt;库目录&gt;/macros.json</c>（4.0.0 起宏**不在数据库里**——它是"手动保存的脚本"，
/// 与库无事务关系；随 <c>data\</c> 整体搬移，格式演进与设置文件同族口径）。
///
/// <para><b>原子写</b>：经 <see cref="AtomicFile"/>（先写 tmp 再原子替换），崩溃不留半截文件。</para>
///
/// <para><b>损坏 / 版本不符 → 明确暴露、绝不覆盖</b>（观测面纪律）：文件读不了、JSON 坏了、
/// 版本不认识 → 抛明确错误，文件原样留存，处置权归用户。</para>
///
/// <para><b>已知边界</b>：同一文件的读-改-写不是跨进程原子的（应用与 CLI 极端并发保存时可能丢一次更新）——
/// 宏量少、写入是显式动作，接受此边界；文件本身不会损坏（原子替换），最坏只是一次覆盖。</para>
/// </summary>
public sealed class FileMacroStore : IMacroStore
{
    /// <summary>宏文件名（与库同目录，固定名——唯一推导点是 <see cref="PathFor"/>）。</summary>
    public const string FileName = "macros.json";

    /// <summary>文件格式版本（与设置文件同族：结构演进时抬版本号，转换归更新流程）。</summary>
    private const int CurrentVersion = 1;

    private readonly string _path;

    public FileMacroStore(string filePath) => _path = Path.GetFullPath(filePath);

    /// <summary>
    /// 库路径 → 宏文件路径（唯一推导点）。**缺省库（`linkpocket.db`）= 同目录标准名 `macros.json`**（data\ 的对外口径）；
    /// 别名的库（`--db` 指到别处 / 测试临时库）= 名字随库派生 `<库名>.macros.json`——同一目录里的多个库天然隔离
    /// （与附属库 `<库名>-ops.db` 的名字派生同一纪律）。
    /// </summary>
    public static string PathFor(string dbPath)
    {
        var full = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(full) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(full);
        return stem == "linkpocket"
            ? Path.Combine(dir, FileName)
            : Path.Combine(dir, stem + "." + FileName);
    }

    private sealed class MacroFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
        [JsonPropertyName("macros")] public List<MacroEntry> Macros { get; set; } = [];
    }

    private sealed class MacroEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("script_json")] public string ScriptJson { get; set; } = "";
        [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
        [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task SaveAsync(string name, string scriptJson, CancellationToken ct)
    {
        // 保存前校验脚本可解析（无效脚本禁止入库——宏是技能库，坏脚本比缺脚本更危险）
        try
        {
            _ = JsonSerializer.Deserialize<BatchScript>(scriptJson, EngineJson.ScriptOptions)
                ?? throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed, $"the script of macro '{name}' is not a valid batch script"));
        }
        catch (JsonException)
        {
            // 坏 JSON 是输入问题而非内部错误：报 ProtocolMalformed，不冒泡成 LP.INTERNAL
            throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed, $"the script of macro '{name}' is not a valid batch script"));
        }

        var file = Read();
        var now = DateTimeOffset.Now;
        var existing = file.Macros.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
        if (existing is null)
            file.Macros.Add(new MacroEntry { Name = name, ScriptJson = scriptJson, CreatedAt = now, UpdatedAt = now });
        else
        {
            existing.ScriptJson = scriptJson;
            existing.UpdatedAt = now;
        }
        Write(file);
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string name, CancellationToken ct)
        => Task.FromResult(Read().Macros
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal))?.ScriptJson);

    public Task<IReadOnlyList<(string Name, string ScriptJson, DateTimeOffset UpdatedAt)>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<(string, string, DateTimeOffset)>>(Read().Macros
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .Select(m => (m.Name, m.ScriptJson, m.UpdatedAt))
            .ToList());

    public Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        var file = Read();
        var removed = file.Macros.RemoveAll(m => string.Equals(m.Name, name, StringComparison.Ordinal)) > 0;
        if (removed) Write(file);
        return Task.FromResult(removed);
    }

    private MacroFile Read()
    {
        if (!File.Exists(_path)) return new MacroFile();

        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Corrupt($"macros file is unreadable: {ex.Message}");
        }

        MacroFile? file;
        try
        {
            file = JsonSerializer.Deserialize<MacroFile>(text, Json);
        }
        catch (JsonException)
        {
            throw Corrupt("macros file is not valid JSON");
        }
        if (file is null) return new MacroFile();
        if (file.Version != CurrentVersion)
            throw Corrupt($"unsupported macros file version {file.Version} (this app supports {CurrentVersion}); the file is left untouched");
        return file;
    }

    private void Write(MacroFile file) => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, Json));

    private static EngineException Corrupt(string message)
        => new(EngineErrors.Of(EngineErrors.DbError, "macros file problem: " + message));
}
