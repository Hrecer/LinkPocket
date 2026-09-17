using System.Diagnostics;
using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Data;

namespace ProtocolSmoke;

/// <summary>§6 引擎能力（links.query/batch/幂等/干跑）+ §7 并发压测 + §8 错误模型 + §9 10k 性能门槛。</summary>
internal static partial class SmokeRunner
{
    private static async Task SectionEngineAbilities(SmokeState s)
    {
        var client = s.Client;
        var f1 = (await client.FolderCreateAsync("能力F1")).Data!;
        var f2 = (await client.FolderCreateAsync("能力F2")).Data!;
        var git = (await client.LinkCreateAsync("https://github.com/x", "GitHub", listId: f1.FolderId)).Data!;
        var gitlab = (await client.LinkCreateAsync("https://gitlab.com/y", "GitLab", listId: f1.FolderId)).Data!;
        var other = (await client.LinkCreateAsync("https://other.example/", "其他", listId: f1.FolderId)).Data!;

        // ★ links.query：filter / sort / fields / 白名单外字段拒绝（PagedLinkResult 引擎能力 DTO）
        var byUrl = await client.QueryAsync<LinkPocket.Modules.Links.PagedLinkResult>("links.query", new
        {
            filter = new object[] { new { field = "url", op = "starts", value = "https://git" } },
            sort = new object[] { new { field = "title", dir = "asc" } },
            page = new { index = 1, size = 0 },
        });
        Asserts.That(byUrl.Total == 2, $"links.query url starts 应命中 2，实际 {byUrl.Total}");

        var projected = await client.QueryAsync<LinkPocket.Modules.Links.PagedLinkResult>("links.query", new
        {
            filter = new object[] { new { field = "title", op = "contains", value = "GitHub" } },
            fields = new[] { "id", "title" },
        });
        var row = projected.Items.Single() as Dictionary<string, object?>
            ?? throw new Exception("fields 投影的 item 应为字典");
        Asserts.That(row.ContainsKey("title") && !row.ContainsKey("url"),
            "fields 投影应只含 id/title");

        var badField = await AssertThrowsAsync(() => client.QueryAsync<LinkPocket.Modules.Links.PagedLinkResult>("links.query", new
        {
            filter = new object[] { new { field = "hacker", op = "eq", value = "1" } },
        }));
        Asserts.That(badField.Error.Code == EngineErrors.EnumOutOfRange, "白名单外字段应报 LP.VAL.003（防注入）");

        // ★ move_batch / copy_batch：单命令单事务
        var moved = (await client.LinkMoveBatchAsync([git.LinkId], f2.FolderId)).Data!;
        Asserts.That(moved.Affected == 1, "move_batch 应移动 1 条");
        var copied = (await client.LinkCopyBatchAsync([git.LinkId], f2.FolderId)).Data!;
        Asserts.That(copied.Affected == 1, "copy_batch 应复制 1 条");
        var stats = (await client.LinkStatsAsync());
        Asserts.That(stats.ByFolder[f1.FolderId] == 2 && stats.ByFolder[f2.FolderId] == 2,
            "移动+复制后计数应为 F1=2 / F2=2");

        // ★ visit_batch：批量记录访问
        await client.LinkVisitBatchAsync([git.LinkId, other.LinkId]);
        Asserts.That((await client.LinkGetAsync(git.LinkId)).VisitCount == 1, "visit_batch 后 VisitCount 应为 1");

        // 幂等键：24h 窗口内重复调用返回首次结果、不重复执行
        var key = $"smoke-{Guid.NewGuid():N}";
        var first = await client.LinkCreateAsync("https://idempotent.example/", "幂等", o: new CallOptions(IdempotencyKey: key));
        var second = await client.LinkCreateAsync("https://idempotent.example/", "幂等", o: new CallOptions(IdempotencyKey: key));
        Asserts.That(first.Data!.LinkId == second.Data!.LinkId, "幂等重调应返回首次结果（同 ID）");
        var idemCount = (await client.LinkListAsync(perPage: 0)).Links.Count(l => l.Url == "https://idempotent.example/");
        Asserts.That(idemCount == 1, "幂等重调不得产生第二条链接");

        // 干跑：执行但回滚，零副作用
        var dryEvents = s.Events.Count;
        var dry = await client.FolderCreateAsync("干跑目录", o: new CallOptions(DryRun: true));
        Asserts.That(dry.Data!.Name == "干跑目录", "干跑仍返回结果");
        Asserts.That(!(await client.FolderTreeAsync()).Any(f => f.Name == "干跑目录"), "干跑不得落库");
        Asserts.That(s.Events.Count == dryEvents, "干跑不得发布事件");

        // ★ 事件存储（阶段 8，方案 4.4）：发布即入环形存储；追平回放到 Head / 游标续读 / 轮询 limit
        var store = client.EventStore;
        Asserts.That(store.Head.Sequence > 0, "事件存储应有事件（Head > 0）");
        var replay = new List<StoredEvent>();
        await foreach (var ev in store.FollowAsync()) replay.Add(ev);
        Asserts.That(replay.Count > 0 && replay[^1].Cursor.Sequence == store.Head.Sequence,
            "追平应从最早存活事件回放到 Head");
        Asserts.That(replay.Any(ev => ev.Event.Name == "links.changed"), "存储应含 links.changed");

        var mid = replay[replay.Count / 2].Cursor;
        var tail = new List<StoredEvent>();
        await foreach (var ev in store.FollowAsync(mid)) tail.Add(ev);
        Asserts.That(tail.Count == replay.Count - replay.Count / 2 - 1,
            "从游标续读应只含游标之后的事件");

        var poll = await store.PollAsync(mid, 2);
        Asserts.That(poll.Items.Count == 2 && poll.Next is { } next
                && next.Sequence == mid.Sequence + 2,
            "轮询 limit=2 应给出 Next = 游标+2");
        Asserts.That((await store.PollAsync(mid, 1)).Items[0].Cursor.Sequence == mid.Sequence + 1,
            "轮询应从游标后第一条开始");

        Console.WriteLine("[OK] §6 引擎能力：links.query（白名单防注入）/ batch 三件套 / 幂等键 / DryRun 零副作用 / 事件存储追平轮询");
    }

