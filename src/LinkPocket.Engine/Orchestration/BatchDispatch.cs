using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 批三命令（<c>batch.run</c> / <c>batch.dry_run</c> / <c>batch.status</c>）的**直路由唯一实现**：
/// 批引擎自身即管道父调用（写闸 / 嵌套派发 / 父审计 / 撤销登记都在批内），三命令**不进命令注册表、
/// 不走标准命令管道**。两个按命令名的入口——wire 的直接方法名与进程内 <c>IEngine</c>（EngineClient / AI）——
/// 汇到同一处（本类），杜绝"wire 能跑、进程内报 UNKNOWN_COMMAND"的双口径。
/// </summary>
internal static class BatchDispatch
{
    public static bool IsBatchCommand(string command)
        => command is "batch.run" or "batch.dry_run" or "batch.status";

    /// <summary>写流入口：批结果包成 <see cref="CommandResult{T}"/>（Changes = 批报告的总变更集）。</summary>
    public static async Task<CommandResult<T>> ExecuteAsync<T>(IBatchEngine batch, string command,
        object? args, CallOptions? options, CancellationToken ct)
    {
        var report = await RunAsync(batch, command, EngineJson.ToJsonElement(args), options, ct).ConfigureAwait(false);
        return new CommandResult<T>(true, Adapt<T>(report), report is BatchReport r ? r.Changes : null, null);
    }

    /// <summary>读流入口：<c>batch.dry_run</c> / <c>batch.status</c>（Query 形态）返回数据本体。</summary>
    public static async Task<T> QueryAsync<T>(IBatchEngine batch, string command,
        object? args, CallOptions? options, CancellationToken ct)
    {
        var report = await RunAsync(batch, command, EngineJson.ToJsonElement(args), options, ct).ConfigureAwait(false);
        return Adapt<T>(report);
    }

    private static async Task<object?> RunAsync(IBatchEngine batch, string command, JsonElement args,
        CallOptions? options, CancellationToken ct)
        => command switch
        {
            "batch.run" => await batch.RunAsync(ParseScript(Element(args, "script")), options, ct).ConfigureAwait(false),
            "batch.dry_run" => await batch.DryRunAsync(ParseScript(Element(args, "script")), ct).ConfigureAwait(false),
            _ => Status(batch, args),
        };

    /// <summary>状态读面：给了 <c>batch_id</c> = 精确读（不存在如实报 <c>ENTITY_NOT_FOUND</c>）；
    /// 省略 = 读**在飞批**（空闲 → null，不是错误——"现在没有批在跑"是合法答案）。</summary>
    private static BatchStatus? Status(IBatchEngine batch, JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("batch_id", out var value)
            && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            return batch.GetStatus(value.GetString()!)
                ?? throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound,
                    $"batch not found: {value.GetString()}"));
        return batch.CurrentStatus;
    }

    /// <summary>批脚本解析（snake_case + 枚举字符串；wire 与引擎入口共用）。</summary>
    public static BatchScript ParseScript(JsonElement scriptEl)
        => JsonSerializer.Deserialize<BatchScript>(scriptEl.GetRawText(), EngineJson.ScriptOptions)
           ?? throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed,
               "batch script is not valid BatchScript JSON"));

    /// <summary>必填 JSON 元素参数（复杂入参，如批脚本）；缺失即抛 <c>REQUIRED_PARAM</c>。</summary>
    private static JsonElement Element(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
           && value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? value
            : throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
                $"batch command needs '{name}'", details: JsonSerializer.SerializeToElement(new { @param = name })));

    /// <summary>结果适配：<c>object</c> 直接承接；<c>JsonElement</c> 走 snake_case 序列化；其余按类型还原。
    /// <c>null</c>（batch.status 空闲）→ JsonElement 给 <c>ValueKind.Null</c>，其余类型给 default。</summary>
    private static T Adapt<T>(object? report)
    {
        if (report is T typed) return typed;
        if (report is null)
            return typeof(T) == typeof(JsonElement)
                ? (T)(object)JsonSerializer.SerializeToElement<object?>(null, EngineJson.Options)
                : default!;
        var element = JsonSerializer.SerializeToElement(report, EngineJson.Options);
        return typeof(T) == typeof(JsonElement)
            ? (T)(object)element
            : element.Deserialize<T>(EngineJson.Options)!;
    }
}
