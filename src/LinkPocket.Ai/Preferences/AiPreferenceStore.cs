using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 助手偏好存储（**唯一实现**）：<c>{数据根}/preferences.json</c>。
/// 口径（功能书 §9.1）：
/// <list type="bullet">
/// <item>**加字段不升版本**（旧文件缺字段 = 出厂缺省，照常读）；</item>
/// <item>改结构才升版本，旧版本一律视为损坏；</item>
/// <item>损坏 / 取值越界一律如实暴露（<c>LP.AI.015</c>）并**拒绝覆盖**——不静默回退、不重建。</item>
/// </list>
/// </summary>
public sealed class AiPreferenceStore
{
    private sealed record FileModel(int Version, AiPreferences Preferences);

    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();

    /// <param name="dataRoot">AI 数据根（宿主传 <c>{程序目录}/ai</c>；测试传临时目录）。</param>
    public AiPreferenceStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _path = Path.Combine(dataRoot, "preferences.json");
    }

    public string FilePath => _path;

    /// <summary>读偏好（文件不存在 = 出厂缺省；损坏 / 越界如实抛 <c>LP.AI.015</c>）。</summary>
    public AiPreferences Load()
    {
        lock (_gate)
        {
            var text = AtomicFile.TryReadAllText(_path);
            if (text is null) return new AiPreferences();

            FileModel? model;
            try
            {
                model = JsonSerializer.Deserialize<FileModel>(text, JsonOptions);
            }
            catch (JsonException)
            {
                throw Corrupt("preferences are not valid JSON");
            }

            if (model is null || model.Preferences is null || model.Version != CurrentVersion)
                throw Corrupt($"preferences version/format mismatch (expected version {CurrentVersion})");

            Validate(model.Preferences);
            return model.Preferences;
        }
    }

    /// <summary>写偏好（原子替换；取值越界拒绝写入）。</summary>
    public void Save(AiPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Validate(preferences);
        lock (_gate)
        {
            Load();   // 损坏时拒绝覆盖：先过校验（读不出来就不许写）
            AtomicFile.WriteAllText(_path,
                JsonSerializer.Serialize(new FileModel(CurrentVersion, preferences), JsonOptions));
        }
    }

    /// <summary>取值范围（越界即报错——拿不准就报错，不猜意图）。</summary>
    private static void Validate(AiPreferences p)
    {
        if (!Enum.IsDefined(p.Mode)) throw OutOfRange("mode");
        if (p.MaxToolCallsPerTurn is < 1 or > 200) throw OutOfRange("max_tool_calls_per_turn");
        if (p.MaxChangesPerTurn is < 1 or > 10_000) throw OutOfRange("max_changes_per_turn");
        if (p.MaxBatchSteps is < 1 or > 5_000) throw OutOfRange("max_batch_steps");
        if (p.CallsPerMinute is < 1 or > 6_000) throw OutOfRange("calls_per_minute");
        if (p.ContextBudgetTokens is < 1_000 or > 2_000_000) throw OutOfRange("context_budget_tokens");
    }

    private static AiException OutOfRange(string field)
        => new(AiErrors.Of(AiErrors.AiDataStoreFailed, $"preference out of range: {field}",
            details: JsonSerializer.SerializeToElement(new { field })));

    private static AiException Corrupt(string reason)
        => new(AiErrors.Of(
            AiErrors.AiDataStoreFailed,
            $"{reason}; refusing to overwrite the existing file (delete it manually to reset)"));
}
