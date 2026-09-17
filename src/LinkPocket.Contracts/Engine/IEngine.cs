namespace LinkPocket.Contracts;

/// <summary>
/// 引擎唯一入口（方案 3.1）：一切消费者（WPF/无头/批处理/未来 AI）走同一条路。
/// 写流 ExecuteAsync = 校验 → 幂等查重 → 能力门 → 审计 → 写闸 → UoW → Handler → 提交 → 事件 → 审计完成。
/// 读流 QueryAsync = 校验 → 读池（短 UoW）→ Handler → 结果；免写闸、免审计、免撤销。
/// 失败语义：Execute 失败同样抛 <see cref="EngineException"/>（wire 层转 JSON-RPC error），
/// 成功返回 <see cref="CommandResult{T}"/>。
/// </summary>
public interface IEngine
{
    /// <summary>写流：经完整命令管道。</summary>
    Task<CommandResult<T>> ExecuteAsync<T>(string command, object? args = null,
        CallOptions? options = null, CancellationToken ct = default);

    /// <summary>读流：校验 → 读池 → Handler；免写闸、免审计、免撤销。</summary>
    Task<T> QueryAsync<T>(string query, object? args = null,
        CallOptions? options = null, CancellationToken ct = default);

    /// <summary>自描述：引擎全部能力（AI 工具清单 / 文档 / 测试骨架的唯一事实源）。</summary>
    EngineManifest Describe(string? category = null);

    /// <summary>事件总线（方案 4.4）：提交成功后同步推送领域事件；
    /// ⚠️ 订阅方纪律 = 处理器内不得同步回派命令（会自锁），一律异步/防抖消费。</summary>
    IEventBus Events { get; }

    /// <summary>事件存储（方案 4.4 L3）：发布即写入的环形缓冲（默认 5000 条），
    /// 供新会话追平（<see cref="IEventStore.FollowAsync"/>）与 AI 轮询（<see cref="IEventStore.PollAsync"/>）。</summary>
    IEventStore EventStore { get; }

    /// <summary>批引擎（方案 4.3 L2 编排层）：batch.run / batch.dry_run / batch.status 的执行面。
    /// 组合时注入（OrchestrationHost）；未装配为 null（wire 调用编排命令报「未装配」）。</summary>
    IBatchEngine? Batch { get; }
}
