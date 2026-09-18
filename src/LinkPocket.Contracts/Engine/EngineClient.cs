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
        => Engine.ExecuteAsync<T>(command, args, options, ct);

    /// <summary>读流：校验 → 读池 → Handler；免写闸、免审计。</summary>
    public Task<T> QueryAsync<T>(string query, object? args = null,
        CallOptions? options = null, CancellationToken ct = default)
        => Engine.QueryAsync<T>(query, args, options, ct);

    /// <summary>自描述：引擎全部能力（命令目录，AI 工具清单/文档的唯一事实源）。</summary>
    public EngineManifest Describe(string? category = null) => Engine.Describe(category);

    /// <summary>订阅引擎领域事件（links.changed / folders.changed / trash.changed / ...）。
    /// ⚠️ 订阅方纪律：处理器内不得同步回派命令（会自锁）——一律异步/防抖消费。</summary>
    public IDisposable Subscribe(Action<DomainEvent> handler) => Engine.Events.Subscribe(handler);

    /// <summary>事件存储（L3）：发布即写入的环形缓冲，新会话追平 / AI 轮询入口。</summary>
    public IEventStore EventStore => Engine.EventStore;
}
