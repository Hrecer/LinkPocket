using System.Text;
using System.Text.Json;
using LinkPocket.Composition;
using LinkPocket.Contracts;

namespace LinkPocket.Cli;

/// <summary>命令行的数据视图与渲染（<see cref="CliRunner"/> 的表现层部分）。</summary>
public static partial class CliRunner
{
    /// <summary>
    /// 一次取回全量数据视图：目录树 + 全部链接 + 回收站 + 统计 + 诊断 + schema 版本。
    /// 人或外部 Agent 一眼看全库的最短路径；子查询失败如实带 <c>error</c> 字段，不静默吞掉。
    /// </summary>
    private static async Task<int> SnapshotAsync(CliArgs parsed, EngineHost host, TextWriter output, TextWriter error, CallerRef? caller)
    {
        var callerRef = caller ?? CallerRef.Ui;
        var body = new Dictionary<string, object?>
        {
            ["generated_at"] = DateTimeOffset.Now.ToString("o"),
            ["database"] = host.DatabasePath,
            ["schema_version"] = await QueryAsync(host, "maintenance.schema_version", null, callerRef).ConfigureAwait(false),
            ["stats"] = await QueryAsync(host, "links.stats", null, callerRef).ConfigureAwait(false),
            ["folders"] = await QueryAsync(host, "folders.tree", null, callerRef).ConfigureAwait(false),
            ["links"] = await QueryAsync(host, "links.query", new { page = new { index = 1, size = 0 } }, callerRef).ConfigureAwait(false),
            ["trash"] = await QueryAsync(host, "trash.overview", null, callerRef).ConfigureAwait(false),
            ["diagnostics"] = await QueryAsync(host, "diagnostics.collect", null, callerRef).ConfigureAwait(false),
        };

        Emit(JsonSerializer.SerializeToElement(body, ResultText.Options), parsed, output, error);
        return ExitOk;
    }

    private static async Task<object?> QueryAsync(EngineHost host, string command, object? args, CallerRef caller)
    {
        try
        {
            return await host.Client.QueryAsync<object>(command, args, new CallOptions(Caller: caller)).ConfigureAwait(false);
        }
        catch (EngineException ex)
        {
            return new Dictionary<string, object?> { ["error"] = ex.Error.Code, ["message"] = ex.Error.Message };
        }
    }

    /// <summary>导出全部数据到目录：Netscape 书签 + JSON + CSV + .lpbackup 备份。</summary>
    private static async Task<int> DumpAsync(CliArgs parsed, EngineHost host, TextWriter output, TextWriter error, CallerRef? caller)
    {
        var callerRef = caller ?? CallerRef.Ui;
        var target = Path.GetFullPath(parsed.Value("out") ?? Path.Combine(Directory.GetCurrentDirectory(), "linkpocket-export"));
        Directory.CreateDirectory(target);

        var steps = new (string Command, Dictionary<string, object?> Args)[]
        {
            ("bookmarks.export", new() { ["file_path"] = Path.Combine(target, "bookmarks.html") }),
            ("links.export", new() { ["file_path"] = Path.Combine(target, "links.json"), ["format"] = "json" }),
            ("links.export", new() { ["file_path"] = Path.Combine(target, "links.csv"), ["format"] = "csv" }),
            ("backup.export", new() { ["output_path"] = Path.Combine(target, "linkpocket.lpbackup") }),
        };

        var failed = 0;
        foreach (var (command, stepArgs) in steps)
        {
            try
            {
                await host.Client.ExecuteAsync<object>(command, stepArgs, new CallOptions(Caller: callerRef)).ConfigureAwait(false);
                output.WriteLine($"  ok   {command,-18} -> {stepArgs.Values.First()}");
            }
            catch (EngineException ex)
            {
                failed++;
                error.WriteLine($"  fail {command,-18} {ex.Error.Code}: {ex.Error.Message}");
            }
        }

        output.WriteLine(failed == 0
            ? $"exported everything to {target}"
            : $"export finished with {failed} failure(s); see above. target: {target}");
        return failed == 0 ? ExitOk : ExitError;
    }

    private static void RenderQuery(object? data, CliArgs parsed, TextWriter output, TextWriter error)
        => Emit(ResultText.ToElement(data), parsed, output, error);

