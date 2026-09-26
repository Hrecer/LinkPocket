using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 审批卡的**影响面**构造（功能书 §8.2：审批卡必含「做什么 / 动哪些对象（名称 + 数量 + 路径）/ 影响预览 /
/// 将记住的作用域」，批脚本还要**逐步骤**可见）。
///
/// <para><b>口径</b>：影响面取自**入参与引擎**，不是模型自述——
/// ① 名称类参数（name/title/url/路径）是用户数据，直接用；
/// ② ID 类参数逐个经 <c>locate.resolve</c> 换名称与 canonical 路径，**有上限**（不为一张审批卡打 N 次查询），
/// 解析不到就只报数量，绝不编造；
/// ③ <c>{ref…}</c> 模板是**占位符不是值**——既不当名称也不当数量，如实报「未指明」。</para>
/// </summary>
internal static class AiApprovalBrief
{
    /// <summary>一张审批卡最多把多少个 ID 换成名称 / 路径（超出只报数量，见 <see cref="AiApproval.TargetMore"/>）。</summary>
    public const int TargetResolveLimit = 5;

    /// <summary>名称类参数：用户数据本身（名称 / 标题 / URL）。**只在没有身份 ID 时才算对象**——
    /// <c>links.update { id, title }</c> 里的 <c>title</c> 是"要改成的新值"，不是"动谁"。</summary>
    private static readonly string[] NameKeys = ["name", "title", "url"];

    /// <summary>路径类参数：文件命令的作用对象（用户给的路径，同样是用户数据）。</summary>
    private static readonly string[] LocationKeys = ["file_path", "output_path", "source_path"];

    /// <summary>对象**身份** ID（单值，按此顺序取第一个）：
    /// 只有"主对象"那一枚——落点参数（<c>list_id</c> / <c>parent_id</c> / <c>target_*</c>）是"放哪"，不是"动谁"，
    /// 拿它当对象会把审批卡写成"对象 = 目标目录"。</summary>
    private static readonly string[] IdKeys = ["id", "folder_id", "unit_id", "staging_id"];

    /// <summary>ID 类参数（集合）：批量命令的对象集合。</summary>
    private static readonly string[] IdArrayKeys = ["link_ids", "folder_ids", "item_ids", "ids"];

    /// <summary>一次解析结果：<see cref="Names"/> = 已知名称（字面或已解析）、<see cref="Count"/> = 对象总数
    /// （0 = 未指明）、<see cref="More"/> = 没列出来的数量、<see cref="Path"/> = 单一目标的 canonical 路径、
    /// <see cref="Ids"/> = 待解析的 ID（<see cref="ResolveAsync"/> 消费）。</summary>
    public sealed record Targets(
        IReadOnlyList<string> Names, int Count, int More, string? Path, IReadOnlyList<string> Ids);

    private static readonly Targets Empty = new([], 0, 0, null, []);

    /// <summary>纯解析：从入参里认出"这次调用动的是哪些对象"（不做任何引擎查询）。
    /// 判定顺序 = 身份 ID → 名称 → 路径 → 对象集合（身份压过字段值，字段值压过落点）。</summary>
    public static Targets Parse(JsonElement? args)
    {
        if (args is not { ValueKind: JsonValueKind.Object } obj) return Empty;

        foreach (var key in IdKeys)
            if (TryScalar(obj, key, out var id))
                return new Targets([], 1, 0, null, [id]);

        foreach (var key in NameKeys)
            if (TryScalar(obj, key, out var name))
                return new([name], 1, 0, null, []);

        foreach (var key in LocationKeys)
            if (TryScalar(obj, key, out var path))
                return new([path], 1, 0, null, []);

        foreach (var key in IdArrayKeys)
        {
            if (!obj.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
                continue;
            var ids = new List<string>();
            var templated = false;
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String) { templated = true; break; }
                var value = element.GetString() ?? "";
                if (IsTemplate(value)) { templated = true; break; }
                ids.Add(value);
            }
            // 数组里混了模板 = 属性值还没定（展开后才知道有几个）→ 数量不可信，如实报"未指明"
            return templated ? Empty : new Targets([], ids.Count, 0, null, ids);
        }

