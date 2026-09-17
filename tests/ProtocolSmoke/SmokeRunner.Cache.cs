using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;

namespace ProtocolSmoke;

/// <summary>
/// §11 缓存与增量（阶段 12 性能加固）：查询缓存命中/失效的真实行为、
/// 事件携带 ChangeSet 负载（增量投递原语）、诊断读数接线。
///
/// <para>断言口径：<b>只认引擎读数与落库结果</b>——「结果相同」既可能是缓存命中也可能只是数据没变，
/// 因此一律用 <see cref="IEngine.RuntimeStats"/> 的命中/失效计数差分 + 写后重查可见性来判定。</para>
/// </summary>
internal static partial class SmokeRunner
{
    private static async Task SectionCacheAndInvalidation(SmokeState s)
    {
        var client = s.Client;
        var engine = client.Engine;

        // —— 缓存命中：同一查询第二次由缓存服务（未命中 +1 → 命中 +1）——
        var before = engine.RuntimeStats;
        var tree1 = await client.QueryAsync<List<FolderDto>>("folders.tree");
        var tree2 = await client.QueryAsync<List<FolderDto>>("folders.tree");
        var after = engine.RuntimeStats;

        Asserts.That(tree2.Count == tree1.Count, "缓存命中应返回同一份目录树");
        Asserts.That(after.CacheMisses == before.CacheMisses + 1,
            $"首次 folders.tree 应记 1 次未命中（{before.CacheMisses} → {after.CacheMisses}）");
        Asserts.That(after.CacheHits == before.CacheHits + 1,
            $"第二次 folders.tree 应记 1 次命中（{before.CacheHits} → {after.CacheHits}）");
        Asserts.That(after.CacheEntries >= 1, "命中过的条目应驻留缓存");

        // —— 未声明缓存策略的查询不参与缓存（策略由 Descriptor 单一事实源声明）——
        await client.QueryAsync<LinkPocket.Modules.Links.PagedLinkResult>("links.query", new { page = new { index = 1, size = 0 } });
        var afterUncached = engine.RuntimeStats;
        await client.QueryAsync<LinkPocket.Modules.Links.PagedLinkResult>("links.query", new { page = new { index = 1, size = 0 } });
        var afterUncached2 = engine.RuntimeStats;
        Asserts.That(afterUncached2.CacheHits == afterUncached.CacheHits
                && afterUncached2.CacheMisses == afterUncached.CacheMisses,
            "未声明 CachePolicy 的查询（links.query）不得进出缓存");

        var manifest = client.Describe();
        var cacheable = manifest.Commands.Where(c => c.IsCacheable).ToList();
        Asserts.That(cacheable.Count is > 0 and < 20,
            $"应只有少量明确声明的可缓存查询，实际 {cacheable.Count} 条");
        Asserts.That(cacheable.All(c => c.Cache!.DependsOn.Count > 0),
            "可缓存查询必须声明非空依赖（空依赖 = 永不失效，已被构造器拒绝）");

        // —— 事件驱动失效：写提交后推进世代戳，重查必须看到新数据（陈旧值不得残留）——
        var invalidBefore = engine.RuntimeStats;
        var created = (await client.FolderCreateAsync("缓存失效目录")).Data!;
        var invalidAfter = engine.RuntimeStats;
        Asserts.That(invalidAfter.CacheInvalidations > invalidBefore.CacheInvalidations,
            "写提交后应推进相关事件名的世代戳");

        var tree3 = await client.QueryAsync<List<FolderDto>>("folders.tree");
        Asserts.That(tree3.Any(f => f.FolderId == created.FolderId),
            "写后重新查询必须看到新目录（条目已随事件失效，未留陈旧值）");

        // —— 增量投递原语：事件负载 = 本次调用的 ChangeSet（受影响实体 + 事件名 + 人类摘要）——
        var domainEvents = new List<DomainEvent>();
        using (client.Subscribe(domainEvents.Add))
        {
            var link = (await client.LinkCreateAsync("https://cache.example/payload", "负载验证")).Data!;
            var published = domainEvents.FirstOrDefault(e => e.Name == "links.changed");
            Asserts.That(published is not null, "links.create 应发布 links.changed");

            var payload = published!.Data;
            Asserts.That(payload is { ValueKind: JsonValueKind.Object },
                "领域事件应携带 ChangeSet 负载（而非 null）");
            Asserts.That(payload!.Value.GetProperty("touched").EnumerateArray()
                    .Any(t => t.GetProperty("type").GetString() == "link"
                          && t.GetProperty("id").GetString() == link.LinkId),
                "负载应含被创建链接的实体引用（消费方可做行级增量处理，不必只知道有变更）");
            Asserts.That(payload.Value.GetProperty("events").EnumerateArray()
                    .Any(e => e.GetString() == "links.changed"),
                "负载应含本次发布的事件名");
            Asserts.That(payload.Value.GetProperty("human_summary").GetString() is { Length: > 0 },
                "负载应含人类可读摘要");
        }

        // —— 事件存储追平：负载随事件一并入环形存储（新会话/AI 增量消费的数据来源）——
        var replayed = new List<StoredEvent>();
        await foreach (var stored in engine.EventStore.FollowAsync()) replayed.Add(stored);
        Asserts.That(replayed.Any(e => e.Event.Data is { ValueKind: JsonValueKind.Object }),
            "事件存储中的事件应同样携带 ChangeSet 负载");

        // —— 诊断读数（观测面）：宿主已把引擎读数接线进 diagnostics.collect，不得为 null/假值 ——
        var diagnostics = await client.QueryAsync<JsonElement>("diagnostics.collect");
        var runtime = diagnostics.GetProperty("runtime");
        Asserts.That(runtime.ValueKind == JsonValueKind.Object,
            "diagnostics.runtime 段应由组合根接线提供（null = 未接线，而不是伪造 0）");
        var live = engine.RuntimeStats;
        Asserts.That(runtime.GetProperty("cache_hits").GetInt64() == live.CacheHits
                && runtime.GetProperty("cache_misses").GetInt64() == live.CacheMisses,
            "diagnostics 的缓存读数应与引擎实时读数一致（不是另一份统计源）");
        Asserts.That(runtime.GetProperty("event_store_head").GetInt64() == engine.EventStore.Head.Sequence,
            "diagnostics 的事件存储游标应与实时读数一致");

        Console.WriteLine($"[OK] §11 缓存与增量：命中 {live.CacheHits}/{live.CacheHits + live.CacheMisses} · " +
                          $"失效 {live.CacheInvalidations} 次 · 事件负载含 ChangeSet · diagnostics.runtime 已接线");
    }

    /// <summary>
    /// 缓存命中路径基准（跑在 §9 的 10k 库上）：冷查询 vs 命中。
    /// 门槛差值本身就是"缓存是否真的短路了 DB"的证据（命中 = 一次字典查找，与库大小无关）。
    /// </summary>
    private static async Task MeasureCacheLatency(EngineClient client)
    {
        List<FolderDto>? cold = null;
        var coldMs = await Perf.MeasureAsync("10k 库 folders.tree 冷查询", 30, async () =>
        {
            cold = await client.QueryAsync<List<FolderDto>>("folders.tree");
            Asserts.That(cold.Count > 0, "10k 库应有目录");
        });

        var hotMs = await Perf.MeasureAsync("10k 库 folders.tree 缓存命中", 2, async () =>
        {
            var hot = await client.QueryAsync<List<FolderDto>>("folders.tree");
            Asserts.That(hot.Count == cold!.Count, "缓存命中应返回同一份目录树");
        });

        // 两者都落在 5ms 计时粒度内时（cold == 0）不做相对断言，避免秒表精度造成假失败
        if (coldMs > 0)
            Asserts.That(hotMs < coldMs, $"缓存命中应快于冷查询（冷 {coldMs}ms / 热 {hotMs}ms）");
    }
}
