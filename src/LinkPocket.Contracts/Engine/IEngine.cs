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
}
