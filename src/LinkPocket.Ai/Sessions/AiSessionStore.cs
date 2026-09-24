using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>会话文件形状（**可变聚合**：回合循环在原地更新，持久化时整体序列化）。
/// <c>Chat</c> 是回灌模型的唯一历史来源，其余是界面投影。</summary>
public sealed class AiSessionFile
{
    public int Version { get; set; } = AiSessionStore.CurrentVersion;
    public AiSessionSummary Summary { get; set; } = new("", "", AiMode.ConfirmEach, null, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, null);
    public List<AiMessage> Messages { get; init; } = [];
    public List<AiToolCall> ToolCalls { get; init; } = [];
    public List<AiChange> Changes { get; init; } = [];
    public List<AiApproval> Approvals { get; init; } = [];
    public List<AiTurn> Turns { get; init; } = [];
    public List<AiChatMessage> Chat { get; init; } = [];

    /// <summary>摘要连续失败计数（熔断；成功一次清零）——随会话文件持久化，重启不重置。</summary>
    public int CompactFailures { get; set; }

    public static AiSessionFile Create(AiSessionSummary summary) => new() { Summary = summary };
}

/// <summary>
/// 会话存储（**唯一实现**）：<c>{数据根}/sessions/&lt;会话ID&gt;.json</c>（大结果在同目录 <c>&lt;会话ID&gt;.data/</c>）。
/// 口径：写入 = 同目录临时文件 + 原子替换；列表 = **扫目录**（不维护索引文件——少一个会漂移的事实源，
/// 会话量级为个人使用，读文件头开销可接受）；损坏 / 版本不符 → <c>LP.AI.013</c>（如实暴露，不覆盖）。
/// <para><b>草稿（draft / deferred）</b>：只活在内存字典 <see cref="_drafts"/> 里，**不落盘**——所以永远不会
/// 以空会话的形态出现在左栏（列表是扫目录的，草稿不在目录里）。首个回合开始前由
/// <see cref="Promote"/> 提升（= 落盘 + 从草稿表移除）。进程结束即全部消失，符合草稿语义。</para>
/// </summary>
public sealed class AiSessionStore
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly string _root;
    private readonly object _gate = new();

    /// <summary>内存草稿表（会话ID → 文件聚合）。**草稿绝不落盘**，见类型注释。</summary>
    private readonly Dictionary<string, AiSessionFile> _drafts = new(StringComparer.Ordinal);

    public AiSessionStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _root = Path.Combine(dataRoot, "sessions");
        Artifacts = new AiArtifactStore(dataRoot);
    }

    /// <summary>大工具结果落盘（超预算结果不进模型上下文，只回灌摘要 + 引用）。</summary>
    public AiArtifactStore Artifacts { get; }

    public string StoreDirectory => _root;

    /// <summary>会话清单（按最后活动倒序）。文件损坏 → 如实抛（LP.AI.013），不静默跳过。
    /// **只含正式会话**：草稿在内存、不在目录里，天然不出现。</summary>
    public IReadOnlyList<AiSessionSummary> List()
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(_root)) return [];
            return System.IO.Directory.EnumerateFiles(_root, "*.json")
                .Select(path => LoadFile(path).Summary)
                .OrderByDescending(s => s.UpdatedAt)
                .ToArray();
        }
    }

    /// <summary>读一个会话：先查内存草稿（草稿未落盘），再查磁盘（不存在 → null；损坏 / 版本不符 → LP.AI.013）。</summary>
    public AiSessionFile? Load(string sessionId)
    {
        var path = PathOf(sessionId);
        lock (_gate)
        {
            if (_drafts.TryGetValue(sessionId, out var draft)) return draft;
            return File.Exists(path) ? LoadFile(path) : null;
        }
    }

    /// <summary>新建草稿（**不落盘**）：进内存草稿表，直到 <see cref="Promote"/> 才写文件。</summary>
    public void CreateDraft(AiSessionFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var sessionId = EnsureSafeId(file.Summary.SessionId);
        file.Summary = file.Summary with { Persistence = AiSessionPersistence.Deferred };
        lock (_gate) _drafts[sessionId] = file;
    }

    /// <summary>草稿是否已在内存中（未提升）。</summary>
    public bool IsDraft(string sessionId)
    {
        EnsureSafeId(sessionId);
        lock (_gate) return _drafts.ContainsKey(sessionId);
    }

    /// <summary>丢弃草稿（幂等；只删内存项，**绝不删磁盘文件**——正式会话走 <see cref="Delete"/>）。</summary>
    public void DiscardDraft(string sessionId)
    {
        EnsureSafeId(sessionId);
        lock (_gate) _drafts.Remove(sessionId);
    }

    /// <summary>
    /// 提升草稿为正式会话（**落盘**；已在磁盘的正式会话 = 幂等 no-op）。
    /// 提升后从草稿表移除——此后读写都走磁盘这条唯一路径。
    /// </summary>
    public void Promote(string sessionId)
    {
        EnsureSafeId(sessionId);
        lock (_gate)
        {
            if (!_drafts.TryGetValue(sessionId, out var draft)) return;   // 已是正式会话 / 不存在：no-op
            draft.Summary = draft.Summary with { Persistence = AiSessionPersistence.Immediate };
            _drafts.Remove(sessionId);
            var json = JsonSerializer.Serialize(draft, JsonOptions);
            AtomicFile.WriteAllText(PathOf(sessionId), json);
        }
    }

    /// <summary>
    /// 写一个会话（原子替换；UpdatedAt 由调用方维护）。**草稿写不落盘**——仍在草稿表里原地更新，
    /// 只有提升后才真正写文件（否则草稿语义就破了）。
    /// </summary>
    public void Save(AiSessionFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var sessionId = EnsureSafeId(file.Summary.SessionId);
        file.Version = CurrentVersion;
        lock (_gate)
        {
            if (_drafts.ContainsKey(sessionId))
            {
                // 草稿态：只更新内存里的聚合，不落盘（提升时才会写文件）。
                file.Summary = file.Summary with { Persistence = AiSessionPersistence.Deferred };
                return;
            }
            var json = JsonSerializer.Serialize(file, JsonOptions);
            AtomicFile.WriteAllText(PathOf(sessionId), json);
        }
    }

    /// <summary>删除会话（文件 + 大结果目录 + 内存草稿；不存在 = 幂等成功）。</summary>
    public void Delete(string sessionId)
    {
        var path = PathOf(sessionId);
        lock (_gate)
        {
            _drafts.Remove(sessionId);
            if (File.Exists(path)) File.Delete(path);
            Artifacts.DeleteSession(sessionId);
        }
    }

    /// <summary>会话 ID 形状校验（防路径穿越：ID 只许 [A-Za-z0-9_-]，由本层统一生成）。</summary>
    public static string EnsureSafeId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new AiException(AiErrors.Of(AiErrors.SessionStoreFailed,
                "invalid session id",
                details: JsonSerializer.SerializeToElement(new { session_id = sessionId })));
        return sessionId;
    }

    private string PathOf(string sessionId)
    {
        // EnsureSafeId 已禁掉一切路径分隔符与非法字符（ID 只许 [A-Za-z0-9_-]），无需再查整条路径
        EnsureSafeId(sessionId);
        return Path.Combine(_root, sessionId + ".json");
    }

    private static AiSessionFile LoadFile(string path)
    {
        AiSessionFile? file;
        try
        {
            file = JsonSerializer.Deserialize<AiSessionFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            throw Corrupt(path);
        }

        if (file is null || file.Version != CurrentVersion || file.Summary is null
            || file.Messages is null || file.ToolCalls is null || file.Changes is null
            || file.Approvals is null || file.Turns is null || file.Chat is null)
            throw Corrupt(path);
        return file;
    }

    private static AiException Corrupt(string path)
        => new(AiErrors.Of(AiErrors.SessionStoreFailed, "session file is corrupt or version-mismatched",
            details: JsonSerializer.SerializeToElement(new { file = Path.GetFileName(path) })));
}
