using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Engine;

/// <summary>命令上下文实现：顶层调用（引擎构造）与嵌套调用（父 UoW 复用）共用。</summary>
internal sealed class CommandContextImpl : ICommandContext
{
    private readonly EngineCore _engine;
    private readonly List<string> _nestedEvents = [];
    private readonly List<EntityRef> _nestedTouched = [];
    private readonly List<string> _nestedWarnings = [];

    public CommandContextImpl(
        IUnitOfWork uow,
        bool isNested,
        bool dryRun,
        string correlationId,
        CallerRef caller,
        CancellationToken ct,
        EngineCore engine)
    {
        Uow = uow;
        IsNested = isNested;
        DryRun = dryRun;
        CorrelationId = correlationId;
        Caller = caller;
        Ct = ct;
        _engine = engine;
    }

    public IUnitOfWork Uow { get; }
    public bool IsNested { get; }
    public bool DryRun { get; }
    public CancellationToken Ct { get; }
    public string CorrelationId { get; }
    public CallerRef Caller { get; }

    /// <summary>所属引擎（同程序集编排组件复用嵌套派发入口）。</summary>
    internal EngineCore Engine => _engine;

    public Task<CommandResult> DispatchNestedAsync(string command, object? args = null, CancellationToken ct = default)
        => _engine.ExecuteNestedAsync(this, command, args, ct);

    /// <summary>累积嵌套子命令的变更集（父提交成功后随父事件一并发布并失效缓存；父审计条目下附带子记录）。
    /// <b>设计（报告 4.5）</b>：嵌套步骤的 HumanSummary 不向上合并——人话摘要只由顶层命令产出，
    /// 嵌套子步骤只贡献「受影响实体 + 事件名」，避免多段摘要拼接带来文案割裂。</summary>
    public void CollectNestedChange(ChangeSet? changes)
    {
        if (changes is null) return;
        _nestedEvents.AddRange(changes.Events);
        _nestedTouched.AddRange(changes.Touched);
        if (changes.Warnings is { Count: > 0 }) _nestedWarnings.AddRange(changes.Warnings);
    }

    /// <summary>取走嵌套变更集（事件名去重、受影响实体去重）：父级只发布/失效一次。</summary>
    public ChangeSet TakeNestedChanges()
    {
        var merged = new ChangeSet(
            _nestedTouched.DistinctBy(r => (r.Type, r.Id)).ToArray(),
            _nestedEvents.Distinct(StringComparer.Ordinal).ToArray(),
            null,
            _nestedWarnings.Count > 0 ? _nestedWarnings.Distinct(StringComparer.Ordinal).ToArray() : null);
        _nestedEvents.Clear();
        _nestedTouched.Clear();
        _nestedWarnings.Clear();
        return merged;
    }
}

/// <summary>引擎入参序列化约定：snake_case 命名 + 大小写不敏感读取（方案 3.3 标准参数口径）。</summary>
public static class EngineJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>批脚本/宏脚本解析口径：在 Options 之上放开枚举字符串（scope/on_error 可写 "transactional"/"continue"）。</summary>
    public static readonly JsonSerializerOptions ScriptOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static JsonElement ToJsonElement(object? args)
        => args switch
        {
            null => EmptyObject,
            // wire 直路由（方法名 = 命令名）缺省传 default(JsonElement)（Undefined）；JSON null 同理。
            // 统一归一为 {} —— 下游 Handler 一律按"对象形态"读参数，加速器与观测面不得成为故障源。
            // 契约（报告 3.6）：对传入的 JsonElement 直接返回原引用（不 Clone）——调用方须保证其底层
            // JsonDocument 的生命周期足够长；wire 层已先行 Clone，普通 new{...} 走下方 SerializeToElement。
            JsonElement e => e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? EmptyObject : e,
            _ => JsonSerializer.SerializeToElement(args, Options),
        };

    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");
}
