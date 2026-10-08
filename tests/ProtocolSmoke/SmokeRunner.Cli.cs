using System.Text.Json;
using LinkPocket.Cli;
using LinkPocket.Composition;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using LinkPocket.Mcp;

namespace ProtocolSmoke;

/// <summary>
/// §12 命令行客户端与外部 Agent 网关：命令行绑定/执行/空跑/两阶段确认 + MCP 协议帧。
/// 断言的是"外部通道与界面、内置助手同层"这件事：同一份命令目录、同一条引擎管道、同一套输出形状。
/// </summary>
internal static partial class SmokeRunner
{
    private static async Task SectionCliAndGateway(SmokeState s)
    {
        // 本节点用独立临时库（不扰动前面各节的数据状态），自建自清。
        var dbPath = Path.Combine(TempArea.Resolve(), $"lpsmoke_cli_{Guid.NewGuid():N}.db");
        var undoJournal = Path.Combine(Path.GetDirectoryName(dbPath)!, "undo-journal.json");
        using var host = new EngineHost(dbPath);
        var server = McpServer.Create(host);

        try
        {
            await CliSection(host);
            await GatewaySection(server, host);
            Console.WriteLine("[OK] §12 命令行与网关：命令行直调/两段写法/空跑/两阶段确认 + MCP 初始化/工具面/工具调用/资源读取");
        }
        finally
        {
            ProbeEnv.TryDelete(dbPath);
            try { if (File.Exists(undoJournal)) File.Delete(undoJournal); } catch { /* 尽力而为 */ }
        }
    }

    // —— §12.1 命令行：与界面对同一条引擎管道，全部命令可直调 ——
    private static async Task CliSection(EngineHost host)
    {
        // 两段写法（folders create）与一段写法（folders.tree）等价
        var created = await RunCliAsync(host, "folders", "create", "--name", "cli-smoke-folder", "--json");
        Asserts.That(created.Exit == CliRunner.ExitOk, $"folders.create 应成功，退出码 {created.Exit}：{created.Error}");
        Asserts.That(created.Output.Contains("cli-smoke-folder", StringComparison.Ordinal),
            "命令行应回出被创建实体的字段");

        var tree = await RunCliAsync(host, "folders.tree", "--json");
        Asserts.That(tree.Output.Contains("cli-smoke-folder", StringComparison.Ordinal),
            "folders.tree 应能看到刚建的目录");

        // 引擎把型号不匹配的入参按真实值处理并报明确错误码（LP.VAL.002），命令行如实透出
        var badArg = await RunCliAsync(host, "links.list", "--per_page", "abc");
        Asserts.That(badArg.Exit == CliRunner.ExitUsage, $"类型不符应退出码 2，实际 {badArg.Exit}");
        Asserts.That(badArg.Error.Contains("LP.VAL.002", StringComparison.Ordinal), "应透出引擎错误码 LP.VAL.002");

        var unknown = await RunCliAsync(host, "folders.nope");
        Asserts.That(unknown.Exit == CliRunner.ExitUsage, "未知命令应退出码 2");

        // 空跑：执行但不提交（零副作用）
        var before = await RunCliAsync(host, "folders.tree", "--json");
        var dry = await RunCliAsync(host, "folders", "create", "--name", "cli-smoke-dry", "--dry-run", "--json");
        Asserts.That(dry.Exit == CliRunner.ExitOk, $"干跑应成功，退出码 {dry.Exit}：{dry.Error}");
        var after = await RunCliAsync(host, "folders.tree", "--json");
        Asserts.That(before.Output == after.Output, "干跑不得产生任何副作用");

        // 破坏性命令两阶段确认：无 --yes 只报影响面（退出码 3），带 --yes 才执行
        var unconfirmed = await RunCliAsync(host, "maintenance", "reinit");
        Asserts.That(unconfirmed.Exit == CliRunner.ExitConfirmRequired,
            $"破坏性命令未确认应退出码 3，实际 {unconfirmed.Exit}");
        Asserts.That(unconfirmed.Error.Contains("entire database", StringComparison.Ordinal),
            "未确认时应报出影响面");
        var stillThere = await RunCliAsync(host, "folders.tree", "--json");
        Asserts.That(stillThere.Output.Contains("cli-smoke-folder", StringComparison.Ordinal),
            "未确认的破坏性命令不得产生任何副作用");
    }

