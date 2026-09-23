using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// AI 运行时的装配入口（**只被组合根调用**）：把 HTTP 传输、两协议适配、四份存储、
/// 工具目录与引擎消费者门面组装成 <see cref="IAiAssistant"/>。
/// 构造**不读任何文件**（各存储只解析路径）——损坏文件在真正读取时如实暴露，不在启动期炸。
/// </summary>
public static class AiRuntime
{
    /// <summary>缺省数据根：<c>{程序目录}/ai</c>（与 db / 日志 / 界面偏好同目录；不进库、不进备份）。</summary>
    public static string DefaultDataRoot => Path.Combine(AppContext.BaseDirectory, "ai");

    /// <param name="dataRoot">AI 数据根（测试传临时目录）。</param>
    /// <param name="client">引擎客户端（AI 只经它读写）。</param>
    /// <param name="engineSessions">引擎会话管理器（限流 / 只读 / 写入冻结的权威）。</param>
    /// <param name="transport">HTTP 传输（缺省 = 唯一实现 <see cref="HttpAiTransport"/>；测试注入假传输层）。</param>
    public static IAiAssistant Create(string dataRoot, EngineClient client, ISessionManager engineSessions,
        IAiHttpTransport? transport = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(engineSessions);

        var credentials = new AiCredentialStore(dataRoot, new DpapiSecretCipher());
        var providers = new AiProviderStore(dataRoot);
        var preferences = new AiPreferenceStore(dataRoot);
        var sessions = new AiSessionStore(dataRoot);
        var catalog = new AiToolCatalog(client.Describe());
        return new AiAssistant(client, engineSessions, catalog, providers, credentials, preferences, sessions,
            transport ?? new HttpAiTransport());
    }
}
