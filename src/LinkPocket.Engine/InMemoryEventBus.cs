using System.Collections.Concurrent;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>进程内事件总线（方案 4.4）：持闸期间同步推送（订阅方纪律：不得同步回派命令——架构单测强制 + 文档双保险）。</summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<int, Action<DomainEvent>> _subscribers = new();
    private int _nextId;

    public ValueTask PublishAsync(DomainEvent e)
    {
        foreach (var handler in _subscribers.Values) handler(e);
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
