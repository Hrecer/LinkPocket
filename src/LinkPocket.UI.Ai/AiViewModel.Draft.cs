using LinkPocket.Contracts;

namespace LinkPocket.UI.Ai;

/// <summary>
/// AI 页 VM 的**草稿新建**面。
///
/// <para><b>要解决的事</b>：过去「新建会话」直接就建一条真会话并落盘——点 N 次就有 N 条空会话躺在左栏，
/// 既没内容也没标题。正确的语义是：新建只是**准备好一个待用的会话载体（草稿）**，用户真发出第一条消息
/// 时才把它变成正式会话。这就是「草稿（deferred）」。</para>
///
/// <para><b>三件事合起来才安全</b>：
/// ① 草稿**不落盘、不进列表**（引擎侧保证，见 <see cref="AiSessionPersistence"/>）；
/// ② 界面侧**单飞**——没有绑定的会话时，反复点「新建」只会复用**同一个**草稿，不再每次建一个新的；
/// ③ 草稿**无人用时回收**——换会话 / 删最后一条 / 关页时丢弃那些从没被提升过的草稿，保证不留空壳。</para>
///
/// <para><b>与引用的实现（参考实现）的对应</b>：参考实现的 <c>promotionState</c>（draft→pending→promoted→discarded）
/// 与本类的 <see cref="DraftState"/> 一一对应；参考实现的 <c>PREWARM_RETRY_DELAYS_MS</c> 有界退避重试、
/// <c>cleanupTimer</c>（setTimeout 0 延迟确认）、<c>blockedInvalidationVersion</c>（失败后阻塞同代重建）
/// 都是为了让「同步重挂 / 快速切换」不产生 create→delete→create 的抖动。本类按同样语义实现：
/// 创建**在飞**时不重复发；创建**失败**时按退避重试有界次；**pending**（首发已发出、结果未知）绝不回收。</para>
/// </summary>
public sealed partial class AiViewModel
{
    /// <summary>草稿生命周期（与参考实现 promotionState 同义）。</summary>
    private enum DraftState
    {
        /// <summary>已建、未提升，可安全回收。</summary>
        Draft = 0,

        /// <summary>首发命令已发出、结果未知——**不许回收**（否则会关掉正在用的会话）。</summary>
        Pending = 1,

        /// <summary>已提升为正式会话——不属于草稿，永不回收。</summary>
        Promoted = 2,

        /// <summary>已失效（订阅错误 / 被拒）——不回收到引擎（会话多半已不存在），也不复用。</summary>
        Discarded = 3,
    }

    /// <summary>创建失败后的有界退避节奏：瞬态失败（换工作区、反复点）隔一会儿重试就能成。</summary>
    private static readonly int[] DraftRetryDelaysMs = [500, 1000, 2000];

    private readonly object _draftGate = new();

    /// <summary>当前草稿上下文（null = 没有草稿）。</summary>
    private DraftContext? _draft;

    /// <summary>创建失败计数（-1 = 没在重试；用尽 <see cref="DraftRetryDelaysMs"/> 后回落"无草稿"）。</summary>
    private int _draftRetryAttempt = -1;

    /// <summary>草稿的代（换代 = 回收 + 重建；只有换代才允许重建，防 create/delete 抖动）。</summary>
    private int _draftGeneration;

    /// <summary>已阻塞重建的代（创建失败后阻塞，避免同代立刻重排）。</summary>
    private int _draftBlockedGeneration = -1;

    private sealed class DraftContext(string sessionId, int generation)
    {
        public string SessionId { get; } = sessionId;
        public int Generation { get; } = generation;

        /// <summary>创建是否已收口（ACK 到达）。未收口时 dispose 要等它落地再决定删不删。</summary>
        public bool Settled { get; set; }

        public DraftState State { get; set; } = DraftState.Draft;
    }

    // ── 对外：新建会话 = 拿到（或复用）一个草稿 ────────────────

