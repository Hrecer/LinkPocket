namespace LinkPocket.Contracts;

/// <summary>
/// 一个**用户动作**的调用作用域（经 <see cref="EngineClient.BeginAction"/> 开启）：
/// 期间经客户端发出的命令**共用同一个 correlation_id**，使"一次动作"的 N 条命令 / N 条审计行 / 全部日志
/// 对齐到同一条时间轴（一次拖拽 10 项 = 10 条命令，靠它才是一条线索而不是十条）。
///
/// <para><b>口径（与本案的红线一致）</b>：</para>
/// <list type="bullet">
/// <item>**覆盖式**：AsyncLocal 存当前作用域，嵌套时内层生效；退出即复位——**绝不留常驻标志**；</item>
/// <item>**嵌套不产生新 id**：内层复用外层的 correlation（整棵树一条线），退出时计数**并回父作用域**；</item>
/// <item>**必须 Dispose**（`using`）：否则 correlation 会泄漏到后续不相关的调用上（观察面污染）；</item>
/// <item>计数如实（<see cref="Calls"/> / <see cref="Failures"/>）：失败的命令已由引擎按同一 correlation
/// 记 Warn，这里只汇总，**不重复报错**；</item>
/// <item>退出时写一条 <c>engine.action</c> 完成记录（含动作名 / 命令数 / 失败数 / 耗时），
/// 与内部各条记录同 correlation——"该次动作发了多少命令、有没有失败"一眼可读。</item>
/// </list>
/// </summary>
public sealed class EngineCallScope : IDisposable
{
    /// <summary>日志分类（与 engine.pipeline / engine.batch / engine.undo 同族）。</summary>
    public const string LogCategory = "engine.action";

    internal static readonly AsyncLocal<EngineCallScope?> Ambient = new();

    private readonly EngineCallScope? _parent;
    private readonly IDisposable? _logScope;
    private readonly IDisposable? _callContext;
    private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
    private long _calls;
    private long _failures;
    private int _disposed;

    internal EngineCallScope(string name, string correlationId, EngineCallScope? parent)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "unnamed action" : name.Trim();
        CorrelationId = correlationId;
        _parent = parent;

        Ambient.Value = this;
        // 日志侧同步挂上：作用域内的每条记录都带 action 字段，且 corr 落到**首类字段**（不靠调用方手抄）
        _logScope = LpLog.BeginScope(("action", Name));
        _callContext = LpLog.BeginCall(correlationId);
    }

    /// <summary>动作名（人类可读：如「拖拽移动」「粘贴」「删除」）。</summary>
    public string Name { get; }

    /// <summary>本动作的关联 ID（嵌套时 = 外层，整棵树一条线）。</summary>
    public string CorrelationId { get; }

    /// <summary>作用域内发出的命令数。</summary>
    public long Calls => Interlocked.Read(ref _calls);

    /// <summary>作用域内失败的命令数（成功与否由客户端逐个如实计入）。</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>当前生效的动作（无 = null）。</summary>
    public static EngineCallScope? Current => Ambient.Value;

    /// <summary>客户端每完成一次调用登记一次（唯一写入点）。</summary>
    internal void CountCall(bool ok)
    {
        Interlocked.Increment(ref _calls);
        if (!ok) Interlocked.Increment(ref _failures);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            // 完成记录仍写在"调用上下文 + 作用域"内 → 自动带 corr（首类字段）与 action
            LpLog.Write(LogLevel.Info, LogCategory, $"Action completed: {Name}", props: new Dictionary<string, object?>
            {
                ["calls"] = Calls,
                ["failures"] = Failures,
            }, elapsedMs: _watch.ElapsedMilliseconds);
        }
        finally
        {
            _callContext?.Dispose();
            _logScope?.Dispose();

            // 只在"自己仍是栈顶"时回退（乱序释放不误伤内层）
            if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = _parent;

            if (_parent is not null)
            {
                Interlocked.Add(ref _parent._calls, Calls);
                Interlocked.Add(ref _parent._failures, Failures);
            }
        }
    }
}
