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
    private readonly List<FieldChange> _nestedDiff = [];
    private readonly List<PendingUndo> _pendingUndo = [];

    public CommandContextImpl(
        IUnitOfWork uow,
        bool isNested,
        bool dryRun,
        string correlationId,
        CallerRef caller,
        CancellationToken ct,
        EngineCore engine,
        string? undoGroupId = null,
        string? batchId = null)
    {
        Uow = uow;
        IsNested = isNested;
        DryRun = dryRun;
        CorrelationId = correlationId;
        Caller = caller;
        Ct = ct;
        _engine = engine;
        UndoGroupId = undoGroupId;
        BatchId = batchId;
    }

    public IUnitOfWork Uow { get; }
    public bool IsNested { get; }
    public bool DryRun { get; }
    public CancellationToken Ct { get; }
    public string CorrelationId { get; }
    public CallerRef Caller { get; }

    /// <summary>顶层调用的撤销归属键（<c>CallOptions.UndoGroupId</c>）：批/宏步骤登记撤销时的缺省分组。</summary>
    internal string? UndoGroupId { get; }

    /// <summary>所属批/宏的运行键（audit_log <c>batch_id</c> 列）：嵌套派发向子上下文透传，
    /// 使 <c>audit.query {batch_id}</c> 能取齐该批的每一步（G4）。来源：批引擎填批 ID；
    /// 宏运行填自身 correlation（与 BatchReport.BatchId 同口径）；非编排调用为 null。
    /// 可变 = 仅限宏处理器在派发步骤前登记（顶层上下文由引擎构造时不知宏身份）。</summary>
    internal string? BatchId { get; set; }

    /// <summary>所属引擎（同程序集编排组件复用嵌套派发入口）。</summary>
    internal EngineCore Engine => _engine;

    public Task<CommandResult> DispatchNestedAsync(string command, object? args = null, CancellationToken ct = default)
        => _engine.ExecuteNestedAsync(this, command, args, ct);

    /// <summary>累积嵌套子命令的变更集（父提交成功后随父事件一并发布并失效缓存；父审计条目下附带子记录）。
    /// <b>设计</b>：嵌套步骤的 HumanSummary 不向上合并——人话摘要只由顶层命令产出，
    /// 嵌套子步骤只贡献「受影响实体 + 事件名 + 字段级 diff」，避免多段摘要拼接带来文案割裂。
    /// diff **按发生顺序并入、不去重**（同一实体多次变更 = 多条记录，时间线保真）。</summary>
    public void CollectNestedChange(ChangeSet? changes)
    {
        if (changes is null) return;
        _nestedEvents.AddRange(changes.Events);
        _nestedTouched.AddRange(changes.Touched);
        if (changes.Warnings is { Count: > 0 }) _nestedWarnings.AddRange(changes.Warnings);
        if (changes.Diff is { Count: > 0 }) _nestedDiff.AddRange(changes.Diff);
    }

    /// <summary>取走嵌套变更集（事件名去重、受影响实体去重；diff 保序不去重）：父级只发布/失效一次。</summary>
    public ChangeSet TakeNestedChanges()
    {
        if (_nestedTouched.Count == 0 && _nestedEvents.Count == 0 && _nestedWarnings.Count == 0
            && _nestedDiff.Count == 0)
            return ChangeSet.Empty;   // 无嵌套变更 = 空集短路，不为零集合反复分配新实例

        var merged = new ChangeSet(
            _nestedTouched.DistinctBy(r => (r.Type, r.Id)).ToArray(),
            _nestedEvents.Distinct(StringComparer.Ordinal).ToArray(),
            null,
            _nestedWarnings.Count > 0 ? _nestedWarnings.Distinct(StringComparer.Ordinal).ToArray() : null,
            _nestedDiff.Count > 0 ? _nestedDiff.ToArray() : null);
        _nestedEvents.Clear();
        _nestedTouched.Clear();
        _nestedWarnings.Clear();
        _nestedDiff.Clear();
        return merged;
    }

    // ===== 批/宏步骤的撤销暂存（E5）=====

    /// <summary>
    /// 一条待登记的撤销记录（批/宏的嵌套步骤产出）。**登记点不在此处**——撤销栈是"已提交事实"的逆向账本，
    /// 只能在提交成功后入栈：暂存 → 管道/批引擎提交后统一登记（干跑与回滚路径的暂存自然作废）。
    /// </summary>
    internal sealed record PendingUndo(
        CommandDescriptor Descriptor, JsonElement Args, IReadOnlyList<UndoInverseStep>? Inverse, string? GroupId);

    /// <summary>暂存一条步骤的撤销信息（<paramref name="groupId"/> = 所属批/宏的归属键，同键合并为一条记录）。</summary>
    internal void AddPendingUndo(CommandDescriptor descriptor, JsonElement args,
        IReadOnlyList<UndoInverseStep>? inverse, string? groupId)
        => _pendingUndo.Add(new PendingUndo(descriptor, args, inverse, groupId));

    /// <summary>取走全部暂存（提交后由登记点消费；干跑/回滚路径不消费即作废）。</summary>
    public List<PendingUndo> TakePendingUndo()
    {
        if (_pendingUndo.Count == 0) return [];
        var taken = new List<PendingUndo>(_pendingUndo);
        _pendingUndo.Clear();
        return taken;
    }
}

/// <summary>引擎入参序列化约定：snake_case 命名 + 大小写不敏感读取（标准参数口径）。</summary>
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
            // 契约：对传入的 JsonElement 直接返回原引用（不 Clone）——调用方须保证其底层
            // JsonDocument 的生命周期足够长；wire 层已先行 Clone，普通 new{...} 走下方 SerializeToElement。
            JsonElement e => e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? EmptyObject : e,
            _ => JsonSerializer.SerializeToElement(args, Options),
        };

    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");
}
