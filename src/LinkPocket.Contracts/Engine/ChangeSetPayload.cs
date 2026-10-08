using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkPocket.Contracts;

/// <summary>
/// ChangeSet 的载荷投影（**唯一实现**）：wire <c>changes</c>、审计 <c>changes_json</c>、事件负载
/// 与进程内消费者（CLI / 外部 Agent 网关）四处共用同一形状——一处改、四处逐字节一致
/// （见 ENGINE-API §1「changes 载荷形状」）。
///
/// <para>单命令 diff 上限 2000 条：超出时裁剪为前 2000 条并带**自描述**截断标记
/// <c>diff_truncated</c> / <c>diff_omitted</c>（不新增列、不改 schema）。</para>
///
/// <para><c>before</c>/<c>after</c> 键：<c>null</c> = 字段值为空；**键缺失** = 该时刻字段不适用
/// （创建前 / 删除后，契约里是 C# <c>null</c>）。</para>
///
/// <para><b>归属（契约层）</b>：调用方分布在 Engine（wire / 审计 / 批）与消费者侧（CLI / 外部 Agent 网关），
/// 而消费者只依赖契约层——与 <see cref="LogRedactor"/> 同一种情形（唯一实现放在两侧都到得了的地方）。</para>
/// </summary>
public static class ChangeSetPayload
{
    /// <summary>单命令 diff 载荷上限（超出裁剪 + 截断标记；内存态与命令结果不受此限）。</summary>
    public const int MaxDiffEntries = 2000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>ChangeSet → 载荷 JSON（空字段缺省不出现：warnings / diff / 截断标记）。</summary>
    public static JsonElement From(ChangeSet set)
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

    /// <summary>
    /// 载荷 JSON → 读侧视图（审计 <c>changes_json</c> 的解析点）：**与 <see cref="From"/> 共用同一份形状定义**
    /// （同一个 <see cref="Options"/> 与 <c>Payload</c> 形状），因此写读两侧永不漂移。
    ///
    /// <para>解析失败（空串 / 非法 JSON / 早期版本的老数据）一律返回 <c>null</c>——
    /// 调用方按"拿不到精确信息"处理（跨进程变更流退化为整体失效），**绝不猜**。</para>
    /// </summary>
    public static ChangeSetView? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<ReadPayload>(json, Options);
            return payload is null
                ? null
                : new ChangeSetView(payload.Touched ?? [], payload.Events ?? [], payload.HumanSummary);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>读侧视图（<c>changes_json</c> 里跨进程变更流真正用到的三段）。</summary>
    public sealed record ChangeSetView(
        IReadOnlyList<EntityRef> Touched,
        IReadOnlyList<string> Events,
        string? HumanSummary);

    /// <summary>读侧 DTO：只声明用得上的字段（diff / warnings 等由序列化器按未匹配属性跳过）。</summary>
    private sealed record ReadPayload(
        IReadOnlyList<EntityRef>? Touched,
        IReadOnlyList<string>? Events,
        string? HumanSummary);

    private sealed record Payload(
        IReadOnlyList<EntityRef> Touched,
        IReadOnlyList<string> Events,
        string? HumanSummary,
        IReadOnlyList<string>? Warnings,
        IReadOnlyList<FieldChange>? Diff,
        bool? DiffTruncated,
        int? DiffOmitted);
}