    private static async Task SectionConcurrency(SmokeState s)
    {
        // 引擎写闸 + WAL 读池：16 写 + 32 读真正并发在途，零丢失、事件精确
        s.Events.Clear();
        var folder = (await s.Client.FolderCreateAsync("并发压测")).Data!;
        const int FanOut = 16;
        var tasks = new List<Task>();
        for (var i = 0; i < FanOut; i++)
        {
            var idx = i;
            tasks.Add(s.Client.LinkCreateAsync($"https://stress.example.com/{idx}", $"并发链接 {idx}", listId: folder.FolderId));
            tasks.Add(s.Client.LinkStatsAsync());                                    // 读
            tasks.Add(s.Client.FolderContentsAsync(folder.FolderId));                // 同目录读写并发
            tasks.Add(s.Client.LinkListAsync(perPage: 0));                           // 全量读
        }
        await Task.WhenAll(tasks);   // 任一调用抛异常 → WhenAll 直接失败

        var contents = (await s.Client.FolderContentsAsync(folder.FolderId));
        Asserts.That(contents.Links.Count == FanOut, $"并发写后文件夹应有 {FanOut} 条链接，实际 {contents.Links.Count}");
        Asserts.That(contents.DirectLinkCount == FanOut, "并发写后直接子链接计数应精确");
        var all = (await s.Client.LinkListAsync(perPage: 0)).Links;
        Asserts.That(all.Count(l => l.ListId == folder.FolderId) == FanOut, "并发写不得丢失或重复任何一条");
        Asserts.That(s.Events.Count(e => e == "links.changed") == FanOut,
            $"每条并发写应推一次 links.changed（实际 {s.Events.Count(e => e == "links.changed")}）");

        Console.WriteLine($"[OK] §7 并发压测：{FanOut} 写 + {FanOut * 2} 读混发零丢失、计数与事件精确（写闸 + WAL 读池）");
    }

    private static async Task SectionErrorModel(SmokeState s)
    {
        // LP.STATE.001 实体不存在
        Asserts.That(ThrowsCode(() => s.Client.LinkGetAsync("missing")) == EngineErrors.EntityNotFound,
            "不存在的链接应报 LP.STATE.001");
        // 根不是实体、无 ID：folders.get 对根哨兵形状的 ID 报 LP.STATE.002 ROOT_NOT_ENTITY
        var rootGet = await AssertThrowsAsync(() => s.Client.FolderGetAsync("0"));
        Asserts.That(rootGet.Error.Code == EngineErrors.RootNotEntity,
            "根（哨兵形状 ID）应报 LP.STATE.002 ROOT_NOT_ENTITY");
        // LP.VAL.001 缺必填
        Asserts.That(ThrowsCode(() => s.Client.FolderCreateAsync("")) == EngineErrors.RequiredParam,
            "空名称应报 LP.VAL.001");
        // LP.SYS.001 未知命令 / LP.SYS.002 方法形态错配
        Asserts.That(ThrowsCode(() => s.Client.ExecuteAsync<LinkDto>("no.such.command")) == EngineErrors.UnknownCommand,
            "未知命令应报 LP.SYS.001");
        Asserts.That(ThrowsCode(() => s.Client.ExecuteAsync<LinkDto>("links.stats")) == EngineErrors.ProtocolMalformed,
            "查询命令走写流应报 LP.SYS.002");
        Asserts.That(ThrowsCode(() => s.Client.QueryAsync<LinkDto>("links.create", new { url = "https://x.example/" })) == EngineErrors.ProtocolMalformed,
            "变更命令走读流应报 LP.SYS.002");

        Console.WriteLine("[OK] §8 错误模型：LP.STATE.001/002 · LP.VAL.001 · LP.SYS.001/002 全部命中");
    }