    /// <summary>
    /// 新建会话。**反复点都安全**：没有绑定的会话时复用同一个草稿，绝不产生第二条空会话；
    /// 草稿不进左栏列表（列表只有正式会话）。用户发出第一条消息时草稿才提升为正式会话。
    /// </summary>
    public async Task NewSessionAsync()
    {
        try
        {
            // 已经在一个正式会话里？那就是"从当前会话出发开新草稿"——先丢掉旧的未用草稿再预热。
            var draft = await EnsureDraftAsync().ConfigureAwait(true);
            if (draft is null) return;

            // 草稿态：没有可投影的会话内容，对话区是空白（空态），会话列表不动。
            if (_activeSessionId == draft.SessionId) return;
            _activeSessionId = draft.SessionId;
            ClearUsage();   // 进草稿即清用量读数（环与上下文面板一起退场；读数由 RefreshUsageAsync 的草稿闸门守着）
            Raise(nameof(ActiveSessionId));
            ClearConversationContent();
            Mode = _mode;   // 草稿模式取偏好缺省（引擎建草稿时已定），这里保持投影一致
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>
    /// 取当前草稿；没有就建一个（单飞）。创建在飞时不重复发；创建失败有界退避重试。
    /// 返回 null = 暂时拿不到草稿（已用尽重试 / 本代已阻塞）；调用侧按"无草稿"回落，不报错。
    /// </summary>
    private async Task<DraftContext?> EnsureDraftAsync()
    {
        lock (_draftGate)
        {
            if (_draft is { State: DraftState.Draft or DraftState.Pending }) return _draft;
            if (_draftBlockedGeneration == _draftGeneration) return null;   // 本代已失败并阻塞：不重排
        }

        var generation = _draftGeneration;
        DraftContext created;
        try
        {
            // 草稿只在内存（引擎侧 CreateSessionAsync 建的就是 deferred）：不落盘、不进列表。
            var summary = await _assistant.CreateSessionAsync().ConfigureAwait(true);
            created = new DraftContext(summary.SessionId, generation) { Settled = true };
        }
        catch (AiException ex)
        {
            ScheduleDraftRetry(generation);
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
            return null;
        }

        lock (_draftGate)
        {
            // 期间换代了（换会话 / 切走）：这次创建的草稿作废并回收，避免遗留。
            if (_draftGeneration != generation)
            {
                _ = _assistant.DiscardDraftSessionAsync(created.SessionId);
                return null;
            }
            _draftRetryAttempt = -1;
            _draft = created;
            return created;
        }
    }

    /// <summary>创建失败：按有界退避重排一次（用尽即"回落无草稿"，不无限重试）。</summary>
    private void ScheduleDraftRetry(int generation)
    {
        int attempt;
        lock (_draftGate)
        {
            attempt = _draftRetryAttempt + 1;
            if (attempt >= DraftRetryDelaysMs.Length)
            {
                _draftBlockedGeneration = generation;   // 用尽：阻塞本代，避免同代立刻重排
                return;
            }
            _draftRetryAttempt = attempt;
        }

        // 有界退避：瞬态失败（环境抖动）隔一会儿重试；乱序到达的旧代重试自我作废。
        var delay = DraftRetryDelaysMs[attempt];
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            lock (_draftGate)
            {
                if (_draftGeneration != generation || _draft is not null) return;
            }
            _ = EnsureDraftAsync();
        }, TaskScheduler.Default);
    }

    // ── 内部：草稿生命周期（提升 / 回收）─────────────────────

    /// <summary>首发提升：草稿已被引擎提升为正式会话，界面标记它（此后一律不回收）。</summary>
    private void PromoteDraft(string sessionId)
    {
        lock (_draftGate)
        {
            if (_draft is { } draft && draft.SessionId == sessionId) draft.State = DraftState.Promoted;
        }
    }

    /// <summary>
    /// 作废当前草稿并换代：下一个 <see cref="EnsureDraftAsync"/> 会建新草稿。
    /// **只有从未提升的 draft 才自动回收**；pending（首发在飞、结果未知）必须等它收口后由
    /// <see cref="PromoteDraft"/> 落定，绝不在这里删——否则会关掉正在运行的会话（参考实现的同理约束）。
    /// </summary>
    private void RetireDraft()
    {
        DraftContext? retiring;
        lock (_draftGate)
        {
            retiring = _draft;
            _draft = null;
            _draftGeneration++;
            _draftRetryAttempt = -1;
            if (_draftBlockedGeneration >= 0 && _draftBlockedGeneration != _draftGeneration)
                _draftBlockedGeneration = -1;
        }

        if (retiring is null) return;
        if (retiring.State == DraftState.Draft && retiring.Settled)
        {
            // 从未提升的草稿：静默回收（引擎侧幂等，正式会话无论如何都不会被它删掉）。
            _ = _assistant.DiscardDraftSessionAsync(retiring.SessionId);
        }
    }

    /// <summary>草稿是否仍无人使用且当前不在其中（切到正式会话 / 回到空态时用）。</summary>
    private void RetireDraftIfUnbound()
    {
        lock (_draftGate)
        {
            if (_draft is null) return;
            // 当前正"停在"这个草稿上（空态下用户在草稿里打字）——不回收。
            if (_activeSessionId == _draft.SessionId && _draft.State == DraftState.Draft) return;
            if (_draft.State is DraftState.Promoted) return;
        }
        RetireDraft();
    }
}