    private static void RenderMutation(CommandResult<object> result, CliArgs parsed, TextWriter output, TextWriter error)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = result.Ok,
            ["data"] = result.Data,
            ["audit_ref"] = result.AuditRef,
            ["changes"] = result.Changes is null ? null : ChangeSetPayload.From(result.Changes),
        };
        var element = JsonSerializer.SerializeToElement(payload, ResultText.Options);

        if (parsed.Value("out") is not null || parsed.Flag("json"))
        {
            Emit(element, parsed, output, error);
            return;
        }

        output.WriteLine(result.Ok ? "ok" : "failed");
        if (result.Changes is { } changes)
        {
            if (!string.IsNullOrEmpty(changes.HumanSummary)) output.WriteLine($"  {changes.HumanSummary}");
            var diff = changes.Diff is { Count: > 0 } d ? $" | field changes: {d.Count}" : "";
            output.WriteLine($"  affected entities: {changes.Touched.Count}{diff}");
            foreach (var warning in changes.Warnings ?? []) error.WriteLine($"  warning: {warning}");
        }
        if (result.Data is not null)
        {
            var data = ResultText.ToElement(result.Data);
            if (!ResultText.TryRenderTable(data, output, all: true)) output.WriteLine(ResultText.ToText(data));
        }
    }

    /// <summary>结果落点：<c>--out</c> 写文件（路径回显到 stderr），否则 <c>--json</c> 或不可表格化时打 JSON，否则打表格。</summary>
    private static void Emit(JsonElement element, CliArgs parsed, TextWriter output, TextWriter error)
    {
        if (parsed.Value("out") is { } file)
        {
            var full = Path.GetFullPath(file);
            File.WriteAllText(full, ResultText.ToText(element), new UTF8Encoding(false));
            error.WriteLine($"# wrote {full}");
            return;
        }

        if (!parsed.Flag("json") && ResultText.TryRenderTable(element, output, parsed.Flag("all"))) return;
        output.WriteLine(ResultText.ToText(element));
    }

    private static int RenderEngineError(EngineException ex, CliArgs parsed, TextWriter error)
    {
        var engineError = ex.Error;
        if (parsed.Flag("json"))
        {
            error.WriteLine(ResultText.ToText(JsonSerializer.SerializeToElement(engineError, ResultText.Options)));
        }
        else
        {
            error.WriteLine($"error: {engineError.Code} - {engineError.Message}");
            if (!string.IsNullOrEmpty(engineError.CorrelationId) && engineError.CorrelationId != "unknown")
                error.WriteLine($"  correlation_id: {engineError.CorrelationId}");
            if (engineError.Details is { } details) error.WriteLine($"  details: {details.GetRawText()}");
            if (engineError.Retryable) error.WriteLine("  (this error is retryable)");
        }

        return engineError.Code.StartsWith("LP.VAL", StringComparison.Ordinal) ? ExitUsage : ExitError;
    }

    private static string? ReadTextFile(string? path) => path is null ? null : File.ReadAllText(path);

    private static void Help(TextWriter output, string? topic)
    {
        if (topic is not null && topic.Contains('.'))
        {
            output.WriteLine($"usage: linkpocket {topic} [--parameter value ...]");
            output.WriteLine("  Parameter names are the descriptor's snake_case names - run 'linkpocket describe' to list them.");
            output.WriteLine($"  Complex or nested arguments: linkpocket call {topic} --args '{{ }}'");
            return;
        }

        output.WriteLine($"linkpocket {ProductVersion} - drive the LinkPocket library from a shell (same engine and database as the app)");
        output.WriteLine("""
        usage:
          linkpocket describe [category]            list every command (business / orchestration / batch)
          linkpocket <command> [--param value ...]  call a command directly
          linkpocket <domain> <action> [...]        same as above (links list == links.list)
          linkpocket call <command> --args '<json>' arbitrary arguments
          linkpocket docs                           the full command reference (markdown)
          linkpocket snapshot [--out file]          one-shot view of all data (tree + links + trash + stats)
          linkpocket dump --out <dir>               export everything (html / json / csv / .lpbackup)
          linkpocket help [command]                 help

        global options:
          --db <path>   library file (default: first linkpocket.db found walking up from the current directory)
          --json        machine-readable JSON output
          --all         do not truncate tables
          --dry-run     execute without committing (zero side effects, no confirmation needed)
          --yes         confirm destructive commands
          --out <file>  write the JSON result to a file
          --verbose     engine logs on stderr
          --version     version

        examples:
          linkpocket links stats
          linkpocket links list --list_id F123 --per_page 20 --json
          linkpocket search links --query github --json
          linkpocket folders create --name Notes
          linkpocket dedup scan --json
          linkpocket snapshot --out data.json
        """);
    }
}
