using System.Text.Json;
using System.Text.Json.Serialization;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// ChangeSet 的载荷投影（**唯一实现**）：wire <c>changes</c>、审计 <c>changes_json</c> 与事件负载三处共用同一形状——
/// 一处改、三处逐字节一致（见 ENGINE-API §1「changes 载荷形状」）。
///
/// <para>单命令 diff 上限 2000 条：超出时裁剪为前 2000 条并带**自描述**截断标记
/// <c>diff_truncated</c> / <c>diff_omitted</c>（不新增列、不改 schema）。</para>
///
/// <para><c>before</c>/<c>after</c> 键：<c>null</c> = 字段值为空；**键缺失** = 该时刻字段不适用
/// （创建前 / 删除后，契约里是 C# <c>null</c>）。</para>
/// </summary>
internal static class ChangeSetPayload
{
    /// <summary>单命令 diff 载荷上限（超出裁剪 + 截断标记；内存态与命令结果不受此限）。</summary>
    internal const int MaxDiffEntries = 2000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>ChangeSet → 载荷 JSON（空字段缺省不出现：warnings / diff / 截断标记）。</summary>
    internal static JsonElement From(ChangeSet set)
    {
        var diff = set.Diff;
        var truncated = diff is { Count: > MaxDiffEntries };
        var payload = new Payload(
            set.Touched,
            set.Events,
            set.HumanSummary,
            set.Warnings is { Count: > 0 } ? set.Warnings : null,
            diff is { Count: > 0 } ? (truncated ? diff.Take(MaxDiffEntries).ToArray() : diff) : null,
            truncated ? true : null,
            truncated ? diff!.Count - MaxDiffEntries : null);
        return JsonSerializer.SerializeToElement(payload, Options);
    }

    private sealed record Payload(
        IReadOnlyList<EntityRef> Touched,
        IReadOnlyList<string> Events,
        string? HumanSummary,
        IReadOnlyList<string>? Warnings,
        IReadOnlyList<FieldChange>? Diff,
        bool? DiffTruncated,
        int? DiffOmitted);
}
