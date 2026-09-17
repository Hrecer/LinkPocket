using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 查询缓存单元测试（阶段 12）：世代戳语义、事件名精确失效、TTL 兜底、LRU 容量、键构造。
/// 这些断言是「缓存不会读到陈旧数据」这条不变量在单元层的证据。
/// </summary>
public class QueryCacheUnitTests
{
    private static readonly string[] DepFolders = [DomainEventNames.FoldersChanged];
    private static readonly string[] DepLinks = [DomainEventNames.LinksChanged];
    private static readonly TimeSpan LongTtl = TimeSpan.FromSeconds(30);

    [Fact]
    public void Entry_Is_Served_While_Stamp_Matches()
    {
        var cache = new QueryCache();
        var stamp = cache.Snapshot(DepFolders);
        cache.Set("k", DepFolders, stamp, "value", LongTtl);

        Assert.True(cache.TryGet("k", cache.Snapshot(DepFolders), out var value));
        Assert.Equal("value", value);
        Assert.Equal(1L, cache.Counters.Hits);
    }

    [Fact]
    public void Entry_Misses_After_Dependency_Invalidation()
    {
        var cache = new QueryCache();
        var stamp = cache.Snapshot(DepFolders);
        cache.Set("k", DepFolders, stamp, "value", LongTtl);

        cache.Invalidate(DepFolders);

        Assert.False(cache.TryGet("k", cache.Snapshot(DepFolders), out _));
        Assert.Equal(1L, cache.Counters.Invalidations);
    }

    [Fact]
    public void Entry_Is_Untouched_By_Unrelated_Event()
    {
        var cache = new QueryCache();
        var stamp = cache.Snapshot(DepFolders);
        cache.Set("k", DepFolders, stamp, "value", LongTtl);

        cache.Invalidate(DepLinks);   // 失效是"按依赖精确"的，不是全清

        Assert.True(cache.TryGet("k", cache.Snapshot(DepFolders), out _));
    }

    /// <summary>
    /// 写期间的并发读：读者在触库前取的快照已过期 —— 用旧快照回填的条目必须失效，
    /// 否则一次交错就会把陈旧结果长期留在缓存里。
    /// </summary>
    [Fact]
    public void Entry_Filled_With_Stale_Stamp_Misses()
    {
        var cache = new QueryCache();
        var staleStamp = cache.Snapshot(DepFolders);   // 读取开始前
        cache.Invalidate(DepFolders);                  // 读取进行中发生写提交
        cache.Set("k", DepFolders, staleStamp, "value", LongTtl);

        Assert.False(cache.TryGet("k", cache.Snapshot(DepFolders), out _));
    }

    [Fact]
    public void Entry_Expires_By_Ttl()
    {
        var cache = new QueryCache();
        var stamp = cache.Snapshot(DepFolders);
        cache.Set("k", DepFolders, stamp, "value", TimeSpan.FromMilliseconds(1));

        Thread.Sleep(30);

        Assert.False(cache.TryGet("k", stamp, out _));
    }

    [Fact]
    public void Null_Value_Is_Not_Cached()
    {
        var cache = new QueryCache();
        cache.Set("k", DepFolders, cache.Snapshot(DepFolders), null, LongTtl);
        Assert.Equal(0L, cache.Count);
    }

    [Fact]
    public void Capacity_Evicts_Least_Recently_Used()
    {
        var cache = new QueryCache(capacity: 2);
        var stamp = cache.Snapshot(DepFolders);
        cache.Set("a", DepFolders, stamp, 1, LongTtl);
        cache.Set("b", DepFolders, stamp, 2, LongTtl);
        Assert.True(cache.TryGet("a", stamp, out _));   // a 变为最近使用

        cache.Set("c", DepFolders, stamp, 3, LongTtl);

        Assert.Equal(2L, cache.Count);
        Assert.False(cache.TryGet("b", stamp, out _));  // 最久未用 → 被淘汰
        Assert.True(cache.TryGet("a", stamp, out _));
        Assert.Equal(1L, cache.Counters.Evictions);
    }

    [Fact]
    public void Clear_Drops_Every_Entry()
    {
        var cache = new QueryCache();
        var stamp = cache.Snapshot(DepFolders);
        cache.Set("a", DepFolders, stamp, 1, LongTtl);
        cache.Set("b", DepLinks, cache.Snapshot(DepLinks), 2, LongTtl);

        cache.Clear();

        Assert.Equal(0L, cache.Count);
        Assert.False(cache.TryGet("a", stamp, out _));
    }

    [Fact]
    public void BuildKey_Distinguishes_Command_And_Args()
    {
        var f1 = JsonSerializer.Deserialize<JsonElement>("""{"folder_id":"F1"}""");
        var f2 = JsonSerializer.Deserialize<JsonElement>("""{"folder_id":"F2"}""");

        Assert.NotEqual(QueryCache.BuildKey("folders.contents", f1), QueryCache.BuildKey("folders.contents", f2));
        Assert.NotEqual(QueryCache.BuildKey("folders.contents", f1), QueryCache.BuildKey("folders.tree", f1));
        Assert.Equal(QueryCache.BuildKey("folders.contents", f1), QueryCache.BuildKey("folders.contents", f1));
    }

    /// <summary>
    /// wire 直路由（方法名 = 命令名）会把 params 解析成 <c>default(JsonElement)</c>，
    /// 其 ValueKind = Undefined 且 <c>GetRawText()</c> 会抛异常 —— 键构造必须归一为"无参"。
    /// </summary>
    [Fact]
    public void BuildKey_Normalizes_Undefined_Args()
    {
        var undefined = default(JsonElement);
        var empty = JsonSerializer.Deserialize<JsonElement>("{}");

        Assert.Equal(QueryCache.BuildKey("links.stats", empty), QueryCache.BuildKey("links.stats", undefined));
    }

