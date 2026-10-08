using System.Text.Json;
using LinkPocket.Composition;
using LinkPocket.Contracts;

namespace LinkPocket.Cli;

/// <summary>
/// 命令行执行器：把一次 argv 跑成"输出 + 退出码"。
///
/// <para><b>为什么它是 public 的进程内 API</b>：外部 Agent 网关（<c>LinkPocket.Mcp</c>）在进程内复用它——
/// 外部 Agent 的每次工具调用都走**与人在终端里敲命令完全相同的一条执行路径**（同样的参数绑定、
/// 同样的引擎管道、同样的 snake_case 输出），所以不存在"外部阉割版"，也没有第二套命令实现。</para>
///
/// <para><b>能力面</b>：引擎目录里的每一条命令（业务 60 + 编排 16 + 批 3）都可直接调用——
/// 与界面、内置助手同一条管道（校验 → 幂等 → 能力门 → 写闸 → 事务 → 撤销 → 事件 → 审计），
/// 因此撤销 / 审计 / 事件 / 确认令牌对命令行调用同样生效。</para>
/// </summary>
public static partial class CliRunner
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitUsage = 2;
    public const int ExitConfirmRequired = 3;

    /// <summary>不接值的开关（缺省视为布尔真）。</summary>
    private static readonly string[] BoolFlags =
        ["json", "dry-run", "yes", "help", "h", "version", "v", "verbose", "quiet", "all"];

    /// <summary>全局选项名：绑定引擎入参时跳过（它们不是命令参数）。</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "db", "json", "dry-run", "yes", "help", "h", "version", "v", "verbose", "quiet",
        "all", "out", "args", "args-file", "category",
    };

    /// <summary>产品版本（取契约程序集的程序集版本，与仓库 Directory.Build.props 的 &lt;Version&gt; 同源）。</summary>
    public static string ProductVersion => typeof(IEngine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// 执行一次命令行调用。
    /// </summary>
    /// <param name="args">argv（不含程序名）。</param>
    /// <param name="output">结果落点（stdout）。</param>
    /// <param name="error">诊断落点（stderr）——协议消费者可据此保持 stdout 纯净。</param>
    /// <param name="host">已装配的宿主；null = 由本次调用按 <c>--db</c> 自建并在结束时释放（长驻调用方应自持一个复用）。</param>
    /// <param name="caller">调用方身份（外部 Agent 网关传 <see cref="CallerKind.Agent"/>；null = 宿主自有调用）。</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        EngineHost? host = null, CallerRef? caller = null)
    {
        var parsed = CliArgs.Parse(args, BoolFlags);

        if (parsed.Flag("version") || parsed.Flag("v"))
        {
            output.WriteLine($"linkpocket {ProductVersion}");
            return ExitOk;
        }

        if (parsed.Positional.Count == 0)
        {
            Help(output, null);
            return ExitOk;
        }

        var head = parsed.Positional[0].ToLowerInvariant();
        if (parsed.Flag("help") || parsed.Flag("h") || head == "help")
        {
            Help(output, parsed.Positional.Count > 1 ? parsed.Positional[1] : null);
            return ExitOk;
        }

        var ownsHost = host is null;
        var active = host;
        try
        {
            if (active is null)
            {
                active = new EngineHost(parsed.Value("db"), logToStderr: parsed.Flag("verbose"));
            }
            if (!parsed.Flag("quiet"))
            {
                error.WriteLine($"# library: {active.DatabasePath} ({active.DatabaseSource})");
            }

            return await DispatchAsync(parsed, head, active, output, error, caller).ConfigureAwait(false);
        }
        catch (EngineException ex)
        {
            return RenderEngineError(ex, parsed, error);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"error: {ex.Message}");
            return ExitUsage;
        }
        catch (Exception ex)
        {
            error.WriteLine($"error: {ex.Message}");
            return ExitError;
        }
        finally
        {
            if (ownsHost) active?.Dispose();
        }
    }

    private static async Task<int> DispatchAsync(CliArgs parsed, string head, EngineHost host,
        TextWriter output, TextWriter error, CallerRef? caller)
    {
        switch (head)
        {
            case "describe":
                return Describe(parsed, host, output);
            case "docs":
                output.WriteLine(host.Catalog.Export(ManifestFormat.MarkdownDocs));
                return ExitOk;
            case "call":
                return await CallAsync(parsed, host, output, error, caller).ConfigureAwait(false);
            case "snapshot":
                return await SnapshotAsync(parsed, host, output, error, caller).ConfigureAwait(false);
            case "dump":
            case "export":
                return await DumpAsync(parsed, host, output, error, caller).ConfigureAwait(false);
            default:
            {
                var command = ResolveCommand(host, parsed.Positional) ?? string.Join(".", parsed.Positional);
                return await InvokeAsync(parsed, command, host, output, error, caller).ConfigureAwait(false);
            }
        }
    }

    /// <summary>列出命令目录（engine.describe 的同一份数据：79 条 = 业务 + 编排 + 批）。</summary>
    private static int Describe(CliArgs parsed, EngineHost host, TextWriter output)
    {
        var rest = parsed.Positional.Skip(1).ToList();
        var category = parsed.Value("category") ?? (rest.Count > 0 ? rest[0] : null);
        var manifest = host.Catalog.Manifest(category);

        if (parsed.Flag("json"))
        {
            output.WriteLine(ResultText.ToText(JsonSerializer.SerializeToElement(manifest, ResultText.Options)));
            return ExitOk;
        }

        output.WriteLine($"# LinkPocket command catalog - {manifest.Commands.Count} commands");
        foreach (var group in manifest.Commands.GroupBy(c => c.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine();
            output.WriteLine($"## {group.Key}");
            foreach (var command in group)
            {
                var kind = command.IsQuery ? "query" : command.IsDestructive ? "mutation!" : "mutation";
                var parameters = string.Join(", ", command.Parameters.Select(p => p.Required ? p.Name : p.Name + "?"));
                output.WriteLine($"  {command.Name,-24} {kind,-10} {parameters}");
            }
        }
        return ExitOk;
    }

    /// <summary>万能调用：命令名 + JSON 入参（<c>--args</c> / <c>--args-file</c>），也可与 <c>--key value</c> 混用。</summary>
    private static async Task<int> CallAsync(CliArgs parsed, EngineHost host,
        TextWriter output, TextWriter error, CallerRef? caller)
    {
        var rest = parsed.Positional.Skip(1).ToList();
        if (rest.Count == 0)
        {
            error.WriteLine("error: usage: linkpocket call <command> [--args '<json>'] [--key value ...]");
            return ExitUsage;
        }

        var command = ResolveCommand(host, rest) ?? string.Join(".", rest);
        var descriptor = host.Descriptor(command);
        if (descriptor is null) return UnknownCommand(command, host, error);

        JsonElement argEl;
        var raw = parsed.Value("args") ?? ReadTextFile(parsed.Value("args-file"));
        if (raw is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                argEl = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                error.WriteLine($"error: --args is not valid JSON: {ex.Message}");
                return ExitUsage;
            }
        }
        else
        {
            argEl = ArgBinder.Bind(descriptor, parsed, Reserved);
        }

        return await ExecuteAsync(command, descriptor, argEl, parsed, host, output, error, caller).ConfigureAwait(false);
    }

    /// <summary>直接命令名调用（<c>linkpocket links.list --per_page 20</c> 或 <c>linkpocket links list ...</c>）。</summary>
    private static Task<int> InvokeAsync(CliArgs parsed, string command, EngineHost host,
        TextWriter output, TextWriter error, CallerRef? caller)
    {
        var descriptor = host.Descriptor(command);
        if (descriptor is null) return Task.FromResult(UnknownCommand(command, host, error));

        var argEl = ArgBinder.Bind(descriptor, parsed, Reserved);
        return ExecuteAsync(command, descriptor, argEl, parsed, host, output, error, caller);
    }

    private static async Task<int> ExecuteAsync(string command, CommandDescriptor descriptor, JsonElement argEl,
        CliArgs parsed, EngineHost host, TextWriter output, TextWriter error, CallerRef? caller)
    {
        var callerRef = caller ?? CallerRef.Ui;

        if (descriptor.IsQuery)
        {
            try
            {
                var options = new CallOptions(Caller: callerRef);
                var data = await host.Client.QueryAsync<object>(command, argEl, options).ConfigureAwait(false);
                RenderQuery(data, parsed, output, error);
                return ExitOk;
            }
            catch (EngineException ex)
            {
                return RenderEngineError(ex, parsed, error);
            }
        }

        try
        {
            var options = new CallOptions(DryRun: parsed.Flag("dry-run"), Caller: callerRef);
            var result = await host.Client.ExecuteAsync<object>(command, argEl, options).ConfigureAwait(false);
            RenderMutation(result, parsed, output, error);
            return ExitOk;
        }
        catch (EngineException ex) when (ex.Error.Code == EngineErrors.ConfirmRequired)
        {
            return await ConfirmAsync(command, descriptor, argEl, ex, parsed, host, output, error, callerRef)
                .ConfigureAwait(false);
        }
        catch (EngineException ex)
        {
            return RenderEngineError(ex, parsed, error);
        }
    }

    /// <summary>
    /// 破坏性命令的两阶段确认（引擎侧令牌 60s 一次性）：不带 <c>--yes</c> 时只报影响面并退出（退出码 3），
    /// 带上 <c>--yes</c> 才拿引擎签发的令牌重调。外部 Agent 网关据其"无需确认"的口径自行追加 <c>--yes</c>。
    /// </summary>
    private static async Task<int> ConfirmAsync(string command, CommandDescriptor descriptor, JsonElement argEl,
        EngineException confirm, CliArgs parsed, EngineHost host, TextWriter output, TextWriter error, CallerRef callerRef)
    {
        var details = confirm.Error.Details;
        var impact = details is { } d && d.TryGetProperty("impact", out var i) ? i.GetString() : descriptor.Category;
        var token = details is { } d2 && d2.TryGetProperty("confirm_token", out var t) ? t.GetString() : null;

        error.WriteLine($"! destructive command: {command} (impact: {impact})");
        if (!parsed.Flag("yes"))
        {
            error.WriteLine("  re-run with --yes to execute, or --dry-run to preview the impact with no side effect.");
            return ExitConfirmRequired;
        }
        if (token is null)
        {
            error.WriteLine("error: the engine did not issue a confirmation token.");
            return ExitError;
        }

        try
        {
            var options = new CallOptions(DryRun: parsed.Flag("dry-run"), ConfirmToken: token, Caller: callerRef);
            var result = await host.Client.ExecuteAsync<object>(command, argEl, options).ConfigureAwait(false);
            RenderMutation(result, parsed, output, error);
            return ExitOk;
        }
        catch (EngineException ex)
        {
            return RenderEngineError(ex, parsed, error);
        }
    }

    /// <summary>把若干段位置参数解析成目录里真实存在的命令名（最长前缀优先）。</summary>
    private static string? ResolveCommand(EngineHost host, IReadOnlyList<string> tokens)
    {
        for (var take = tokens.Count; take >= 1; take--)
        {
            var candidate = string.Join(".", tokens.Take(take));
            if (host.Commands.ContainsKey(candidate)) return candidate;
        }
        return null;
    }

    private static int UnknownCommand(string command, EngineHost host, TextWriter error)
    {
        error.WriteLine($"error: unknown command '{command}'");
        var domain = command.Contains('.') ? command[..command.IndexOf('.')] : command;
        var siblings = host.Commands.Keys
            .Where(name => name.StartsWith(domain + ".", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(12)
            .ToList();
        if (siblings.Count > 0) error.WriteLine($"hint: commands in that domain: {string.Join(", ", siblings)}");
        error.WriteLine("hint: run 'linkpocket describe' for the full command list, 'linkpocket docs' for the reference.");
        return ExitUsage;
    }
}
