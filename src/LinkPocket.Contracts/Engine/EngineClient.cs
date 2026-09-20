using System.Text.Json;

namespace LinkPocket.Contracts;

/// <summary>
/// 引擎客户端门面（wire 层定稿）：一切消费者（WPF / 无头宿主 / 批处理 / 测试 / 未来 AI）
/// 的强类型入口。核心面 = ExecuteAsync / QueryAsync / Describe（与 IEngine 同形，可整体替换底层实现）；
/// 便利方法 = 每命令一个（参数形状与各模块 Handler 逐一对齐；返回 Contracts DTO 的命令强类型，
/// 返回模块内部 Result 的命令用 <see cref="JsonElement"/>，待模型归位阶段提升 DTO）。
/// 便利方法最终由目录元数据机械生成（catalog 导出）；现阶段人工与 Descriptor 保持同步。
/// </summary>
public sealed partial class EngineClient(IEngine engine)
{
    /// <summary>底层引擎（事件总线订阅等高级入口）。</summary>
    public IEngine Engine { get; } = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>写流：完整命令管道（校验 → 幂等 → 能力门 → 审计 → 写闸 → 事务 → 事件）。</summary>
    public Task<CommandResult<T>> ExecuteAsync<T>(string command, object? args = null,
        CallOptions? options = null, CancellationToken ct = default)
        => CallAsync(command, options, ct,
            (effective, token) => Engine.ExecuteAsync<T>(command, args, effective, token),
            auditRefOf: result => result.AuditRef, isMutation: true);

    /// <summary>读流：校验 → 读池 → Handler；免写闸、免审计。</summary>
    public Task<T> QueryAsync<T>(string query, object? args = null,
        CallOptions? options = null, CancellationToken ct = default)
        => CallAsync(query, options, ct,
            (effective, token) => Engine.QueryAsync<T>(query, args, effective, token),
            auditRefOf: _ => null, isMutation: false);

    /// <summary>
    /// 开一个**动作作用域**：期间经本客户端发出的命令共用同一个 correlation_id
    /// （调用方未显式传 <see cref="CallOptions.CorrelationId"/> 时），使"一次用户动作"的多条命令 /
    /// 多条审计行 / 全部日志对齐到同一条时间轴；退出时写一条 <c>engine.action</c> 汇总记录
    /// （动作名 / 命令数 / 失败数 / 耗时）。
    /// <para>嵌套时复用外层 correlation（整棵树一条线）；**必须 Dispose**（`using`）——
    /// 覆盖式状态，绝不留常驻标志。</para>
    /// </summary>
    public EngineCallScope BeginAction(string name)
    {
        var current = EngineCallScope.Current;
        return new EngineCallScope(name, current?.CorrelationId ?? NewCorrelationId(), current);
    }

    /// <summary>
    /// 写流 / 读流的**唯一调用通道**（67 个便利方法全部汇入这里）：补 correlation
    /// （显式 &gt; 动作作用域 &gt; 新生成）+ 记一条**消费者视角**的调用记录（值 = 引擎日志里没有的
    /// <c>audit_ref</c>；corr / 命令名由调用上下文落到记录的首类字段）。
    /// </summary>
    private async Task<TResult> CallAsync<TResult>(string command, CallOptions? options, CancellationToken ct,
        Func<CallOptions, CancellationToken, Task<TResult>> invoke,
        Func<TResult, string?> auditRefOf, bool isMutation)
    {
        var correlationId = options?.CorrelationId;
        if (string.IsNullOrEmpty(correlationId))
            correlationId = EngineCallScope.Current?.CorrelationId ?? NewCorrelationId();

        var effective = options is null
            ? new CallOptions(CorrelationId: correlationId)
            : options with { CorrelationId = correlationId };

        var caller = CallOptions.CallerOf(effective);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await invoke(effective, ct);
            EngineCallScope.Current?.CountCall(ok: true);
            LogCall(command, correlationId, caller, isMutation, watch.ElapsedMilliseconds, auditRefOf(result), null);
            return result;
        }
        catch (Exception ex)
        {
            // 失败也要计数与留痕（否则"这次动作全成功"是假的）；错误码以引擎错误为准
            EngineCallScope.Current?.CountCall(ok: false);
            LogCall(command, correlationId, caller, isMutation, watch.ElapsedMilliseconds, null,
                ex is EngineException engineError ? engineError.Error.Code : ex.GetType().Name);
            throw;
        }
    }

    private static void LogCall(string command, string correlationId, CallerRef caller, bool isMutation, long elapsedMs,
        string? auditRef, string? errorCode)
    {
        // 级别分档：写 = Info（用户动作，量少、值得常看）；读 = Debug（高频，缺省 info 级下不可见）
        var level = isMutation ? LogLevel.Info : LogLevel.Debug;
        if (!LpLog.IsEnabled(level)) return;   // 昂贵构造前短路

        var props = new Dictionary<string, object?>
        {
            ["ok"] = errorCode is null,
        };
        if (!string.IsNullOrEmpty(auditRef)) props["audit_ref"] = auditRef;
        if (errorCode is not null) props["error_code"] = errorCode;

        using var call = LpLog.BeginCall(correlationId, command, caller.ToString());
        LpLog.Write(level, "engine.call",
            errorCode is null ? $"调用完成：{command}" : $"调用失败：{command}（{errorCode}）",
            props: props, elapsedMs: elapsedMs);
    }

    private static string NewCorrelationId() => Guid.NewGuid().ToString("N");

    /// <summary>自描述：引擎全部能力（命令目录，AI 工具清单/文档的唯一事实源）。</summary>
    public EngineManifest Describe(string? category = null) => Engine.Describe(category);

    /// <summary>订阅引擎领域事件（links.changed / folders.changed / trash.changed / ...）。
    /// ⚠️ 订阅方纪律：处理器内不得同步回派命令（会自锁）——一律异步/防抖消费。</summary>
    public IDisposable Subscribe(Action<DomainEvent> handler) => Engine.Events.Subscribe(handler);

    /// <summary>事件存储（L3）：发布即写入的环形缓冲，新会话追平 / AI 轮询入口。</summary>
    public IEventStore EventStore => Engine.EventStore;
}