    [Fact]
    public void Policy_Rejects_Empty_Dependencies()
        => Assert.Throws<ArgumentException>(() => CachePolicy.Of(10));
}

/// <summary>
/// 查询缓存接入管道后的行为（阶段 12）：
/// 命中不触库、变更按声明的事件名精确失效、整库影响面命令清空缓存、读数可观测。
/// 断言一律以「Handler 实际执行次数」为准 —— 只比结果值无法区分"命中缓存"与"数据没变"。
/// </summary>
public class QueryCacheEngineTests
{
    private const string FoldersQuery = "test.cached_folders";
    private const string LinksQuery = "test.cached_links";

    private static CachedQueryHandler FoldersDep()
        => new(FoldersQuery, CachePolicy.Of(60, DomainEventNames.FoldersChanged));

    private static CachedQueryHandler LinksDep()
        => new(LinksQuery, CachePolicy.Of(60, DomainEventNames.LinksChanged));

    [Fact]
    public async Task Cacheable_Query_Serves_Second_Call_From_Cache()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var cached = FoldersDep();
            var engine = TestEnv.CreateEngine(factory, cached);

            var first = await engine.QueryAsync<int>(FoldersQuery);
            var second = await engine.QueryAsync<int>(FoldersQuery);

            Assert.Equal(first, second);
            Assert.Equal(1, cached.Executions);                    // 第二次未触库
            Assert.Equal(1L, engine.Cache.Counters.Hits);
            Assert.Equal(1L, engine.Cache.Counters.Misses);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Mutation_Invalidates_Declared_Dependency()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var cached = FoldersDep();
            var engine = TestEnv.CreateEngine(factory, cached);
            await engine.QueryAsync<int>(FoldersQuery);            // 冷启动填缓存

            await engine.ExecuteAsync<string>("test.add_folder", new { name = "A" });   // 发布 folders.changed

            var after = await engine.QueryAsync<int>(FoldersQuery);
            Assert.Equal(1, after);                                // 看到新数据（未命中陈旧条目）
            Assert.Equal(2, cached.Executions);                    // 失效后重新触库
            Assert.Equal(1L, engine.Cache.Counters.Invalidations);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Unrelated_Event_Does_Not_Invalidate_Other_Dependency()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var folders = FoldersDep();
            var links = LinksDep();
            var engine = TestEnv.CreateEngine(factory, folders, links, new EmitEventHandler(DomainEventNames.LinksChanged));
            await engine.QueryAsync<int>(FoldersQuery);
            await engine.QueryAsync<int>(LinksQuery);

            await engine.ExecuteAsync<string>("test.emit_links_changed");   // 只发布 links.changed

            await engine.QueryAsync<int>(FoldersQuery);
            await engine.QueryAsync<int>(LinksQuery);

            Assert.Equal(1, folders.Executions);   // 依赖 folders.changed → 未受影响（精确失效，不是全清）
            Assert.Equal(2, links.Executions);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Database_Impact_Command_Clears_Whole_Cache()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var cached = FoldersDep();
            var engine = TestEnv.CreateEngine(factory, cached);
            await engine.QueryAsync<int>(FoldersQuery);
            Assert.Equal(1L, engine.Cache.Count);

            // test.destructive 的 Impact = Database（整库影响面）
            var denied = await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<string>("test.destructive"));
            Assert.Equal(EngineErrors.ConfirmRequired, denied.Error.Code);
            var token = denied.Error.Details!.Value.GetProperty("confirm_token").GetString();

            await engine.ExecuteAsync<string>("test.destructive", options: new CallOptions(ConfirmToken: token));

            Assert.Equal(0L, engine.Cache.Count);
            await engine.QueryAsync<int>(FoldersQuery);
            Assert.Equal(2, cached.Executions);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Runtime_Stats_Expose_Cache_And_Event_Reading()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var engine = TestEnv.CreateEngine(factory, FoldersDep());
            await engine.QueryAsync<int>(FoldersQuery);   // miss
            await engine.QueryAsync<int>(FoldersQuery);   // hit

            var stats = engine.RuntimeStats;
            Assert.Equal(1L, stats.CacheEntries);
            Assert.Equal(1L, stats.CacheHits);
            Assert.Equal(1L, stats.CacheMisses);
            Assert.True(stats.CacheHitRate > 0);
            Assert.Equal(engine.EventStore.Head.Sequence, stats.EventStoreHead);
        }
        finally { TryDelete(path); }
    }

    /// <summary>
    /// 嵌套派发的事件同样进父级失效批次（否则跨模块编排写完之后，目录页还会看到旧树）。
    /// </summary>
    [Fact]
    public async Task Nested_Mutation_Invalidates_Through_Parent_Pipeline()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var cached = FoldersDep();
            var engine = TestEnv.CreateEngine(factory, cached);
            await engine.QueryAsync<int>(FoldersQuery);

            // test.nested_add 内部派发 test.add_folder（其 folders.changed 由父管道统一发布）
            await engine.ExecuteAsync<string>("test.nested_add", new { name = "B" });

            Assert.Equal(1, await engine.QueryAsync<int>(FoldersQuery));
            Assert.Equal(2, cached.Executions);
        }
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
        catch { /* 临时文件清理尽力而为 */ }
    }
}