    private static string ThrowsCode(Func<Task> action)
    {
        try
        {
            action().GetAwaiter().GetResult();
        }
        catch (EngineException ex)
        {
            return ex.Error.Code;
        }
        throw new Exception("应抛 EngineException");
    }

    // —— §9 10k 性能门槛（方案 7.3；DEBUG 构建放宽 ×5，见 Asserts.Within）——
    private static async Task SectionPerformance(SmokeState s)
    {
        var perfDb = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpsmoke_perf_{Guid.NewGuid():N}.db");
        try
        {
            var client = ProbeEnv.CreateEngineOn(perfDb);

            // 直插 10k（50 文件夹 × 200 书签）：单事务批量，绕过命令管道（基准数据构造，非被测路径）
            var folderIds = new List<string>();
            await using (var ctx = new LinkPocketDbContext(perfDb))
            {
                for (var i = 0; i < 50; i++)
                {
                    var f = new Folder { Name = $"性能目录 {i:D2}" };
                    folderIds.Add(f.FolderId);
                    ctx.Add(f);
                }

                var links = new List<Link>(10_000);
                for (var i = 0; i < 10_000; i++)
                    links.Add(new Link
                    {
                        Url = $"https://perf.example.com/page/{i}",
                        Title = $"性能页面 #{i:D5}",
                        ListId = folderIds[i % folderIds.Count],
                    });
                ctx.AddRange(links);
                await ctx.SaveChangesAsync();
            }

            // folders.contents（10k 库全量排序，SQL 下推）< 15ms
            var sw = Stopwatch.StartNew();
            var contents = await client.QueryAsync<FolderContentsDto>("folders.contents",
                new { folder_id = folderIds[0] });
            sw.Stop();
            Asserts.That(contents.Links.Count == 200, $"目录 0 应含 200 条，实际 {contents.Links.Count}");
            Asserts.Within(sw.ElapsedMilliseconds, 15, "10k 库 folders.contents");

            // search.links（LIKE 全扫描）< 100ms
            sw = Stopwatch.StartNew();
            var hits = await client.QueryAsync<List<LinkDto>>("search.links", new { query = "性能页面 #0999" });
            sw.Stop();
            Asserts.That(hits.Count == 10, $"「#0999」应命中 10 条，实际 {hits.Count}");
            Asserts.Within(sw.ElapsedMilliseconds, 100, "10k 库 search.links");

            // links.move_batch(10) < 50ms（移动到独立目标目录，规避同目录语义干扰）
            var target = (await client.FolderCreateAsync("性能目标目录")).Data!;
            var tenPage = await client.QueryAsync<LinkPocket.Modules.Links.PagedLinkResult>("links.query", new
            {
                filter = new object[] { new { field = "url", op = "starts", value = "https://perf.example.com/page/999" } },
                page = new { index = 1, size = 10 },
            });
            var ten = tenPage.Items.OfType<LinkDto>().Select(l => l.LinkId).ToList();
            Asserts.That(ten.Count == 10, "应取到 10 条待移动链接");
            sw = Stopwatch.StartNew();
            var moveResult = await client.ExecuteAsync<LinkPocket.Api.LinkBatchResult>("links.move_batch",
                new { link_ids = ten, target_list_id = target.FolderId });
            sw.Stop();
            Asserts.That(moveResult.Data!.Affected == 10, "批量移动应处理 10 条");
            Asserts.Within(sw.ElapsedMilliseconds, 50, "10k 库 links.move_batch(10)");

            // bookmarks.import(10k) < 5s
            var htmlPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpsmoke_perf_{Guid.NewGuid():N}.html");
            var html = new System.Text.StringBuilder("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<DL><p>\n");
            for (var i = 0; i < 10_000; i++)
                html.Append("    <DT><A HREF=\"https://import.example.com/p/").Append(i)
                    .Append("\" ADD_DATE=\"1600000000\">导入页面 #").Append(i).Append("</A>\n");
            html.Append("</DL><p>\n");
            await File.WriteAllTextAsync(htmlPath, html.ToString(), new System.Text.UTF8Encoding(false));

            sw = Stopwatch.StartNew();
            var imported = await client.ExecuteAsync<JsonElement>("bookmarks.import", new { file_path = htmlPath });
            sw.Stop();
            Asserts.That(imported.Data!.GetProperty("links_created").GetInt32() == 10_000,
                "10k 导入应产出 10000 条书签");
            Asserts.Within(sw.ElapsedMilliseconds, 5_000, "10k 书签导入");

            // 缓存命中路径（阶段 12）：同一个 10k 库上冷查询 vs 命中
            await MeasureCacheLatency(client);

            File.Delete(htmlPath);
            Console.WriteLine("[OK] §9 10k 性能门槛：contents / search / move_batch(10) / import(10k) / 缓存命中 全部达标");
        }
        finally
        {
            ProbeEnv.TryDelete(perfDb);
        }

        GC.KeepAlive(s);
    }
}
