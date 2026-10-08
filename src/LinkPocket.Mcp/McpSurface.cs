using System.Text.Json;
using System.Text.Json.Nodes;
using LinkPocket.Cli;
using LinkPocket.Composition;
using LinkPocket.Contracts;

namespace LinkPocket.Mcp;

/// <summary>
/// 网关暴露的两个面：**工具**（来自契约层 <see cref="AiToolCatalog"/> 的命令目录）与**资源**（只读数据视图）。
///
/// <para>两者都**不自持任何命令实现**：一律翻译成 <c>LinkPocket.Cli</c> 的命令行再执行——
/// 与人在终端里敲的是同一条路径（同样的参数绑定、同样的引擎管道、同样的 snake_case 输出）。</para>
/// </summary>
internal sealed class McpSurface
{
    /// <summary>外部 Agent 的调用方身份：不带 SessionId = 宿主自有调用，零能力门约束（与 CLI 一致）；
    /// 仅用于审计/日志里把调用方标成 agent。写入冻结（内置助手改数据期间）对它同样生效。</summary>
    private static readonly CallerRef AgentCaller = new(CallerKind.Agent);

    private readonly EngineHost _host;
    private readonly AiToolCatalog _tools;

    public McpSurface(EngineHost host)
    {
        _host = host;
        _tools = new AiToolCatalog(host.Manifest);
    }

    // ─────────────────────────── tools ───────────────────────────

    /// <summary>工具清单 = 引擎全部命令（业务 + 编排 + 批），与内置助手同一份工具定义。</summary>
    public object ToolsList()
    {
        var tools = _tools.BuildAll()
            .Select(spec => (object)new Dictionary<string, object?>
            {
                ["name"] = spec.Name,
                ["description"] = spec.Description,
                ["inputSchema"] = WithDryRun(spec.ParametersJson),
                ["annotations"] = Annotations(spec.Name),
            })
            .ToList();

        return new Dictionary<string, object?> { ["tools"] = tools };
    }

