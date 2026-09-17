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

    /// <summary>
    /// 整库重置（行为等价语义：旧协议 resetData 的终态 = 全新空库）。
    /// ⚠️ 重置会删除当前库文件并另起引擎实例：<b>此前取得的一切 EngineClient / EngineWire 引用就此作废</b>
    /// （继续使用会在"文件已删"的路径上被 SQLite 静默重建为空库，命令看似可用实则对着空库执行）。
    /// 返回新引擎，调用点一律写成 <c>client = s.Reset();</c> 以免滞留旧引用。
    /// </summary>
    public EngineClient Reset()
    {
        if (DbPath != null) ProbeEnv.TryDelete(DbPath);
        Spawn();
        return Client;
    }

    /// <summary>另起全新库并挂事件收集（临时隔离库，供性能节等场景复用）。同样使旧引擎引用作废。</summary>
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
