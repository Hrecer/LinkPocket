using System.Collections.Concurrent;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 进程内事件总线：持闸期间同步推送（订阅方纪律：不得同步回派命令——架构单测强制 + 文档双保险）。
/// 每个订阅方独立 try/catch：单个订阅方的异常只记录日志、不阻断其余订阅方，
/// 且绝不回传调用方——「已提交的写必须成功返回，订阅方异常不能回传」（否则会让已落库的写报成 LP.SYS.003）。
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<int, Action<DomainEvent>> _subscribers = new();
    private int _nextId;

    public ValueTask PublishAsync(DomainEvent e)
    {
        foreach (var handler in _subscribers.Values)
        {
            try
            {
                handler(e);
            }
            catch (Exception ex)
            {
                // 订阅方异常被隔离：记录日志，继续投递其余订阅方（观测面纪律——订阅方绝不能拖垮已提交的写）。
                LpLog.Warn("event subscriber threw (isolated; other subscribers and the caller are unaffected)", ex, category: "engine.events");
            }
        }
        return ValueTask.CompletedTask;
    }

    public IDisposable Subscribe(Action<DomainEvent> handler)
    {
        var id = Interlocked.Increment(ref _nextId);
        _subscribers[id] = handler;
        return new Subscription(this, id);
    }

    private sealed class Subscription(InMemoryEventBus bus, int id) : IDisposable
    {
        public void Dispose() => bus._subscribers.TryRemove(id, out _);
    }
}