        return Empty;
    }

    /// <summary>把 ID 换成名称与路径（上限 <see cref="TargetResolveLimit"/>；解析失败留空，不猜）。</summary>
    public static async Task<Targets> ResolveAsync(EngineClient client, Targets parsed, CallerRef caller,
        CancellationToken ct)
    {
        if (parsed.Ids.Count == 0) return parsed;

        var single = parsed.Ids.Count == 1;
        var take = Math.Min(parsed.Ids.Count, TargetResolveLimit);
        var names = new List<string>(take);
        string? path = null;
        for (var i = 0; i < take; i++)
        {
            try
            {
                var data = await client.QueryAsync<object>("locate.resolve", new { id = parsed.Ids[i] },
                        new CallOptions(Caller: caller), ct)
                    .ConfigureAwait(false);
                var element = AiAssistant.ToElement(data);
                if (element.ValueKind != JsonValueKind.Object) continue;
                if (single && path is null && element.TryGetProperty("path", out var at)
                    && at.ValueKind == JsonValueKind.String)
                    path = at.GetString();
                if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    names.Add(name.GetString() ?? "");
            }
            catch (Exception ex) when (ex is EngineException or JsonException)
            {
                // 解析不到（快照 ID / 暂存 ID / 已消失的实体）= 名称留空，只报数量——不阻断审批
                LpLog.Debug($"approval target {parsed.Ids[i]} did not resolve (count-only)", category: "ai.permission");
            }
        }

        return new Targets(names, parsed.Ids.Count, Math.Max(0, parsed.Ids.Count - names.Count), path, []);
    }

    /// <summary>批 / 宏脚本的逐步骤影响（脚本不可读 / 没有 steps → null，界面如实说明"只按命令审批"）。</summary>
    public static IReadOnlyList<AiApprovalStep>? ParseSteps(JsonElement? script, AiToolCatalog tools)
    {
        if (script is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<AiApprovalStep>();
        var index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            index++;
            if (step.ValueKind != JsonValueKind.Object) continue;
            var command = step.TryGetProperty("command", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? ""
                : "";
            var hasArgs = step.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object;
            var target = Parse(hasArgs ? args : null);
            var onError = step.TryGetProperty("on_error", out var policy) && policy.ValueKind == JsonValueKind.String
                ? policy.GetString()
                : null;
            list.Add(new AiApprovalStep(index, command,
                target.Names.Count > 0 ? target.Names[0] : null,
                target.Count,
                tools.Descriptor(command)?.IsDestructive == true,
                onError));
        }
        return list;
    }

    /// <summary>
    /// 暴露集闸用的**逐步骤（命令, 入参 JSON）**：批 / 宏的每一步都要按自己的参数档位判定
    /// （例如 <c>folders.delete</c> 的 <c>cascade</c> 是"进回收站"还是"物理删除"）。
    /// 与 <see cref="ParseSteps"/> 分开：那一条产出的是**审批卡展示面**（不含参数与命令实参）。
    /// </summary>
    public static IReadOnlyList<(string Command, string ArgsJson)>? ParseGateSteps(JsonElement? script)
    {
        if (script is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<(string Command, string ArgsJson)>();
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object) continue;
            var command = step.TryGetProperty("command", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? ""
                : "";
            var argsJson = step.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object
                ? args.GetRawText()
                : "{}";
            list.Add((command, argsJson));
        }
        return list;
    }

    /// <summary>「本次会话总是允许」将记住的作用域（工具名 [+ 对象范围]，机器面；界面按键包成本地化整句）。</summary>
    public static string AllowScope(string command, Targets targets)
    {
        if (targets.Names.Count > 0)
            return targets.More > 0
                ? $"{command} · {string.Join(", ", targets.Names)} (+{targets.More})"
                : $"{command} · {string.Join(", ", targets.Names)}";
        return targets.Count > 0 ? $"{command} · {targets.Count}" : command;
    }

    /// <summary><c>{ref…}</c> 是占位符不是值：它既不是名称也不是数量（展开结果只有执行时才知道）。</summary>
    private static bool IsTemplate(string value) => value.Contains('{', StringComparison.Ordinal);

    private static bool TryScalar(JsonElement obj, string key, out string value)
    {
        value = "";
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var element)
            || element.ValueKind != JsonValueKind.String)
            return false;
        var raw = element.GetString() ?? "";
        if (raw.Length == 0 || IsTemplate(raw)) return false;
        value = raw;
        return true;
    }
}
