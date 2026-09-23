using System.Text.Json;
using LinkPocket.Contracts;

namespace ProtocolSmoke;

/// <summary>冒烟主流程：目录自描述 → wire → 数据流 → 回收站 → 书签/备份往返 → 引擎能力 → 并发 → 错误模型 → 性能 → 编排层 → 缓存与增量。</summary>
internal static partial class SmokeRunner
{
    public static async Task RunAsync()
    {
        var state = SmokeState.Create();
        try
        {
            await SectionCatalog(state);
            await SectionWire(state);
            await SectionDataFlow(state);
            await SectionTrash(state);
            await SectionBookmarks(state);
            await SectionBackup(state);
            await SectionEngineAbilities(state);
            await SectionConcurrency(state);
            await SectionErrorModel(state);
            await SectionPerformance(state);
            await SectionOrchestration(state);
            await SectionCacheAndInvalidation(state);
            Console.WriteLine("全部通过");
        }
        finally
        {
            ProbeEnv.TryDelete(state.DbPath);
        }
    }

    // —— §0 目录自描述：79 命令（60 模块 + 16 编排 + 3 批）、分类、能力标志 ——
    private static Task SectionCatalog(SmokeState s)
    {
        var manifest = s.Client.Describe();
        // 批三命令不进注册表（wire 直路由），但批引擎已装配 → Describe 与目录导出同口径并入
        // （否则进程内消费者（AI / EngineClient）的工具清单会缺掉 batch.*，见 AI-ASSISTANT §3.3）
        Asserts.That(manifest.Commands.Count == 79, $"目录应有 79 条命令，实际 {manifest.Commands.Count}");
        Asserts.That(manifest.Commands.Select(c => c.Name).Distinct().Count() == 79, "命令名不得重复");
        Asserts.That(manifest.Commands.All(c => System.Text.RegularExpressions.Regex.IsMatch(c.Name, @"^[a-z_]+\.[a-z_]+$")),
            "命令名必须是 域.动作 形态");

        int Count(string cat) => manifest.Commands.Count(c => c.Category == cat);
        Asserts.That(Count("folders") == 14 && Count("links") == 16 && Count("trash") == 9
            && Count("search") == 2 && Count("bookmarks") == 3 && Count("backup") == 3
            && Count("dedup") == 3 && Count("favicon") == 2 && Count("maintenance") == 3
            && Count("locate") == 1 && Count("audit") == 2 && Count("logs") == 2,
            "业务域的命令数与目录总表不一致");
        Asserts.That(Count("macro") == 5 && Count("undo") == 5 && Count("staging") == 6,
            "编排域命令数不正确（macro 5 / undo 5 / staging 6）");
        Asserts.That(Count("batch") == 3, "批命令应为 3 条（batch.run / batch.dry_run / batch.status）");

        var destructive = manifest.Commands.Where(c => c.IsDestructive).Select(c => c.Name).ToHashSet();
        Asserts.That(destructive.SetEquals(["trash.purge", "trash.purge_batch", "maintenance.reinit", "backup.import", "audit.prune"]),
            $"破坏性命令应为 purge/purge_batch/reinit/import/audit.prune，实际 {string.Join(", ", destructive)}");

        var queries = manifest.Commands.Where(c => c.IsQuery).ToList();
        Asserts.That(queries.All(c => !c.IsMutation) && manifest.Commands.Count(c => c.IsMutation) == 79 - queries.Count,
            "Query/Mutation 互斥且每条命令必有其一");

        Console.WriteLine("[OK] §0 目录自描述：79 命令（60 模块 + 16 编排 + 3 批）/ 十六域 / 破坏性标志");
        return Task.CompletedTask;
    }

    // —— §1 wire 层：JSON-RPC 2.0 往返 + 直接命令名 + 错误通道 ——
    private static async Task SectionWire(SmokeState s)
    {
        // engine.describe
        var describe = await s.Wire.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"engine.describe","params":{}}""");
        var doc = JsonDocument.Parse(describe);
        Asserts.That(doc.RootElement.TryGetProperty("result", out var result), "describe 应返回 result");
        Asserts.That(result.GetProperty("commands").GetArrayLength() == 79, "describe 应含 79 条命令（含批三命令）");
        Asserts.That(doc.RootElement.GetProperty("id").GetInt32() == 1, "响应应回显请求 id");

        // engine.query（直接命令名同效）
        var stats = await s.Wire.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"engine.query","params":{"command":"links.stats"}}""");
        Asserts.That(JsonDocument.Parse(stats).RootElement.GetProperty("result").GetProperty("total").GetInt32() == 0,
            "空库 links.stats.total 应为 0");
        var statsDirect = await s.Wire.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"links.stats"}""");
        Asserts.That(JsonDocument.Parse(statsDirect).RootElement.GetProperty("result").GetProperty("total").GetInt32() == 0,
            "直接命令名应路由到查询");

        // engine.execute：result = { ok, data, changes, audit_ref }
        var create = await s.Wire.HandleAsync(
            """{"jsonrpc":"2.0","id":4,"method":"engine.execute","params":{"command":"folders.create","args":{"name":"wire目录"}}}""");
        var created = JsonDocument.Parse(create).RootElement.GetProperty("result");
        Asserts.That(created.GetProperty("ok").GetBoolean(), "execute result.ok 应为 true");
        Asserts.That(created.GetProperty("data").GetProperty("name").GetString() == "wire目录", "execute result.data 应为 FolderDto");
        Asserts.That(created.GetProperty("changes").GetProperty("events").EnumerateArray()
                .Any(e => e.GetString() == "folders.changed"),
            "execute result.changes.events 应含 folders.changed");
        Asserts.That(created.TryGetProperty("audit_ref", out _), "execute result 应含 audit_ref");

        // 错误通道：未知命令 = -32601 + LP.SYS.001
        var unknown = JsonDocument.Parse(await s.Wire.HandleAsync(
            """{"jsonrpc":"2.0","id":5,"method":"no.such.command"}""")).RootElement.GetProperty("error");
        Asserts.That(unknown.GetProperty("code").GetInt32() == -32601, "未知命令应映射 -32601");
        Asserts.That(unknown.GetProperty("data").GetProperty("code").GetString() == EngineErrors.UnknownCommand,
            "error.data 应含结构化 EngineError（LP.SYS.001）");

        // 错误通道：校验失败 = -32602 + LP.VAL.001
        var missing = JsonDocument.Parse(await s.Wire.HandleAsync(
            """{"jsonrpc":"2.0","id":6,"method":"engine.execute","params":{"command":"folders.create","args":{}}}""")).RootElement.GetProperty("error");
        Asserts.That(missing.GetProperty("code").GetInt32() == -32602, "校验类错误应映射 -32602");
        Asserts.That(missing.GetProperty("data").GetProperty("code").GetString() == EngineErrors.RequiredParam,
            "缺参应为 LP.VAL.001");

        // 错误通道：非法请求体 = -32600
        var bad = JsonDocument.Parse(await s.Wire.HandleAsync("not json")).RootElement.GetProperty("error");
        Asserts.That(bad.GetProperty("code").GetInt32() == -32600, "非法请求体应映射 -32600");

        Console.WriteLine("[OK] §1 wire 层：engine.execute/query/describe + 直接命令名 + 错误码映射");
    }
}