    public async Task<object> ToolsCallAsync(JsonElement parameters)
    {
        var name = String(parameters, "name")
            ?? throw new McpException(-32602, "tools/call requires a string 'name'");
        if (_host.Descriptor(name) is null)
            throw new McpException(-32602, $"unknown tool: {name}. Call tools/list for the available names.");

        var args = parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("arguments", out var a)
            && a.ValueKind == JsonValueKind.Object
                ? a
                : default;

        var (text, isError) = await RunCliAsync(ToolArgv(name, args)).ConfigureAwait(false);

        var result = new Dictionary<string, object?>
        {
            ["content"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = text },
            },
        };
        if (isError) result["isError"] = true;
        return result;
    }

    /// <summary>工具入参 → 命令行参数：<c>{"list_id":"F1","per_page":5}</c> ⇒ <c>--list_id F1 --per_page 5</c>；
    /// 数组/对象按 JSON 字面量传（CLI 的绑定器认这种形态）；<c>_dry_run</c> 翻译成 <c>--dry-run</c>。</summary>
    private static List<string> ToolArgv(string command, JsonElement args)
    {
        var argv = new List<string> { command };
        if (args.ValueKind != JsonValueKind.Object) return argv;

        foreach (var property in args.EnumerateObject())
        {
            if (property.Name == "_dry_run")
            {
                if (property.Value.ValueKind == JsonValueKind.True) argv.Add("--dry-run");
                continue;
            }
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;   // 空值 = 取引擎缺省
            argv.Add("--" + property.Name);
            argv.Add(ArgumentText(property.Value));
        }
        return argv;
    }

    private static string ArgumentText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };

    private object Annotations(string command)
    {
        var descriptor = _host.Descriptor(command);
        return new Dictionary<string, object?>
        {
            ["readOnlyHint"] = descriptor?.IsQuery ?? false,
            ["destructiveHint"] = descriptor?.IsDestructive ?? false,
            ["idempotentHint"] = false,
            ["openWorldHint"] = descriptor?.Caps.HasFlag(CommandCaps.NetworkOutsideGate) ?? false,
        };
    }

    /// <summary>给每条命令的入参 schema 补一个 <c>_dry_run</c>（空跑：执行但不提交，返回将发生什么）。</summary>
    private static JsonNode WithDryRun(string parametersJson)
    {
        var schema = JsonNode.Parse(parametersJson)?.AsObject() ?? new JsonObject();
        schema["type"] = "object";
        if (schema["properties"] is not JsonObject properties)
        {
            properties = new JsonObject();
            schema["properties"] = properties;
        }
        properties["_dry_run"] = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = "Preview only: run the command without committing and return what would change.",
        };
        return schema;
    }

    // ─────────────────────────── resources ───────────────────────────

    private static readonly (string Uri, string Name, string Description, string Mime)[] Resources =
    [
        ("linkpocket://docs", "Command reference", "Every engine command with its parameters and capabilities (markdown).", "text/markdown"),
        ("linkpocket://commands", "Command manifest", "Machine-readable list of all commands.", "application/json"),
        ("linkpocket://snapshot", "Full snapshot", "Folder tree, all links, trash, statistics and diagnostics in one JSON document.", "application/json"),
        ("linkpocket://stats", "Statistics", "Link counts by folder, trash size and root-level count.", "application/json"),
        ("linkpocket://tree", "Folder tree", "All folders with parent id and link counts.", "application/json"),
        ("linkpocket://links", "All links", "Every link with its folder id.", "application/json"),
        ("linkpocket://trash", "Trash", "Deleted folders and links with their original locations.", "application/json"),
        ("linkpocket://diagnostics", "Diagnostics", "Versions, table counts and cache readings.", "application/json"),
    ];

    private static readonly (string UriTemplate, string Name, string Description)[] Templates =
    [
        ("linkpocket://link/{id}", "One link", "A single link by id (links.get)."),
        ("linkpocket://folder/{id}", "One folder page", "Folders and links directly inside a folder (folders.contents)."),
        ("linkpocket://trash/{id}", "One trash unit", "Contents of a deleted folder unit (trash.unit_contents)."),
    ];

    public object ResourcesList() => new Dictionary<string, object?>
    {
        ["resources"] = Resources.Select(r => (object)new Dictionary<string, object?>
        {
            ["uri"] = r.Uri,
            ["name"] = r.Name,
            ["description"] = r.Description,
            ["mimeType"] = r.Mime,
        }).ToList(),
    };

    public object ResourceTemplates() => new Dictionary<string, object?>
    {
        ["resourceTemplates"] = Templates.Select(t => (object)new Dictionary<string, object?>
        {
            ["uriTemplate"] = t.UriTemplate,
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["mimeType"] = "application/json",
        }).ToList(),
    };

    public async Task<object> ResourcesReadAsync(JsonElement parameters)
    {
        var uri = String(parameters, "uri")
            ?? throw new McpException(-32602, "resources/read requires a string 'uri'");
        var argv = ResourceArgv(uri) ?? throw new McpException(-32602, $"unknown resource: {uri}");

        var (text, isError) = await RunCliAsync(argv).ConfigureAwait(false);
        if (isError) throw new McpException(-32603, text);

        return new Dictionary<string, object?>
        {
            ["contents"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["uri"] = uri,
                    ["mimeType"] = uri == "linkpocket://docs" ? "text/markdown" : "application/json",
                    ["text"] = text,
                },
            },
        };
    }

    private static List<string>? ResourceArgv(string uri) => uri switch
    {
        "linkpocket://docs" => ["docs"],
        "linkpocket://commands" => ["describe", "--json"],
        "linkpocket://snapshot" => ["snapshot"],
        "linkpocket://stats" => ["links.stats", "--json"],
        "linkpocket://tree" => ["folders.tree", "--json"],
        "linkpocket://links" => ["links.query", "--page", "{\"index\":1,\"size\":0}"],
        "linkpocket://trash" => ["trash.overview", "--json"],
        "linkpocket://diagnostics" => ["diagnostics.collect", "--json"],
        _ when uri.StartsWith("linkpocket://link/", StringComparison.Ordinal) =>
            ["links.get", "--id", uri["linkpocket://link/".Length..], "--json"],
        _ when uri.StartsWith("linkpocket://folder/", StringComparison.Ordinal) =>
            ["folders.contents", "--folder_id", uri["linkpocket://folder/".Length..], "--json"],
        _ when uri.StartsWith("linkpocket://trash/", StringComparison.Ordinal) =>
            ["trash.unit_contents", "--id", uri["linkpocket://trash/".Length..], "--json"],
        _ => null,
    };

    // ─────────────────────────── CLI execution ───────────────────────────

    /// <summary>
    /// 一次工具/资源调用 = 一次命令行执行：<c>--json</c> 给结构化输出、<c>--yes</c> 免确认（授权即挂载）、
    /// <c>--quiet</c> 收掉库路径回显。stdout 取正文；失败时把 stderr 当正文（引擎错误如实回给 Agent）。
    /// </summary>
    private async Task<(string Text, bool IsError)> RunCliAsync(IEnumerable<string> commandArgv)
    {
        var argv = new List<string> { "--db", _host.DatabasePath, "--json", "--yes", "--quiet" };
        argv.AddRange(commandArgv);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliRunner.RunAsync(argv.ToArray(), stdout, stderr, _host, AgentCaller).ConfigureAwait(false);

        var text = stdout.ToString().Trim();
        if (exit != CliRunner.ExitOk)
            return (text.Length > 0 ? text : stderr.ToString().Trim(), true);
        return (text.Length > 0 ? text : "(no output)", false);
    }

    private static string? String(JsonElement parameters, string name)
        => parameters.ValueKind == JsonValueKind.Object
           && parameters.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
