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

    /// <summary>跨进程变更流（读附属库审计尾）：外部进程写库的**精确变更**（谁、什么命令、改了哪些实体、发了哪些事件）。
    /// 界面宿主用它做精确失效与精确提示；命令行/网关自身是写入方，一般不用它。</summary>
    public IChangeFeed? ChangeFeed => Composition.ChangeFeed;

    /// <summary>外部宿主可见的自描述清单（86 条：67 业务 + 16 编排 + 3 批；不含 3 条 UI 专用 search.session_*）。</summary>
    public EngineManifest Manifest { get; }

    /// <summary>按名索引的命令描述符。</summary>
    public IReadOnlyDictionary<string, CommandDescriptor> Commands { get; }

    /// <summary>能力桥客户端：桌面宿主开着就直连；没开则真正需要浏览器能力时按需拉起能力宿主。</summary>
    private readonly CapabilityBridgeClient _capabilityBridge;
    private readonly object _hookGate = new();
    private bool _hooksSaved;
    private Func<string, CancellationToken, Task<RenderedPageMetadata?>>? _previousRenderer;
    private Func<string, CancellationToken, Task<RenderedPageMetadata?>>? _previousInteractive;
    private Func<string, CancellationToken, Task<byte[]?>>? _previousSvg;
    private Func<CancellationToken, Task<bool>>? _previousEnsure;

    /// <param name="databasePath">库文件路径；null = 按统一发现规则自动解析（见 <see cref="AppPaths.ResolveDatabasePath(string?, out string)"/>）。</param>
    /// <param name="logToStderr">是否装配日志管道（stderr JSONL + 内存环）。缺省 false（LpLog 为空实现，不落盘不刷屏）。</param>
    public EngineHost(string? databasePath = null, bool logToStderr = false)
    {
        DatabasePath = AppPaths.ResolveDatabasePath(databasePath, out var source);
        DatabaseSource = source;

        var directory = Path.GetDirectoryName(DatabasePath)!;
        Directory.CreateDirectory(directory);

        if (logToStderr) _log = EngineComposer.ConfigureHeadlessLogging();

        Composition = EngineComposer.Compose(DatabasePath, new ComposeOptions
        {
            // 外部宿主（CLI / MCP）不注册界面专用的搜索快照会话：外部命令默认全量，
            // search.session_* 是有状态 UI 内部实现，不进入外部工具面。
            IncludeSearchSessions = false,
            // ⚠️ **不与 WPF 宿主共用撤销日志**：共用会有两个真问题——
            // ① 两个进程并发写同一个文件会互相覆盖（丢更新）；
            // ② WPF 宿主启动时把日志载入内存后，本进程追加的条目它看不见，
            //    它下一次保存就把这些条目**抹掉**（表现为"命令行撤过的东西，界面里 Ctrl+Z 又冒出来"）。
            // 因此命令行/网关有**自己的**一条撤销栈：跨 CLI 调用仍可撤（文件持久化），
            // 与界面的 Ctrl+Z 互不干扰（各自撤各自做过的）。见 EXTERNAL-AGENT「并发契约」。
            UndoJournalPath = Path.Combine(directory, "undo-journal.cli.json"),
        });

        Catalog = OrchestrationHost.CreateCatalog(Composition.Registry);
        Manifest = Catalog.Manifest();
        Commands = Manifest.Commands.ToDictionary(c => c.Name, StringComparer.Ordinal);

        // 无头宿主复用同一份浏览器能力（同一数据库 = 同一条管道）。
        // 桌面宿主开着 → 启动时直接接上；没开 → 先把"按需装配"入口交出去，
        // 真正走到渲染 / SVG / 人机验证时再启动能力宿主。装配失败就保持钩子为空，
        // 命令结果会如实上报能力不可用，绝不假装窗口已经弹过。
        _capabilityBridge = new CapabilityBridgeClient(DatabasePath);
        _previousEnsure = PageRenderHook.EnsureProvider;
        PageRenderHook.EnsureProvider = EnsureCapabilitiesAsync;

        if (_capabilityBridge.TryConnectNow())
        {
            AttachCapabilityHooks();
            LpLog.Info(
                $"capability bridge attached (render={_capabilityBridge.RenderAvailable}, " +
                $"verify={_capabilityBridge.VerifyAvailable}, svg={_capabilityBridge.SvgAvailable})",
                category: "host.capability");
        }
        else
        {
            LpLog.Debug("no capability provider at startup; attach on demand", "host.capability");
        }
    }

    private async Task<bool> EnsureCapabilitiesAsync(CancellationToken ct)
    {
        if (!await _capabilityBridge.EnsureAsync(ct).ConfigureAwait(false)) return false;
        AttachCapabilityHooks();
        return true;
    }

    /// <summary>按**当前**握手结果挂载可用能力；缺失的能力恢复原钩子（重连后能力可能变少）。</summary>
    private void AttachCapabilityHooks()
    {
        lock (_hookGate)
        {
            if (!_hooksSaved)
            {
                _previousRenderer = PageRenderHook.Renderer;
                _previousInteractive = PageRenderHook.InteractiveRenderer;
                _previousSvg = FaviconCache.SvgRasterizer;
                _hooksSaved = true;
            }

            PageRenderHook.Renderer = _capabilityBridge.RenderAvailable
                ? _capabilityBridge.RenderAsync
                : _previousRenderer;
            PageRenderHook.InteractiveRenderer = _capabilityBridge.VerifyAvailable
                ? _capabilityBridge.VerifyAsync
                : _previousInteractive;
            FaviconCache.SvgRasterizer = _capabilityBridge.SvgAvailable
                ? _capabilityBridge.RasterizeAsync
                : _previousSvg;
        }
    }

    public CommandDescriptor? Descriptor(string command) => Commands.GetValueOrDefault(command);

    public void Dispose()
    {
        // 恢复原钩子（同一进程多宿主 / 测试串联时不留全局污染）。
        lock (_hookGate)
        {
            if (_hooksSaved)
            {
                PageRenderHook.Renderer = _previousRenderer;
                PageRenderHook.InteractiveRenderer = _previousInteractive;
                FaviconCache.SvgRasterizer = _previousSvg;
                _hooksSaved = false;
            }
        }
        PageRenderHook.EnsureProvider = _previousEnsure;
        _capabilityBridge.Dispose();
        _log?.Dispose();
    }
}
