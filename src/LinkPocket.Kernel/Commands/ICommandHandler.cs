using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Kernel.Commands;

/// <summary>
/// 命令上下文：Handler 只经由它取得工作单元/参数/取消令牌/嵌套派发能力。
/// 引擎在管道中构造；嵌套调用复用父调用的工作单元与写闸（同链串行，绝无死锁）。
/// </summary>
public interface ICommandContext
{
    /// <summary>本调用的工作单元（写流 = 持闸期间创建；嵌套 = 父调用的工作单元）。</summary>
    IUnitOfWork Uow { get; }

    /// <summary>是否嵌套调用（复用父 UoW 与写闸；审计合并为父条目的子记录）。</summary>
    bool IsNested { get; }

    /// <summary>干跑模式：Handler 正常执行，但引擎以不提交模式运行（事务回滚、不发事件）。</summary>
    bool DryRun { get; }

    /// <summary>取消令牌（贯穿全管道）。</summary>
    CancellationToken Ct { get; }

    /// <summary>关联 ID：贯穿审计/日志/错误/事件。</summary>
    string CorrelationId { get; }

    /// <summary>调用方身份。</summary>
    CallerRef Caller { get; }

    /// <summary>嵌套派发子命令（复用父工作单元与写闸；事件照常发布；审计合并为父条目子记录）。</summary>
    Task<CommandResult> DispatchNestedAsync(string command, object? args = null, CancellationToken ct = default);
}

/// <summary>
/// 命令处理器：模块对外动作的唯一形态。
/// 处理器不调用 CommitAsync——提交由引擎管道统一负责（单命令隐式事务、嵌套由父提交）。
/// 失败抛 <see cref="EngineException"/>（引擎捕获→审计→上抛）。
/// </summary>
public interface ICommandHandler
{
    /// <summary>命令描述符（目录自描述的唯一来源）。</summary>
    CommandDescriptor Descriptor { get; }

    Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args);
}
