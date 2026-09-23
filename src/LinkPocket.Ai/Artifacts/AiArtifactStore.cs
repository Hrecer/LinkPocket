namespace LinkPocket.Ai;

/// <summary>
/// 大工具结果存储（**唯一实现**）：<c>{数据根}/sessions/&lt;会话ID&gt;.data/&lt;调用ID&gt;.json</c>。
/// 用途（功能书 §6.5）：超过预算的工具结果**不灌进模型上下文**，落盘后只回灌摘要 + 可追溯引用。
/// </summary>
public sealed class AiArtifactStore
{
    /// <summary>内联上限（字符）：超过则落盘，模型只看到摘要。</summary>
    public const int InlineLimitChars = 32 * 1024;

    private readonly string _root;

    public AiArtifactStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _root = Path.Combine(dataRoot, "sessions");
    }

    /// <summary>写（返回相对引用：<c>&lt;会话ID&gt;.data/&lt;调用ID&gt;.json</c>）。</summary>
    public string Write(string sessionId, string callId, string content)
    {
        var path = PathOf(sessionId, callId);
        AtomicFile.WriteAllText(path, content);
        return Path.Combine(sessionId + ".data", callId + ".json");
    }

    /// <summary>读（不存在 → null）。</summary>
    public string? Read(string sessionId, string callId)
    {
        var path = PathOf(sessionId, callId);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>删整个会话的大结果目录（不存在 = 幂等成功）。</summary>
    public void DeleteSession(string sessionId)
    {
        var dir = Path.Combine(_root, AiSessionStore.EnsureSafeId(sessionId) + ".data");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private string PathOf(string sessionId, string callId)
    {
        var safeSession = AiSessionStore.EnsureSafeId(sessionId);
        var safeCall = AiSessionStore.EnsureSafeId(callId);
        return Path.Combine(_root, safeSession + ".data", safeCall + ".json");
    }
}
