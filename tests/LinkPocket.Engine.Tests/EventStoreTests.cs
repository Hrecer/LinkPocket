using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>事件存储断言（L3）：追平、游标续读、轮询、环形淘汰、引擎发布落存储。</summary>
public class EventStoreTests
{
    private static DomainEvent Ev(string name) => new(name, DateTimeOffset.Now, null, "corr", null);

    private static async Task<List<StoredEvent>> CollectAsync(IEventStore store, EventCursor? from = null)
    {
        var items = new List<StoredEvent>();
        await foreach (var ev in store.FollowAsync(from))
            items.Add(ev);
        return items;
    }

    [Fact]
    public async Task Append_Then_FollowFromStart_ReplaysAllInOrder()
    {
        var store = new InMemoryEventStore();
        store.Append(Ev("links.changed"));
        store.Append(Ev("folders.changed"));
        store.Append(Ev("trash.changed"));

        var all = await CollectAsync(store);
        Assert.Equal(new[] { 1L, 2L, 3L }, all.Select(ev => ev.Cursor.Sequence));
        Assert.Equal(new[] { "links.changed", "folders.changed", "trash.changed" }, all.Select(ev => ev.Event.Name));
        Assert.Equal(3, store.Head.Sequence);
    }

    [Fact]
    public async Task Follow_FromCursor_ReplaysOnlyLaterEvents()
    {
        var store = new InMemoryEventStore();
        for (var i = 0; i < 4; i++) store.Append(Ev($"e{i}"));

        var tail = await CollectAsync(store, new EventCursor(2));
        Assert.Equal(new[] { 3L, 4L }, tail.Select(ev => ev.Cursor.Sequence));
        Assert.Empty(await CollectAsync(store, new EventCursor(4)));   // 已追平：无新事件
    }

    [Fact]
    public async Task Poll_RespectsLimit_AndReturnsNextCursor()
    {
        var store = new InMemoryEventStore();
        for (var i = 0; i < 5; i++) store.Append(Ev($"e{i}"));

        var page1 = await store.PollAsync(limit: 2);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(2, page1.Next!.Value.Sequence);

        var page2 = await store.PollAsync(page1.Next, limit: 100);
        Assert.Equal(3, page2.Items.Count);
        Assert.Equal(5, page2.Next!.Value.Sequence);   // 从游标续读不重不漏
        Assert.Null((await store.PollAsync(page2.Next, limit: 100)).Next);   // 追平后 Next = null
    }

    [Fact]
    public async Task Ring_EvictsOldest_WhenOverCapacity()
    {
        var store = new InMemoryEventStore(capacity: 3);
        for (var i = 1; i <= 5; i++) store.Append(Ev($"e{i}"));

        var all = await CollectAsync(store);
        Assert.Equal(new[] { 3L, 4L, 5L }, all.Select(ev => ev.Cursor.Sequence));   // 最旧被淘汰
        Assert.Equal(5, store.Head.Sequence);
    }

    [Fact]
    public async Task Engine_Publish_Events_Are_Stored_DryRun_Is_Not()
    {
        var (factory, path) = TestEnv.CreateDb();
        try
        {
            var engine = TestEnv.CreateEngine(factory);

            await engine.ExecuteAsync<string>("test.add_folder", new { name = "落存储" });
            var afterFirst = engine.EventStore.Head.Sequence;
            Assert.True(afterFirst >= 1, "发布事件应自动写入存储");

            // 干跑零事件 → 存储不增长
            await engine.ExecuteAsync<string>("test.add_folder", new { name = "预演" }, new CallOptions(DryRun: true));
            Assert.Equal(afterFirst, engine.EventStore.Head.Sequence);

            // 追平可见 folders.changed；失败命令不产生事件、不落存储
            var all = await CollectAsync(engine.EventStore);
            Assert.Contains(all, ev => ev.Event.Name == "folders.changed");
            await Assert.ThrowsAsync<EngineException>(() => engine.ExecuteAsync<string>("test.fail"));
            Assert.Equal(afterFirst, engine.EventStore.Head.Sequence);
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
