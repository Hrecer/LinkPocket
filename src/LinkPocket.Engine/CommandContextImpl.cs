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

    public Task<CommandResult> DispatchNestedAsync(string command, object? args = null, CancellationToken ct = default)
        => _engine.ExecuteNestedAsync(this, command, args, ct);

    /// <summary>嵌套子命令产生的领域事件（父提交后随父事件一并发布）。</summary>
    public void CollectNestedEvent(string eventName) => _nestedEvents.Add(eventName);

    public IReadOnlyList<string> TakeNestedEvents()
    {
        var taken = _nestedEvents.ToArray();
        _nestedEvents.Clear();
        return taken;
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

    public static JsonElement ToJsonElement(object? args)
        => args switch
        {
            null => JsonSerializer.Deserialize<JsonElement>("{}"),
            JsonElement e => e,
            _ => JsonSerializer.SerializeToElement(args, Options),
        };
}