    // —— §12.2 外部 Agent 网关：MCP 协议帧 → 同一套命令 ——
    private static async Task GatewaySection(McpServer server, EngineHost host)
    {
        var init = await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        Asserts.That(init is not null && init.Contains(McpServer.ProtocolVersion, StringComparison.Ordinal),
            "initialize 应回出协议版本");

        // 通知不回响应（协议要求）
        var notification = await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Asserts.That(notification is null, "通知不应产生响应帧");

        var toolsList = await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        using var toolsDocument = JsonDocument.Parse(toolsList!);
        var tools = toolsDocument.RootElement.GetProperty("result").GetProperty("tools");
        Asserts.That(tools.GetArrayLength() == host.Commands.Count,
            $"工具面应与引擎目录逐条对齐（期望 {host.Commands.Count}，实际 {tools.GetArrayLength()}）");
        var linkList = tools.EnumerateArray().First(t => t.GetProperty("name").GetString() == "links.list");
        Asserts.That(linkList.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(),
            "查询命令应标注 readOnlyHint");
        Asserts.That(linkList.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("_dry_run", out _),
            "每个工具都应带 _dry_run（空跑）入口");

        // 工具调用 = 同一条命令行执行：变更走引擎管道并回出变更集
        var call = await server.HandleAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"folders.create","arguments":{"name":"mcp-smoke"}}}""");
        using var callDocument = JsonDocument.Parse(call!);
        var callText = callDocument.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Asserts.That(callText is not null && callText.Contains("\"ok\": true", StringComparison.Ordinal),
            "工具调用应回出 snake_case 的变更结果");
        Asserts.That(callText!.Contains("\"changes\"", StringComparison.Ordinal), "变更结果应带 changes 载荷");

        var tree = await RunCliAsync(host, "folders.tree", "--json");
        Asserts.That(tree.Output.Contains("mcp-smoke", StringComparison.Ordinal), "网关调用应真的改到了数据");

        // 资源 = 只读数据视图
        var resource = await server.HandleAsync(
            """{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"linkpocket://stats"}}""");
        using var resourceDocument = JsonDocument.Parse(resource!);
        var contents = resourceDocument.RootElement.GetProperty("result").GetProperty("contents")[0];
        Asserts.That(contents.GetProperty("uri").GetString() == "linkpocket://stats", "资源读取应回出原 URI");
        Asserts.That(contents.GetProperty("text").GetString()!.Contains("by_folder", StringComparison.Ordinal),
            "统计资源应回出真实数据");

        // 协议错误：未知方法与解析失败
        var unknownMethod = await server.HandleAsync("""{"jsonrpc":"2.0","id":5,"method":"nope"}""");
        using var errorDocument = JsonDocument.Parse(unknownMethod!);
        Asserts.That(errorDocument.RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32601,
            "未知方法应回 -32601");

        var parseError = await server.HandleAsync("{ not json");
        using var parseDocument = JsonDocument.Parse(parseError!);
        Asserts.That(parseDocument.RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32700,
            "非法请求体应回 -32700");
    }

    private static async Task<(int Exit, string Output, string Error)> RunCliAsync(EngineHost host, params string[] argv)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        // 与真实命令行入口同一身份（Program 传 CallerRef.ExternalAgent）：否则"外部进程写的"这件事
        // 在审计里被标成 ui，跨进程变更流就认不出来（§13 用它断言）。
        var exit = await CliRunner.RunAsync(argv, stdout, stderr, host, CallerRef.ExternalAgent);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
