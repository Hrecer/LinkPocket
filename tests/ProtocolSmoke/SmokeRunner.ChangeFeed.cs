using LinkPocket.Composition;
using LinkPocket.Contracts;
using LinkPocket.Engine;

namespace ProtocolSmoke;

/// <summary>
/// §13 跨进程变更流（精确变更）：外部进程（命令行 / 网关）写库之后，界面宿主能不能**精确**知道"改了什么"。
///
/// <para>断言口径 = 三段可观测行为：
/// ① 流的内容——外部身份的写必须出现在流里，且带**事件名**（精确失效的唯一依据）与**受影响实体**（精确提示的依据）；
/// ② 流的归属——**应用内**身份（界面 / 带会话的助手）的写**不得**进流（否则用户自己点一下也会被当成"外部变更"）；
/// ③ 精确失效——无关事件名不得清缓存，空集才整体清空（"精确是优化，正确性不依赖精确"）。</para>
/// </summary>
internal static partial class SmokeRunner
{
    private static async Task SectionExternalChangeFeed(SmokeState s)
    {
        var dbPath = Path.Combine(TempArea.Resolve(), $"lpsmoke_feed_{Guid.NewGuid():N}.db");
        var undoJournal = Path.Combine(Path.GetDirectoryName(dbPath)!, "undo-journal-cli.json");
        using var host = new EngineHost(dbPath);

        try
        {
            var feed = host.ChangeFeed;
            Asserts.That(feed is not null, "落了库的宿主必须提供跨进程变更流（读附属库审计尾）");

            // —— ① 外部身份（命令行入口）的写：必须进流，且带事件名与实体 ——
            var start = feed!.Watermark;
            var exit = (await RunCliAsync(host, "folders", "create", "--name", "feed-smoke")).Exit;
            Asserts.That(exit == 0, "命令行创建目录应成功");

            var changes = feed.Poll(100);
            var created = changes.FirstOrDefault(c => c.Command == "folders.create");
            Asserts.That(created is not null,
                $"外部进程写下的命令必须出现在变更流里（实得 {string.Join(",", changes.Select(c => c.Command))}）");
            Asserts.That(created!.Caller == CallerRef.ExternalAgent.ToString(),
                "变更行的调用方应标成外部进程身份");
            Asserts.That(created.IsEffective, "成功的顶层写入应算有效变更（可提示、可计数）");
            Asserts.That(created.Events.Count > 0, "变更必须带事件名——精确失效的唯一依据");
            Asserts.That(created.Touched.Count > 0, "变更必须带受影响实体——精确提示的依据");
            Asserts.That(feed.Watermark > start, "消费后水位必须推进");
            Asserts.That(feed.Poll(100).Count == 0, "已消费的行不得重报（否则界面会刷屏提示）");

            // —— ② 应用内身份（界面）的写：不得进流 ——
            await host.Client.FolderCreateAsync("in-app-folder");
            Asserts.That(feed.Poll(100).Count == 0,
                "应用内（ui）写入不得进外部变更流——它本就有进程内事件驱动刷新");

            // —— ③ 精确失效：无关事件名不动缓存；空集 = 整体清空 ——
            var client = host.Client;
            await client.FolderTreeAsync();
            var warm = client.Engine.RuntimeStats;
            await client.FolderTreeAsync();
            var hit = client.Engine.RuntimeStats;
            Asserts.That(hit.CacheHits == warm.CacheHits + 1, "预热后的同查询应由缓存服务（命中 +1）");

            client.InvalidateQueryCache(["nothing.changed"]);
            await client.FolderTreeAsync();
            var unrelated = client.Engine.RuntimeStats;
            Asserts.That(unrelated.CacheHits == hit.CacheHits + 1,
                "无关事件名不得动缓存（精确失效 = 只推进实际发布过的事件名）");

            client.InvalidateQueryCache();
            await client.FolderTreeAsync();
            var cleared = client.Engine.RuntimeStats;
            Asserts.That(cleared.CacheMisses == unrelated.CacheMisses + 1,
                "空集失效 = 整体清空（拿不到精确信息时的保守兜底）");

            Console.WriteLine("[OK] §13 跨进程变更流：外部写入进流（带事件名/实体）· 应用内写入不进流 · 精确失效/整体失效口径");
        }
        finally
        {
            ProbeEnv.TryDelete(dbPath);
            try { if (File.Exists(undoJournal)) File.Delete(undoJournal); } catch { /* 尽力而为 */ }
        }
    }
}
