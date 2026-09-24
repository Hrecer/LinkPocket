using System.Text.Json;
using System.Text.RegularExpressions;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 技能库存储（**唯一实现**）：<c>{数据根}/skills.json</c>——技能 = 名称 + 说明（何时用）+ 提示模板 + 可选绑定的宏。
/// 口径：原子写；损坏 / 版本不符 → <c>LP.AI.015</c> 如实暴露、**拒绝覆盖**；名称唯一（大小写不敏感）；
/// 模板里的 `{参数}` 占位上限 <see cref="MaxParameters"/>（超出拒绝保存，不静默截断）。
/// </summary>
public sealed class AiSkillStore
{
    public const int CurrentVersion = 1;
    public const int MaxParameters = 8;
    public const int MaxPromptChars = 8_000;
    public const int MaxNameChars = 60;
    public const int MaxDescriptionChars = 400;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record Record(string SkillId, string Name, string Description, string PromptTemplate,
        string? MacroName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private sealed record FileModel(int Version, List<Record> Skills);

    private readonly string _path;
    private readonly object _gate = new();

    public AiSkillStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _path = Path.Combine(dataRoot, "skills.json");
    }

    public string FilePath => _path;

    public IReadOnlyList<AiSkill> List()
    {
        lock (_gate)
            return Load().Skills
                .OrderBy(s => s.UpdatedAt)
                .Select(ToDto)
                .ToArray();
    }

    /// <summary>读一个技能（不存在 → null；损坏如实抛）。</summary>
    public AiSkill? Get(string skillId)
    {
        lock (_gate)
            return Load().Skills.FirstOrDefault(s => s.SkillId == skillId) is { } record ? ToDto(record) : null;
    }

    /// <summary>保存（新建或按 SkillId 更新）；校验失败 → <c>LP.VAL.003</c>（不静默截断、不静默改名）。</summary>
    public AiSkill Save(AiSkillDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var name = (draft.Name ?? "").Trim();
        var description = (draft.Description ?? "").Trim();
        var template = (draft.PromptTemplate ?? "").Trim();
        var macro = string.IsNullOrWhiteSpace(draft.MacroName) ? null : draft.MacroName!.Trim();

        if (name.Length == 0) throw Invalid("name", "a skill name is required");
        if (name.Length > MaxNameChars) throw Invalid("name", $"the name exceeds {MaxNameChars} characters");
        if (description.Length > MaxDescriptionChars)
            throw Invalid("description", $"the description exceeds {MaxDescriptionChars} characters");
        if (template.Length == 0) throw Invalid("prompt_template", "a prompt template is required");
        if (template.Length > MaxPromptChars)
            throw Invalid("prompt_template", $"the template exceeds {MaxPromptChars} characters");
        var parameters = ExtractParameters(template);
        if (parameters.Count > MaxParameters)
            throw Invalid("prompt_template", $"at most {MaxParameters} placeholders are supported");

        lock (_gate)
        {
            var model = Load();
            if (model.Skills.Any(s => !string.Equals(s.SkillId, draft.SkillId, StringComparison.Ordinal)
                                      && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw Invalid("name", "a skill with this name already exists");

            var now = DateTimeOffset.UtcNow;
            var index = draft.SkillId is { Length: > 0 } id
                ? model.Skills.FindIndex(s => s.SkillId == id)
                : -1;
            Record record;
            if (index >= 0)
            {
                record = model.Skills[index] with
                {
                    Name = name,
                    Description = description,
                    PromptTemplate = template,
                    MacroName = macro,
                    UpdatedAt = now,
                };
                model.Skills[index] = record;
            }
            else
            {
                record = new Record($"k-{Guid.NewGuid():N}", name, description, template, macro, now, now);
                model.Skills.Add(record);
            }
            Save(model);
            return ToDto(record);
        }
    }

    /// <summary>删除（不存在 = 幂等成功）。</summary>
    public bool Delete(string skillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        lock (_gate)
        {
            var model = Load();
            var removed = model.Skills.RemoveAll(s => s.SkillId == skillId);
            if (removed > 0) Save(model);
            return removed > 0;
        }
    }

    /// <summary>模板里的 `{参数}` 占位名（按出现顺序去重）。</summary>
    public static IReadOnlyList<string> ExtractParameters(string template)
    {
        var seen = new List<string>();
        foreach (Match match in PlaceholderPattern.Matches(template ?? ""))
        {
            var name = match.Groups[1].Value;
            if (!seen.Contains(name, StringComparer.Ordinal)) seen.Add(name);
        }
        return seen;
    }

    /// <summary>渲染模板：填入参数（未填的占位保持字面——模型看得见，可再问）。</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string>? parameters)
        => PlaceholderPattern.Replace(template ?? "", match =>
            parameters is not null && parameters.TryGetValue(match.Groups[1].Value, out var value) && value is not null
                ? value
                : match.Value);

    private static readonly Regex PlaceholderPattern = new(@"\{([A-Za-z0-9_\u4e00-\u9fff]{1,32})\}",
        RegexOptions.Compiled);

    private static AiSkill ToDto(Record record)
        => new(record.SkillId, record.Name, record.Description, record.PromptTemplate, record.MacroName,
            record.CreatedAt, record.UpdatedAt, ExtractParameters(record.PromptTemplate));

    private FileModel Load()
    {
        var text = AtomicFile.TryReadAllText(_path);
        if (text is null) return new FileModel(CurrentVersion, []);
        FileModel? model;
        try
        {
            model = JsonSerializer.Deserialize<FileModel>(text, JsonOptions);
        }
        catch (JsonException)
        {
            throw Corrupt("skills file is not valid JSON");
        }
        if (model is null || model.Version != CurrentVersion || model.Skills is null)
            throw Corrupt($"skills file version/format mismatch (expected version {CurrentVersion})");
        return model;
    }

    private void Save(FileModel model)
        => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(model, JsonOptions));

    private static AiException Invalid(string field, string reason)
        => new(AiErrors.Of(AiErrors.InvalidInput, $"invalid skill {field}: {reason}",
            details: JsonSerializer.SerializeToElement(new { field })));

    private static AiException Corrupt(string reason)
        => new(AiErrors.Of(AiErrors.AiDataStoreFailed,
            $"{reason}; refusing to overwrite the existing file (delete it manually to reset)"));
}
