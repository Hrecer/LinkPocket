using LinkPocket.Contracts;
using LinkPocket.Engine;

namespace ProtocolSmoke;

/// <summary>冒烟运行状态：当前引擎面 + 事件收集 + 「整库重置」语义（丢弃库文件另起全新实例）。</summary>
internal sealed class SmokeState
{
    public EngineClient Client { get; private set; } = null!;
    public EngineWire Wire { get; private set; } = null!;
    public List<string> Events { get; } = [];
    public string WorkDir { get; } = AppContext.BaseDirectory;
    public string DbPath { get; private set; } = null!;
    private IDisposable? _subscription;

    public static SmokeState Create()
    {
        var s = new SmokeState();
        s.Spawn();
        return s;
    }

    /// <summary>整库重置（行为等价语义：旧协议 resetData 的终态 = 全新空库）。</summary>
    public void Reset()
    {
        if (DbPath != null) ProbeEnv.TryDelete(DbPath);
        Spawn();
    }

    /// <summary>另起全新库并挂事件收集（临时隔离库，供性能节等场景复用）。</summary>
    public void Spawn()
    {
        _subscription?.Dispose();
        Events.Clear();

        var (client, wire, dbPath) = ProbeEnv.Create();
        Client = client;
        Wire = wire;
        DbPath = dbPath;
        _subscription = client.Subscribe(e => Events.Add(e.Name));
    }
}
