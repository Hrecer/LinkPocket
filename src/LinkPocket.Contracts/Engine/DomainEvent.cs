using System.Text.Json;

namespace LinkPocket.Contracts;

/// <summary>
/// 领域事件：提交成功后发布；持闸期间同步推送给内存订阅方
/// （不变量：订阅方不得在处理器内同步回派命令，违者死锁——架构单测 + 文档双保险）；
/// 同时写入事件存储供追平/轮询（已落地：<see cref="IEventStore"/> 进程内环形缓冲）。
/// </summary>
public sealed record DomainEvent(
    string Name,
    DateTimeOffset At,
    JsonElement? Data,
    string CorrelationId,
    CallerRef? Caller);

/// <summary>事件总线：进程内同步推送 + 事件存储（<see cref="IEventStore"/> 追平游标）。</summary>
public interface IEventBus
{
    ValueTask PublishAsync(DomainEvent e);

    /// <summary>订阅内存事件流；返回 IDisposable，Dispose 即退订。</summary>
    IDisposable Subscribe(Action<DomainEvent> handler);
}
