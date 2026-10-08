using LinkPocket.Contracts;
using LinkPocket.Diagnostics;
using LinkPocket.Engine;

namespace LinkPocket.Composition;

/// <summary>
/// 进程级消费者的**共享宿主引导**（CLI / 外部 Agent 网关 / 无头脚本）：
/// 库路径解析 → <see cref="EngineComposer"/> 全量装配 → 客户端门面 + 引擎目录 + 会话管理器。
///
/// <para><b>为什么在这里</b>：装配引擎是组合根的职责；库路径与"进程怎么找到它的库"是同一条装配链的
/// 第一个参数。把它收在组合根，消费者（<c>LinkPocket.Cli</c> / <c>LinkPocket.Mcp</c>）只需引用
/// <c>Composition</c> + <c>Contracts</c>，**不必引用 <c>Engine</c>/<c>Data</c>**——与 App/冒烟同一条依赖纪律。</para>
///
/// <para><b>对外只暴露契约层类型</b>（<see cref="EngineClient"/> / <see cref="IEngineCatalog"/> /
/// <see cref="ISessionManager"/> 全在 Contracts）：消费者的读写一律经客户端门面走完整引擎管道，
/// 与界面、内置助手同一条路——能力不打折、语义不漂移。</para>
/// </summary>
public sealed class EngineHost : IDisposable
{
    private readonly LogPipeline? _log;

    /// <summary>实际使用的库文件绝对路径。</summary>
    public string DatabasePath { get; }

    /// <summary>库路径来源的可读说明（argument / env / 发现 / 缺省），供界面回显与排障。</summary>
    public string DatabaseSource { get; }

    /// <summary>组合根装配结果（引擎本体 + 客户端 + 注册表 + 会话管理器等）。</summary>
    public EngineComposition Composition { get; }

    /// <summary>强类型客户端门面（一切读写的入口）。</summary>
    public EngineClient Client => Composition.Client;

    /// <summary>引擎目录（自描述导出：命令清单 / AI 工具清单 / 轻量 OpenAPI 的唯一事实源）。</summary>
    public IEngineCatalog Catalog { get; }

    /// <summary>会话管理器（能力门：只读拒绝写、agent 限流、写入冻结）。未登记会话零约束。</summary>
    public ISessionManager Sessions => Composition.Sessions;

    /// <summary>全部能力的自描述清单（79 条：业务 + 编排 + 批）。</summary>
    public EngineManifest Manifest { get; }

    /// <summary>按名索引的命令描述符。</summary>
    public IReadOnlyDictionary<string, CommandDescriptor> Commands { get; }

    /// <param name="databasePath">库文件路径；null = 自动解析（见 <see cref="ResolveDatabasePath"/>）。</param>
    /// <param name="logToStderr">是否装配日志管道（stderr JSONL + 内存环）。缺省 false（LpLog 为空实现，不落盘不刷屏）。</param>
    public EngineHost(string? databasePath = null, bool logToStderr = false)
    {
        DatabasePath = ResolveDatabasePath(databasePath, out var source);
        DatabaseSource = source;

        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (logToStderr) _log = EngineComposer.ConfigureHeadlessLogging();

        Composition = EngineComposer.Compose(DatabasePath, new ComposeOptions
        {
            // ⚠️ **不与 WPF 宿主共用撤销日志**：共用会有两个真问题——
            // ① 两个进程并发写同一个文件会互相覆盖（丢更新）；
            // ② WPF 宿主启动时把日志载入内存后，本进程追加的条目它看不见，
            //    它下一次保存就把这些条目**抹掉**（表现为"命令行撤过的东西，界面里 Ctrl+Z 又冒出来"）。
            // 因此命令行/网关有**自己的**一条撤销栈：跨 CLI 调用仍可撤（文件持久化），
            // 与界面的 Ctrl+Z 互不干扰（各自撤各自做过的）。见 EXTERNAL-AGENT「并发契约」。
            UndoJournalPath = Path.Combine(directory ?? AppContext.BaseDirectory, "undo-journal.cli.json"),
        });

        Catalog = OrchestrationHost.CreateCatalog(Composition.Registry);
        Manifest = Catalog.Manifest();
        Commands = Manifest.Commands.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// 库路径解析，优先级由高到低：
    /// ① 显式参数（命令行 <c>--db</c>）；② 环境变量 <c>LINKPOCKET_DB</c>；
    /// ③ 从当前目录向上回溯到第一个含 <c>linkpocket.db</c> 的目录（命令在库目录树里就地执行时无需传参）；
    /// ④ 程序目录 <c>linkpocket.db</c>（与 WPF 宿主同一约定：同目录即同一个库，零迁移）。
    /// </summary>
    public static string ResolveDatabasePath(string? explicitPath, out string source)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            source = "argument";
            return Path.GetFullPath(explicitPath.Trim());
        }

        var env = Environment.GetEnvironmentVariable("LINKPOCKET_DB");
        if (!string.IsNullOrWhiteSpace(env))
        {
            source = "env";
            return Path.GetFullPath(env.Trim());
        }

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "linkpocket.db");
            if (File.Exists(candidate))
            {
                source = "search";
                return candidate;
            }
            dir = dir.Parent;
        }

        source = "default";
        return Path.Combine(AppContext.BaseDirectory, "linkpocket.db");
    }

    public CommandDescriptor? Descriptor(string command) => Commands.GetValueOrDefault(command);

    public void Dispose() => _log?.Dispose();
}
