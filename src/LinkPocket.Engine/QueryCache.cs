using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Engine;

/// <summary>
/// 查询结果缓存（读流 / 关键路径优化表）。
///
/// <para><b>失效模型 = 世代戳 + 事件名</b>（与 <see cref="CachePolicy"/> 配套）：
/// 每个事件名持有一个单调递增的世代戳；读路径在<b>触库之前</b>对依赖取快照
/// （<see cref="Snapshot"/>），随结果存进条目（<see cref="Set"/>）；写路径提交后按实际发布的事件名
/// 推进世代戳（<see cref="Invalidate"/>）。条目仅当「存档快照 == 当前世代」时才可命中。</para>
///
/// <para><b>为什么不会读到陈旧数据</b>：任何写提交 → 世代戳前进 → 此前存档的条目全部失配（不命中）。
/// 写与读交错时最坏情况是"本该命中的条目失配"（多一次回填），即误差方向恒为"少命中"，
/// 绝不会是"命中陈旧值"。TTL 再兜底覆盖"进程外改库/漏发事件"这类世代戳推不动的场景。</para>
///
/// <para><b>容量</b>：LRU 上限（缺省 256 条），超出先清失效条目、再淘汰最久未用。
/// 线程模型：查询路径无写闸，可能多线程并发访问 → 全部状态走单锁（条目数极少、临界区极短）。</para>
/// </summary>
public sealed class QueryCache
{
    /// <summary>缺省容量：目录页/树/统计这类大结果按"每命令每参数组合一条"计，256 条足以覆盖常用视图。</summary>
    public const int DefaultCapacity = 256;

    private sealed class Entry
    {
        public required string[] DependsOn;
        public required long[] Stamp;
        public required object? Value;
        public required long ExpiresAtTicks;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();   // 首 = 最近使用
    private readonly int _capacity;

    private long _hits;
    private long _misses;
    private long _evictions;
    private long _staleRemovals;
    private long _invalidations;

    public QueryCache(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>当前条目数（诊断用）。</summary>
    public long Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>取依赖的世代戳快照——必须在触库之前调用，否则可能存档到"读之后才失效"的世代。</summary>
    public long[] Snapshot(IReadOnlyList<string> dependsOn)
    {
        var stamp = new long[dependsOn.Count];
        lock (_lock)
        {
            for (var i = 0; i < dependsOn.Count; i++)
                stamp[i] = _generations.TryGetValue(dependsOn[i], out var gen) ? gen : 0L;
        }
        return stamp;
    }

    /// <summary>命中判定：条目存在、快照与当前世代逐项相等、且未过 TTL。</summary>
    public bool TryGet(string key, long[] stamp, out object? value)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry)
                && entry.Stamp.Length == stamp.Length
                && entry.Stamp.AsSpan().SequenceEqual(stamp)
                && entry.ExpiresAtTicks > DateTime.UtcNow.Ticks)
            {
                Touch(key);
                _hits++;
                value = entry.Value;
                return true;
            }

            // 失配 = 已失效（世代不符或过期）：顺手摘除，避免容量被死条目占用
            if (_entries.Remove(key)) _lru.Remove(key);
            _misses++;
            value = null;
            return false;
        }
    }

    /// <summary>回填条目：快照必须是本次读取之前取的快照，否则会存成"当前世代"的陈旧结果。</summary>
    public void Set(string key, IReadOnlyList<string> dependsOn, long[] stamp, object? value, TimeSpan ttl)
    {
        if (value is null) return;   // 空值不进缓存：省掉一次 null 语义判断
        lock (_lock)
        {
            if (_entries.Remove(key)) _lru.Remove(key);
            if (_entries.Count >= _capacity) Evict();

            _entries[key] = new Entry
            {
                DependsOn = [.. dependsOn],
                Stamp = stamp,
                Value = value,
                ExpiresAtTicks = DateTime.UtcNow.Add(ttl).Ticks,
            };
            _lru.AddFirst(key);
        }
    }

    /// <summary>事件驱动失效：推进这些事件名的世代戳（已缓存条目随即全部失配）。</summary>
    public void Invalidate(IEnumerable<string> eventNames)
    {
        lock (_lock)
        {
            foreach (var name in eventNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                _generations[name] = _generations.TryGetValue(name, out var gen) ? gen + 1 : 1L;
                _invalidations++;
            }
        }
    }

    /// <summary>整库重置（maintenance.reinit：表被清空，任何缓存条目都不再可信）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _lru.Clear();
        }
    }

    /// <summary>统计快照（诊断用；只读，不影响缓存状态）。</summary>
    public QueryCacheCounters Counters
    {
        get
        {
            lock (_lock)
            {
                return new QueryCacheCounters(_entries.Count, _hits, _misses, _evictions, _staleRemovals, _invalidations);
            }
        }
    }

    /// <summary>
    /// 失效清理：先清掉已失效/过期条目（世代失配或 TTL 过期，记入 <c>StaleRemovals</c>），
    /// 仍超容量再去尾（最久未用，记入 <c>Evictions</c>）。口径分开：Evictions 只统计「容量淘汰」。
    /// </summary>
    private void Evict()
    {
        var now = DateTime.UtcNow.Ticks;
        var stale = _entries
            .Where(kv => kv.Value.ExpiresAtTicks <= now || !StampMatches(kv.Value))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in stale)
        {
            _entries.Remove(key);
            _lru.Remove(key);
            _staleRemovals++;
        }

        while (_entries.Count >= _capacity && _lru.Last is { } last)
        {
            _entries.Remove(last.Value);
            _lru.RemoveLast();
            _evictions++;
        }
    }

    private bool StampMatches(Entry entry)
    {
        if (entry.Stamp.Length != entry.DependsOn.Length) return false;
        for (var i = 0; i < entry.DependsOn.Length; i++)
        {
            var current = _generations.TryGetValue(entry.DependsOn[i], out var gen) ? gen : 0L;
            if (current != entry.Stamp[i]) return false;
        }
        return true;
    }

    private void Touch(string key)
    {
        _lru.Remove(key);
        _lru.AddFirst(key);
    }

    /// <summary>
    /// 缓存键 = 命令名 + 规范化入参 JSON（同一命令不同参数各自一条）。
    /// ⚠️ 入参会以 wire 直路由的 <c>default(JsonElement)</c>（<see cref="JsonValueKind.Undefined"/>）形态抵达，
    /// 它与「无参」是同一语义 → 统一归一为 <c>{}</c>；<c>GetRawText()</c> 对 Undefined 会抛异常，
    /// 缓存键构造失败绝不能把整条读流带崩（观测面/加速器不许成为故障源）。
    /// </summary>
    public static string BuildKey(string command, JsonElement args)
    {
        var raw = args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : args.GetRawText();
        return command + "\u0000" + raw;
    }
}

/// <summary>查询缓存计数快照（引擎内部读数；对外呈现走 <see cref="EngineRuntimeStats"/>）。</summary>
public readonly record struct QueryCacheCounters(
    long Entries, long Hits, long Misses, long Evictions, long StaleRemovals, long Invalidations);
