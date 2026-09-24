using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 用量汇总存储（**唯一实现**）：<c>{数据根}/usage.json</c>——按 **天 × 模型 × 用途** 聚合的计数与 token 合计。
/// 口径（功能书 §9.1）：原子写；损坏 / 版本不符 → <c>LP.AI.015</c> 如实暴露、**拒绝覆盖**；
/// 保留 365 天（写入时按天裁剪，裁剪数写日志——汇总不是历史存档）。
/// </summary>
public sealed class AiUsageStore
{
    public const int CurrentVersion = 1;
    public const int RetentionDays = 365;

    private sealed record Entry(string Day, string ProviderId, string ModelId, AiUsagePurpose Purpose,
        int Calls, long InputTokens, long OutputTokens);

    private sealed record FileModel(int Version, List<Entry> Entries);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _gate = new();

    public AiUsageStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _path = Path.Combine(dataRoot, "usage.json");
    }

    public string FilePath => _path;

    /// <summary>记一笔用量（按本地日归桶；写入时顺带裁剪过期天）。</summary>
    public void Record(DateTimeOffset at, string providerId, string modelId, AiUsagePurpose purpose,
        int inputTokens, int outputTokens)
    {
        lock (_gate)
        {
            var model = LoadModel();
            var day = at.ToLocalTime().ToString("yyyy-MM-dd");
            var index = model.Entries.FindIndex(e => e.Day == day && e.ProviderId == providerId
                && e.ModelId == modelId && e.Purpose == purpose);
            if (index >= 0)
            {
                var entry = model.Entries[index];
                model.Entries[index] = entry with
                {
                    Calls = entry.Calls + 1,
                    InputTokens = entry.InputTokens + Math.Max(0, inputTokens),
                    OutputTokens = entry.OutputTokens + Math.Max(0, outputTokens),
                };
            }
            else
            {
                model.Entries.Add(new Entry(day, providerId, modelId, purpose, 1,
                    Math.Max(0, inputTokens), Math.Max(0, outputTokens)));
            }

            var cutoff = at.ToLocalTime().Date.AddDays(-RetentionDays);
            var removed = model.Entries.RemoveAll(e =>
                DateTime.TryParse(e.Day, out var parsed) && parsed < cutoff);
            if (removed > 0)
                LpLog.Debug($"usage rollup pruned {removed} day/model row(s) beyond {RetentionDays} days",
                    category: "ai.usage");
            Save(model);
        }
    }

    /// <summary>近 N 天汇总（本地日界：今天 = 今日 00:00；含今天在内 N 个自然日）。</summary>
    public AiUsageSummary Summary(int days, DateTimeOffset? now = null)
    {
        days = Math.Clamp(days, 1, RetentionDays);
        lock (_gate)
        {
            var model = LoadModel();
            var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
            var from = today.AddDays(-(days - 1));
            var buckets = new Dictionary<DateTime, (int Calls, long Input, long Output)>();
            foreach (var entry in model.Entries)
            {
                if (!DateTime.TryParse(entry.Day, out var day)) continue;
                if (day.Date < from || day.Date > today) continue;
                var current = buckets.GetValueOrDefault(day.Date);
                buckets[day.Date] = (current.Calls + entry.Calls,
                    current.Input + entry.InputTokens, current.Output + entry.OutputTokens);
            }

            var items = new List<AiUsageDay>();
            for (var day = from; day <= today; day = day.AddDays(1))
            {
                var current = buckets.GetValueOrDefault(day);
                items.Add(new AiUsageDay(new DateTimeOffset(day), current.Calls, current.Input, current.Output));
            }
            return new AiUsageSummary(days, items,
                items.Sum(i => (long)i.Calls), items.Sum(i => i.InputTokens), items.Sum(i => i.OutputTokens));
        }
    }

    private FileModel LoadModel()
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
            throw Corrupt("usage rollup is not valid JSON");
        }
        if (model is null || model.Version != CurrentVersion || model.Entries is null)
            throw Corrupt($"usage rollup version/format mismatch (expected version {CurrentVersion})");
        return model;
    }

    private void Save(FileModel model)
    {
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(model, JsonOptions));
    }

    private static AiException Corrupt(string reason)
        => new(AiErrors.Of(AiErrors.AiDataStoreFailed,
            $"{reason}; refusing to overwrite the existing file (delete it manually to reset)"));
}
