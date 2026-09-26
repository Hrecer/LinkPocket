using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LinkPocket.Contracts;
using LinkPocket.I18n;

namespace LinkPocket.UI.Ai;

/// <summary>
/// 用量环悬浮面板里的一行分项（来源名 + 估算 token + 占比条宽度）。
/// <paramref name="Percent"/> = 该项占上下文总量的百分比（0–100），直接当宽度用。
/// <paramref name="ToneIndex"/> = 取色档位（按降序 0 起；参照实现是同一强调色的五档深浅）。
/// </summary>
public sealed record AiContextSourceRow(LocValue Label, int Tokens, double Percent, int ToneIndex)
{
    /// <summary>右侧读数（占比百分比，一位小数；走文案键，别在 C# 里拼格式串）。</summary>
    public LocValue PercentValue => Loc.K("ai.usage.panel.percent", Percent);
}

/// <summary>
/// 对话流里的一行（回合分隔行 / 消息 / 工具调用行 / 审批卡 / 提示条）：同一 VM、按 Kind 选模板。
/// 时间线是**平铺**的一条列表：回合分隔行也是一行，其后的条目按 TurnId 归属它（折叠 = 该轮条目隐藏）。
/// </summary>
public sealed class AiFeedItem : INotifyPropertyChanged
{
    public enum ItemKind
    {
        UserMessage = 0,
        AssistantMessage = 1,
        ToolCall = 2,
        Approval = 3,
        Notice = 4,
        TurnHeader = 5,
    }

    /// <summary>流式期间的 Markdown 重渲染节流：内容进来得再密，最多这么久重排一次；收尾立即整篇渲染。</summary>
    private static readonly TimeSpan RenderThrottle = TimeSpan.FromMilliseconds(150);

    private string _text = "";
    private string _renderText = "";
    private string _reasoningText = "";
    private string _reasoningRender = "";
    private DateTimeOffset _lastReasoningAt;
    private bool _isReasoningOpen;
    private DateTimeOffset _lastRenderAt;
    private string _stateKey = "";
    private string _stateSuffix = "";
    private bool _isStreaming;
    private bool _isApprovalOpen;
    private bool _isExpanded;
    private bool _isCollapsed;
    private bool _hiddenByTurn;

    // 回合分隔行的可变读数（随 TurnChanged 通知更新；运行中的"已用 N 秒"由视图每秒 Tick 一次）
    private AiTurnState _turnState = AiTurnState.Pending;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _endedAt;
    private int _changeCount;
    private string _previewText = "";

    public required ItemKind Kind { get; init; }
    public required string ItemId { get; init; }
    public string? TurnId { get; init; }
    public DateTimeOffset At { get; init; }
    public string Command { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Detail { get; private set; } = "";
    public string? ApprovalId { get; init; }
    public bool IsDestructive { get; init; }
    public AiApproval? Approval { get; private set; }
    public AiToolCall? ToolCall { get; private set; }

    /// <summary>提示条的文案值（系统提示不是用户数据：走键 + 参数，渲染边界取词）。</summary>
    public LocValue NoticeValue { get; init; } = LocValue.Empty;

    /// <summary>该行的时刻（消息 / 工具行 / 审批卡 / 回合头都有；渲染边界取词）。</summary>
    public LocValue TimeValue => LocValue.Clock(At.LocalDateTime, false);

    private readonly List<AiChangeRow> _changes = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Text
    {
        get => _text;
        private set
        {
            if (_text == value) return;
            _text = value;
            Raise(nameof(Text));
        }
    }

    /// <summary>Markdown 渲染面（助手消息用）：流式期间节流跟进，收尾整篇渲染。</summary>
    public string RenderText
    {
        get => _renderText;
        private set
        {
            if (_renderText == value) return;
            _renderText = value;
            Raise(nameof(RenderText));
            // 正文到达即恢复可见：收起回合时该条目还没有正文（被判成"纯思考工作段"整条藏起），
            // 之后正文流式进来——**最终回复永远不许被折叠藏住**，此时必须把它从隐藏里捞回来。
            if (Kind == ItemKind.AssistantMessage && HiddenByTurn && !string.IsNullOrWhiteSpace(value))
                HiddenByTurn = false;
        }
    }

    /// <summary>
    /// 模型思考原文（reasoning / thinking）。**只给人看**：不回灌模型、不进上下文预算。
    /// 流式期间按 <see cref="RenderThrottle"/> 节流跟进（思考常有几万字，逐 delta 重排会白烧 UI 线程），
    /// <see cref="Finalize"/> 补一次全文。
    /// </summary>
    public string ReasoningText
    {
        get => _reasoningRender;
        private set
        {
            if (_reasoningRender == value) return;
            _reasoningRender = value;
            // 初始化器直接给投影面时（重开会话），原文也要跟着立起来，否则 HasReasoning 会是假
            if (_reasoningText.Length == 0) _reasoningText = value;
            Raise(nameof(ReasoningText));
            Raise(nameof(HasReasoning));
            Raise(nameof(ReasoningLengthValue));
        }
    }

    /// <summary>思考块是否露面（没有思考的模型 / 未开启思考 → 整块不占位）。</summary>
    public bool HasReasoning => _reasoningText.Length > 0;

    /// <summary>思考块是否展开（缺省收起：它是参考材料，不是正文；要看原文再点开）。</summary>
    public bool IsReasoningOpen
    {
        get => _isReasoningOpen;
        private set
        {
            if (_isReasoningOpen == value) return;
            _isReasoningOpen = value;
            Raise(nameof(IsReasoningOpen));
            Raise(nameof(ReasoningToggleKey));
        }
    }

    public void ToggleReasoning() => IsReasoningOpen = !IsReasoningOpen;

    /// <summary>展开 / 收起的按钮文案键。</summary>
    public string ReasoningToggleKey => IsReasoningOpen ? "ai.reasoning.collapse" : "ai.reasoning.expand";

    /// <summary>收起时显示的读数（"思考 N 字"，让人知道里面有多少东西）。</summary>
    public LocValue ReasoningLengthValue => Loc.K("ai.reasoning.chars", _reasoningText.Length);

    /// <summary>思考增量（原文立即累积；投影面按节流跟进）。</summary>
    public void AppendReasoning(string chunk)
    {
        _reasoningText += chunk;
        Raise(nameof(HasReasoning));
        var now = DateTimeOffset.UtcNow;
        if (now - _lastReasoningAt < RenderThrottle) return;
        _lastReasoningAt = now;
        ReasoningText = _reasoningText;
    }

    /// <summary>状态文案键（工具行用；如 ai.tool.state.running）。</summary>
    public string StateKey
    {
        get => _stateKey;
        private set
        {
            if (_stateKey == value) return;
            _stateKey = value;
            Raise(nameof(StateKey));
        }
    }

    /// <summary>状态附加文本（耗时 / 错误码等机器面信息，原样显示）。</summary>
    public string StateSuffix
    {
        get => _stateSuffix;
        private set
        {
            if (_stateSuffix == value) return;
            _stateSuffix = value;
            Raise(nameof(StateSuffix));
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (_isStreaming == value) return;
            _isStreaming = value;
            Raise(nameof(IsStreaming));
        }
    }

    /// <summary>审批卡是否仍可操作（已作决定 → 收起按钮）。</summary>
    public bool IsApprovalOpen
    {
        get => _isApprovalOpen;
        private set
        {
            if (_isApprovalOpen == value) return;
            _isApprovalOpen = value;
            Raise(nameof(IsApprovalOpen));
        }
    }

    private bool _isRejectFeedbackOpen;
    /// <summary>拒绝反馈区是否展开（**点「拒绝」才出现**——说明是可选的，不是必须输入的确认框；
    /// 常驻的输入框会让用户以为"必须打字才能执行"，实测正是误读来源）。setter 公开：TwoWay 绑定。</summary>
    public bool IsRejectFeedbackOpen
    {
        get => _isRejectFeedbackOpen;
        set
        {
            if (_isRejectFeedbackOpen == value) return;
            _isRejectFeedbackOpen = value;
            Raise(nameof(IsRejectFeedbackOpen));
        }
    }

    private string _rejectReason = "";
    /// <summary>拒绝时附给模型的一句说明（可选；空 = 只拒绝不给理由）。setter 公开：TwoWay 绑定。</summary>
    public string RejectReason
    {
        get => _rejectReason;
        set
        {
            if (_rejectReason == value) return;
            _rejectReason = value;
            Raise(nameof(RejectReason));
        }
    }

    /// <summary>工具行的展开态（一行摘要 → 参数 / 结果 / 内联变更卡）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Raise(nameof(IsExpanded));
        }
    }

    public void ToggleExpand() => IsExpanded = !IsExpanded;

    /// <summary>工具行 / 域图标（命令域 → 字形键；未收录域回落通用工具图标）。</summary>
    public string IconKind => Command.Length > 0 ? LinkPocket.Views.AiKeyMap.Icon(Command) : "wrench-outline";

    /// <summary>机器面入参（等宽显示；空对象不占位）。</summary>
    public string ArgsText => ToolCall?.ArgsJson ?? "";
    public bool HasArgs => ArgsText.Length > 0 && ArgsText != "{}";

    public string? ErrorCode => ToolCall?.ErrorCode;
    public bool HasError => ErrorCode is { Length: > 0 };
    public bool IsDryRun => ToolCall?.IsDryRun == true;

    /// <summary>用户消息携带的 @提及（气泡上方的 chip 行；身份是 ID，名称只是显示）。</summary>
    public IReadOnlyList<AiMentionRef> Mentions { get; init; } = [];
    public bool HasMentions => Mentions.Count > 0;

    /// <summary>被所属回合折叠隐藏（折叠只影响显示，不删任何数据）。</summary>
    public bool HiddenByTurn
    {
        get => _hiddenByTurn;
        private set
        {
            if (_hiddenByTurn == value) return;
            _hiddenByTurn = value;
            Raise(nameof(HiddenByTurn));
        }
    }

    /// <summary>
    /// 助手条目的**思考块**是否随回合折叠隐藏（条目本体与正文永远常显）。
    /// </summary>
    /// <remarks>
    /// 助手条目里其实有两段身份不同的东西：**思考过程**（工作段，属于"这一轮干了什么"）与
    /// **最终回复**（正文，属于"AI 说了什么"）。前者要随回合收起，后者不许被收起——
    /// 所以不能靠"整条 Collapsed"来表达折叠：那要么把正文一起藏了，要么（排除整条后）连思考过程都收不掉
    ///（实测症状：收起一轮只藏了工具行，三段"思考过程"还留在屏幕上）。
    /// </remarks>
    public bool HideReasoningByTurn
    {
        get => _hideReasoningByTurn;
        private set
        {
            if (_hideReasoningByTurn == value) return;
            _hideReasoningByTurn = value;
            Raise(nameof(HideReasoningByTurn));
        }
    }

    private bool _hideReasoningByTurn;

    /// <summary>随所属回合的折叠而显隐（助手条目走 <see cref="HideReasoningByTurn"/>，见其说明）。</summary>
    public void SetHiddenByTurn(bool hidden)
    {
        if (Kind == ItemKind.AssistantMessage)
        {
            // 助手条目里的两段身份不同：**最终正文**永远常显（收起一轮收的是"干了什么"，不是"说了什么"）；
            // **思考过程**属于工作段，随回合收起。
            // ⚠️ 有一种条目**只有思考、没有正文**（模型一轮里分段思考，每段是独立条目）——
            // 它整个就是工作段：只藏"条目内的思考块"会留下一串 16px 的空壳占位
            //（实测：收起后三个"思考过程"行看着"藏了但没收走"）⇒ 整条随回合收起。
            var hasBody = !string.IsNullOrWhiteSpace(RenderText);
            HiddenByTurn = hidden && !hasBody;
            HideReasoningByTurn = hidden;
            return;
        }
        // 两处例外不能藏：
        // ① 待审批卡**等着用户点**（允许 / 拒绝），收起一轮把它藏起来就无从下手（探针实测：焦点拿不到）；
        // ② 用户消息是"说了什么"（对话本身），不是"干了什么"，永远常显——且它物理上排在所属回合
        //    分隔行**之前**，折叠一轮的显隐遍历（分隔行往后走到下一个分隔行）会路过**下一轮**的用户消息：
        //    藏掉它就是"折叠上一轮，把下一轮刚发的那句话一起吞了"（用户实测症状）。
        HiddenByTurn = hidden && Kind is not (ItemKind.Approval or ItemKind.UserMessage);
    }

    // ── 回合分隔行（Kind = TurnHeader）：整行 = 「本轮用时 N 秒 · 变更 M 项」，点击折叠该轮 ──

    /// <summary>第 N 轮（1 起；来自会话文件的回合序号）。</summary>
    public int TurnIndex { get; private set; }

    /// <summary>该轮首条用户消息（导航轨悬停提示用；用户数据原样）。</summary>
    public string PreviewText
    {
        get => _previewText;
        private set
        {
            if (_previewText == value) return;
            _previewText = value;
            Raise(nameof(RailTipValue));
        }
    }

    /// <summary>本轮助手回复的首段（导航轨悬停预览卡用；用户数据原样）。未产生回复前为空。</summary>
    public string ReplyText
    {
        get => _replyText;
        private set
        {
            if (_replyText == value) return;
            _replyText = value;
            Raise(nameof(ReplyText));
            Raise(nameof(HasReplyPreview));
        }
    }

    private string _replyText = "";

    /// <summary>预览卡第一段是否还是"没有正文"的空值（空 → 模板换成回落文案）。</summary>
    public bool HasAskPreview => PreviewText.Trim().Length > 0;

    /// <summary>悬停预览卡是否还有"助手回了什么"这一段（还没有回复 / 提示词为空 → 不摆空块）。</summary>
    public bool HasReplyPreview => _replyText.Trim().Length > 0;

    public void SetPreview(string text) => PreviewText = text;

    /// <summary>设本轮回复预览（只记第一条助手消息；后续增量不覆盖首印象）。</summary>
    public void SetReplyPreview(string text)
    {
        if (_replyText.Length > 0) return;
        ReplyText = text;
    }

    public bool IsCollapsed
    {
        get => _isCollapsed;
        private set
        {
            if (_isCollapsed == value) return;
            _isCollapsed = value;
            Raise(nameof(IsCollapsed));
            Raise(nameof(ToggleKey));
        }
    }

    public void ToggleCollapsed() => IsCollapsed = !IsCollapsed;

    /// <summary>重投影时设定**初始折叠态**（BuildFeed 专用：历史轮收起、最后一轮展开；
    /// 运行时的折叠/展开走 <see cref="ToggleCollapsed"/>，不经过这里）。</summary>
    internal void SetInitialCollapsed(bool collapsed)
    {
        if (_isCollapsed == collapsed) return;
        _isCollapsed = collapsed;
        Raise(nameof(IsCollapsed));
        Raise(nameof(ToggleKey));
    }

    public string ToggleKey => IsCollapsed ? "ai.turn.expand" : "ai.turn.collapse";

    /// <summary>导航轨高亮（当前视口所在轮；由视图按滚动位置投影，不进业务状态）。</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            Raise(nameof(IsActive));
        }
    }

    private bool _isActive;

    /// <summary>运行中（"已用 N 秒"随视图每秒 Tick 重投影）。</summary>
    public bool IsRunning => _turnState is AiTurnState.Pending or AiTurnState.Streaming or AiTurnState.ToolRunning
        or AiTurnState.AwaitingApproval;

    /// <summary>分隔行文案：运行中 / 用时 / 用时 + 变更数 / 停止 / 失败（含变量的整句走 LocValue）。</summary>
    public LocValue HeaderValue => _turnState switch
    {
        AiTurnState.Pending or AiTurnState.Streaming or AiTurnState.ToolRunning or AiTurnState.AwaitingApproval
            => Loc.K("ai.turn.working", DurationValue),
        AiTurnState.Cancelled or AiTurnState.Interrupted => Loc.K("ai.turn.stopped", DurationValue),
        AiTurnState.Failed => Loc.K("ai.turn.failed", DurationValue),
        _ => _changeCount > 0
            ? Loc.K("ai.turn.workedChanges", DurationValue, _changeCount)
            : Loc.K("ai.turn.worked", DurationValue),
    };

    /// <summary>导航轨悬停提示（按钮自带的键位提示）：第 N 轮 · 时间 + 首条用户消息。</summary>
    public LocValue RailTipValue => Loc.K("ai.rail.tip", Loc.K("ai.rail.turn", TurnIndex),
        LocValue.Clock(At.LocalDateTime, false), LocValue.Literal(PreviewText));

    /// <summary>
    /// 预览卡第一段：用户说了什么（按 220 字符 / 2 段归一化）。
    /// 空文本不在这里回落文案——回落由模板的 DataTrigger 换一条 <see cref="RailPreviewAskFallbackKey"/> 承担，
    /// 这样模型里不出现"为拿回落词而取的成品字符串"。
    /// </summary>
    public LocValue RailPreviewAsk => LocValue.Literal(AiRailVisual.NormalizePreview(PreviewText, ""));

    /// <summary>用户正文缺位时的回落文案键（参照实现叫"用户输入"）。</summary>
    public static string RailPreviewAskFallbackKey => "ai.rail.userFallback";

    /// <summary>预览卡第二段：助手回了什么（同样归一化）。空 + 回合在跑 → 走"助手仍在工作"键。</summary>
    public LocValue RailPreviewReplyText => LocValue.Literal(AiRailVisual.NormalizePreview(_replyText, ""));

    /// <summary>助手正文缺位时的回落文案键（在跑 = 仍在工作，已收尾 = 暂无正文）。</summary>
    public string RailPreviewReplyFallbackKey =>
        IsRunning ? "ai.rail.running" : "ai.rail.emptyReply";

    private LocValue DurationValue
    {
        get
        {
            var seconds = Math.Max(0, (int)((_endedAt ?? DateTimeOffset.UtcNow) - _startedAt).TotalSeconds);
            return seconds < 60 ? Loc.K("ai.turn.sec", seconds) : Loc.K("ai.turn.minSec", seconds / 60, seconds % 60);
        }
    }

    /// <summary>
    /// 这一轮的"工作段"是否**锁死展开**（不渲染收起箭头、点了也不收）。
    /// </summary>
    /// <remarks>
    /// 与参照实现同规则：运行中 / 被中断 / 失败 的那一轮必须保持展开（正在发生的事不该被收起，
    /// 中断与失败要让人一眼看到卡在哪一步）；其余（**已结束**的轮）一律收起——
    /// 只看得到一行"已工作 X"和 AI 的最终回复，旧的思考与工具调用不再占满屏幕。
    /// </remarks>
    public bool IsLockedOpen => _turnState is not AiTurnState.Completed;

    public void ApplyTurn(AiTurn turn)
    {
        var wasLocked = IsLockedOpen;
        _turnState = turn.State;
        // 刚结束的那一轮**随即收起**（参照实现：结束即 fold，只留正文与状态行）。
        // 锁死态解除时补一次通知：箭头要出现、折叠态要生效。
        if (wasLocked && !IsLockedOpen)
        {
            SetInitialCollapsed(true);
            Raise(nameof(IsLockedOpen));
        }
        _startedAt = turn.StartedAt;
        _endedAt = turn.EndedAt;
        _changeCount = turn.ChangeCount;
        if (turn.Index > 0) TurnIndex = turn.Index;
        RaiseTurnReadings();
    }

    /// <summary>变更数就地跟进（回合进行中每条变更都要让分隔行的"变更 M 项"当场变）。</summary>
    public void SetChangeCount(int count)
    {
        if (_changeCount == count) return;
        _changeCount = count;
        RaiseTurnReadings();
    }

    /// <summary>运行中的分隔行每秒重投影一次（已用 N 秒）。</summary>
    public void TickRunning()
    {
        if (IsRunning) RaiseTurnReadings();
    }

    private void RaiseTurnReadings()
    {
        Raise(nameof(HeaderValue));
        Raise(nameof(IsRunning));
        Raise(nameof(RailTipValue));
    }

    /// <summary>工具行内联的变更卡（一行 = 一次调用的全部变更；展开/收起由各行自持）。</summary>
    public IReadOnlyList<AiChangeRow> Changes => _changes;
    public bool HasChanges => _changes.Count > 0;

    /// <summary>"N 项变更"（含变量的整句 = LocValue，渲染边界取词）。</summary>
    public LocValue ChangeCountValue => Loc.K("ai.change.count", _changes.Count);

    // ── 审批卡（功能书 §8.2：做什么 / 动哪些对象 / 影响预览 / 将记住的作用域 / 逐步骤）──

    /// <summary>本地化动作短语键（缺命令名时回落到通用短语）。</summary>
    public string ActionKey => Command.Length > 0 ? LinkPocket.Views.AiKeyMap.Action(Command) : "ai.action.unknown";

    /// <summary>动哪些对象：已知名称原样（用户数据）→ 否则按数量 → 否则如实说"未指明"。</summary>
    public LocValue TargetValue
    {
        get
        {
            var approval = Approval;
            if (approval is null) return LocValue.Empty;
            if (approval.TargetNames.Count > 0) return LocValue.Literal(string.Join(", ", approval.TargetNames));
            return approval.TargetCount > 0
                ? Loc.K("count.itemsN", approval.TargetCount)
                : LocValue.Of("ai.approve.target.unknown");
        }
    }

    /// <summary>名称列表被截断（还有 N 个对象没列出来）——如实标注，不假装列全了。</summary>
    public bool HasMoreTargets
        => Approval is { TargetMore: > 0, TargetNames.Count: > 0 };

    public LocValue MoreTargetsValue => Loc.K("ai.approve.target.more", Approval?.TargetMore ?? 0);

    /// <summary>单一目标的 canonical 路径（可解析时）；显示走当前语言投影。</summary>
    public bool HasTargetPath => Approval?.TargetPath is not null;
    public LocValue TargetPathValue => LocValue.Projection(Approval?.TargetPath ?? "");

    /// <summary>
    /// 步骤预览一次最多列几条。
    /// </summary>
    /// <remarks>
    /// ⚠️ **这不是照搬参照实现**：参照的审批卡里根本没有"步骤列表"这个概念——它的一次请求就是
    /// 一张卡、一个工具调用块（多目标只作为一条命令串 / 一个文件列表呈现，文件列表上限 6 条是
    /// <c>permission-request-preview.ts</c> 里"提取涉及文件路径"的截断，与本表的语义不同）。
    /// 我们的审批对象經常是**一条多步批脚本**，逐条铺开能看清要动什么，所以保留列表，
    /// 只是不让它铺满整张卡：超出前 6 条的部分用一行"另 N 步"交代（条数照实说，不静默省略）。
    /// </remarks>
    public const int ApprovalStepPreviewMax = 6;

    /// <summary>
    /// 批 / 宏的逐步骤影响（非批为空）。**只列前 <see cref="ApprovalStepPreviewMax"/> 条**。
    /// </summary>
    /// <remarks>
    /// 为什么截断：删 13 个空文件夹这种"一条批脚本 13 步"的请求，把 13 行全铺开，
    /// 看上去就是"每个文件夹都通知我一遍"（实测反馈）。一次请求 = 一张卡、一个按钮通过；
    /// 卡片只负责让人看清**大概要动什么**，超出的用一行"另 N 步"交代清楚（一条不少，只是不铺开）。
    /// </remarks>
    public IReadOnlyList<AiApprovalStepRow> ApprovalSteps
        => Approval?.Steps is { Count: > 0 } steps
            ? steps.Take(ApprovalStepPreviewMax).Select(step => new AiApprovalStepRow(step)).ToArray()
            : [];
    public bool HasApprovalSteps => Approval?.Steps is { Count: > 0 };

    /// <summary>被截断掉的步骤数（&gt; 0 时卡片补一行"另 N 步"，绝不静默省略）。</summary>
    public int HiddenStepCount => Math.Max(0, (Approval?.Steps?.Count ?? 0) - ApprovalStepPreviewMax);
    public bool HasHiddenSteps => HiddenStepCount > 0;
    public LocValue HiddenStepsValue => Loc.K("ai.approve.steps.more", HiddenStepCount);

    /// <summary>"批脚本步骤（共 N 步）"的标题（含变量整句 = LocValue）。</summary>
    public LocValue StepsTitleValue
        => Loc.K("ai.approve.steps.title", Approval?.Steps?.Count ?? 0);

    /// <summary>批 / 宏却读不到脚本 → 如实说明"只按命令审批"（不假装看过了）。</summary>
    public bool HasStepsNote => Command is "batch.run" or "macro.run" && !HasApprovalSteps;
    public string StepsNoteKey => "ai.approve.steps.unread";

    /// <summary>「本次会话总是允许」将记住的作用域（必须显示，不允许只显示"记住"）。</summary>
    public bool HasAllowScope => Approval?.AllowScope is not null;
    public LocValue AllowScopeValue
        => Approval?.AllowScope is { } scope ? Loc.K("ai.approve.allowScope", scope) : LocValue.Empty;

    /// <summary>影响预览：引擎下发的影响面 / 逐步骤 / 已知对象，三者都没有才如实说"无法预览"。</summary>
    public LocValue ImpactValue => LinkPocket.Views.AiKeyMap.Impact(Approval?.PreviewSummary);
    public bool HasImpact => Approval?.PreviewSummary is not null;
    public bool HasPreview => HasImpact || HasApprovalSteps
                               || Approval is { TargetCount: > 0 } or { TargetNames.Count: > 0 };

    public static AiFeedItem ForTurn(AiTurn turn)
    {
        var item = new AiFeedItem
        {
            Kind = ItemKind.TurnHeader,
            ItemId = turn.TurnId,
            TurnId = turn.TurnId,
            At = turn.StartedAt,
            TurnIndex = turn.Index,
        };
        item._startedAt = turn.StartedAt;
        item._endedAt = turn.EndedAt;
        item._turnState = turn.State;
        item._changeCount = turn.ChangeCount;
        return item;
    }

    public static AiFeedItem ForUser(AiMessage message)
        => new()
        {
            Kind = ItemKind.UserMessage,
            ItemId = message.MessageId,
            TurnId = message.TurnId,
            At = message.At,
            Text = message.Text,
            Mentions = message.Mentions ?? [],
        };

    public static AiFeedItem ForAssistant(AiMessage message)
        => new()
        {
            Kind = ItemKind.AssistantMessage,
            ItemId = message.MessageId,
            TurnId = message.TurnId,
            At = message.At,
            Text = message.Text,
            RenderText = message.Text,
            ReasoningText = message.Reasoning ?? "",
            IsStreaming = message.IsStreaming,
        };

    public static AiFeedItem ForTool(AiToolCall call)
    {
        var item = new AiFeedItem
        {
            Kind = ItemKind.ToolCall,
            ItemId = call.CallId,
            TurnId = call.TurnId,
            At = call.At,
            Command = call.Command,
            Summary = call.ArgsSummary ?? "",
            ToolCall = call,
        };
        return item.WithState(call);
    }

    public static AiFeedItem ForApproval(AiApproval approval)
        => new()
        {
            Kind = ItemKind.Approval,
            ItemId = approval.ApprovalId,
            TurnId = approval.TurnId,
            At = approval.At,
            Command = approval.Command,
            ApprovalId = approval.ApprovalId,
            IsDestructive = approval.IsDestructive,
            Approval = approval,
            IsApprovalOpen = approval.Decision is null,
        };

    public static AiFeedItem ForNotice(string itemId, LocValue value)
        => new() { Kind = ItemKind.Notice, ItemId = itemId, At = DateTimeOffset.UtcNow, NoticeValue = value };

    /// <summary>流式增量：原文立即跟进；Markdown 渲染面按节流跟进（收尾的 <see cref="Finalize"/> 补整篇）。</summary>
    public void AppendDelta(string chunk)
    {
        Text += chunk;
        var now = DateTimeOffset.UtcNow;
        if (now - _lastRenderAt < RenderThrottle) return;
        _lastRenderAt = now;
        RenderText = _text;
    }

    public void Finalize(AiMessage message)
    {
        Text = message.Text;
        RenderText = message.Text;
        // 思考原文以落定态为准（流式期间是节流投影，这里补全文）
        _reasoningText = message.Reasoning ?? _reasoningText;
        ReasoningText = _reasoningText;
        Raise(nameof(HasReasoning));
        IsStreaming = false;
    }

    public void Apply(AiToolCall call)
    {
        ToolCall = call;
        WithState(call);
    }

    public void Apply(AiApproval approval)
    {
        Approval = approval;
        IsApprovalOpen = approval.Decision is null;
        Raise(nameof(Approval));
        Raise(nameof(IsApprovalOpen));
        Raise(nameof(TargetValue));
        Raise(nameof(HasMoreTargets));
        Raise(nameof(MoreTargetsValue));
        Raise(nameof(HasTargetPath));
        Raise(nameof(TargetPathValue));
        Raise(nameof(ApprovalSteps));
        Raise(nameof(HasApprovalSteps));
        Raise(nameof(HasAllowScope));
        Raise(nameof(AllowScopeValue));
        Raise(nameof(ImpactValue));
        Raise(nameof(HasImpact));
        Raise(nameof(HasPreview));
    }

    /// <summary>挂一条本工具调用的变更（一行工具卡一张内联变更卡，见功能书 §7.5）。</summary>
    public void AttachChange(AiChangeRow row)
    {
        _changes.Add(row);
        Raise(nameof(Changes));
        Raise(nameof(HasChanges));
        Raise(nameof(ChangeCountValue));
    }

    private AiFeedItem WithState(AiToolCall call)
    {
        StateKey = LinkPocket.Views.AiKeyMap.ToolState(call.State);
        StateSuffix = call.ElapsedMs > 0 ? $"{call.ElapsedMs} ms" : (call.ErrorCode ?? "");
        if (call.ErrorCode is { Length: > 0 } code && call.ElapsedMs > 0) StateSuffix = $"{code} · {call.ElapsedMs} ms";
        Detail = call.ResultSummary ?? "";
        Raise(nameof(ToolCall));
        Raise(nameof(StateKey));
        Raise(nameof(StateSuffix));
        Raise(nameof(Detail));
        Raise(nameof(ErrorCode));
        Raise(nameof(HasError));
        Raise(nameof(IsDryRun));
        Raise(nameof(ArgsText));
        Raise(nameof(HasArgs));
        Raise(nameof(IconKind));
        return this;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>左栏一行会话（标题 / 时间 / 运行状态 / 变更数徽标；重命名与摘要更新就地投影）。</summary>
public sealed class AiSessionRow(AiSessionSummary summary) : INotifyPropertyChanged
{
    private AiSessionSummary _summary = summary;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AiSessionSummary Summary => _summary;
    public string SessionId => _summary.SessionId;
    public string Title => _summary.Title;

    /// <summary>时间（渲染边界取词；换语言由取词版本号自动重算）。</summary>
    public LocValue TimeValue => LocValue.Clock(_summary.UpdatedAt.LocalDateTime, false);

    /// <summary>运行中（该会话有在场回合）——行首状态槽据此转圈。</summary>
    public bool IsRunning => _summary.ActiveTurnState is AiTurnState.Pending or AiTurnState.Streaming
        or AiTurnState.ToolRunning or AiTurnState.AwaitingApproval;

    public bool HasChanges => _summary.ChangeCount > 0;
    public LocValue ChangeCountValue => Loc.K("count.itemsN", _summary.ChangeCount);

    /// <summary>就地重命名中（行内标题位置换成编辑框；Enter 提交 / Esc 取消）。</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value) return;
            _isRenaming = value;
            Raise(nameof(IsRenaming));
        }
    }

    private bool _isRenaming;

    public void Apply(AiSessionSummary summary)
    {
        _summary = summary;
        Raise(nameof(Summary));
        Raise(nameof(Title));
        Raise(nameof(TimeValue));
        Raise(nameof(IsRunning));
        Raise(nameof(HasChanges));
        Raise(nameof(ChangeCountValue));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>模型下拉的一项（标签 = `供应商 / 模型`，机器面标识符原样；选择写助手偏好，与 `/model` 同一条路径）。</summary>
public sealed record AiModelOption(string ProviderId, string ModelId, string Label);

/// <summary>提及候选一行（面板显示）：名称是用户数据原样；路径只存 canonical，显示走当前语言投影。</summary>
public sealed class AiMentionCandidateRow(AiMentionCandidate candidate)
{
    public AiMentionKind Kind => candidate.Kind;
    public string Id => candidate.Id;
    public string Name => candidate.Name;

    public bool HasPath => candidate.Path is { Length: > 0 };
    public LocValue PathValue => HasPath ? LocValue.Projection(candidate.Path) : LocValue.Empty;
}

/// <summary>批 / 宏审批卡里的一行「逐步骤影响」（步骤号与命令是机器面原样，对象是用户数据或数量）。</summary>
public sealed class AiApprovalStepRow(AiApprovalStep step)
{
    public AiApprovalStep Step { get; } = step;

    public int Index => Step.Index;
    public string Command => Step.Command;
    public bool IsDestructive => Step.IsDestructive;

    /// <summary>"步骤 N"（含变量整句 = LocValue，数字走 Invariant）。</summary>
    public LocValue IndexLabel => Loc.K("ai.approve.steps.row", Step.Index);

    /// <summary>本步骤的对象：名称（用户数据原样）→ 数量 → 未指明（模板占位符不当成值）。</summary>
    public LocValue TargetValue => Step.TargetName is { Length: > 0 } name
        ? LocValue.Literal(name)
        : Step.TargetCount > 0
            ? Loc.K("count.itemsN", Step.TargetCount)
            : LocValue.Of("ai.approve.target.unknown");

    /// <summary>错误策略（脚本没写 = 不显示该段）：已知取键，未知原样显示机器面标识符（不猜、不吞）。</summary>
    public bool HasOnError => Step.OnError is not null;
    public LocValue OnErrorValue => Step.OnError is null
        ? LocValue.Empty
        : LinkPocket.Views.AiKeyMap.OnError(Step.OnError) is { } key
            ? LocValue.Of(key)
            : LocValue.Literal(Step.OnError);
}

/// <summary>
/// 导航轨一根短横条在"悬浮山峰"里的视觉态。
/// ⚠️ 口径来自参照实现的 <c>resolveConversationTurnNavigatorBarVisualState</c>：
/// **不透明度只有两档**——指到的那根 1、其余一律 0.58（不是"越远越淡"的渐变）；
/// 所以"山峰"完全由 <paramref name="ScaleX"/> 承担：2.6 / 1.7 / 1.25 / 1。
/// </summary>
internal readonly record struct RailVisual(double ScaleX, double Opacity);

/// <summary>导航轨的视觉常量：与"山峰"算法一起钉在一处，视图只做插值。</summary>
internal static class AiRailVisual
{
    /// <summary>横向峰值倍数（指到的那根）。</summary>
    public const double PeakScale = 2.6;

    /// <summary>峰侧的相邻倍数（上下各一根）。</summary>
    public const double NearScale = 1.7;

    /// <summary>峰侧的次相邻倍数（上下各再一根）。</summary>
    public const double MidScale = 1.25;

    /// <summary>峰顶的不透明度（唯独这一档是 1）。</summary>
    public const double PeakOpacity = 1.0;

    /// <summary>其余所有档位共用的不透明度（含静止态；参照实现里没有第三档）。</summary>
    public const double MutedOpacity = 0.58;

    /// <summary>
    /// 「当前视口所在那一轮」的不透明度：0.9。它只在**没有悬浮焦点**时生效
    /// （参照实现的 <c>showScrollActiveColor</c>），悬浮时让位给山峰。
    /// </summary>
    public const double CurrentOpacity = 0.9;

    /// <summary>条本身的尺寸（宽 × 高，单位 DIP；峰值 2.6 倍 ≈ 31.2）。</summary>
    public const double BarWidth = 12;
    public const double BarHeight = 2;
    public const double PeakWidth = 31.2;

    /// <summary>推移时长（毫秒）：150ms 是"丝滑"的来源——再短显得跳，再长跟不上手。</summary>
    public const int TravelMs = 150;

    /// <summary>悬浮预览卡的开 / 收延迟（毫秒）：开得稍慢（滚过整轨不一路闪卡），收得干脆。</summary>
    /// <remarks>
    /// 收的宽限**长于**参照的 80ms：参照的卡由 Radix HoverCard 托管，指针进卡即算"还在里面"；
    /// WPF 的 Popup 是独立窗口，从条挪到卡要跨过那条缝——80ms 会把**可点的卡**在途中收掉。
    /// 行为与参照一致（指针停在哪一边都算悬浮），补的是"跨缝"这段时间。
    /// </remarks>
    public const int PreviewOpenDelayMs = 120;
    public const int PreviewCloseDelayMs = 260;

    /// <summary>预览卡文案的截断口径：总字符上限 220、段落上限 2（与参照实现同值）。</summary>
    public const int PreviewMaxChars = 220;
    public const int PreviewMaxParagraphs = 2;

    /// <summary>不足两根不露面（一根时轨道没有导航意义，只是视觉噪音）。</summary>
    public const int MinTurns = 2;

    /// <summary>
    /// 按"与悬浮位置的距离"取视觉态：0 = 指到的那根（峰），1 / 2 = 相邻与次相邻，其余 = 静止。
    /// 悬浮离开（<paramref name="hoverIndex"/> 为负）= 全部回静止档。
    /// </summary>
    public static RailVisual VisualAt(int hoverIndex, int index)
    {
        if (hoverIndex < 0) return new RailVisual(1, MutedOpacity);
        return Math.Abs(index - hoverIndex) switch
        {
            0 => new RailVisual(PeakScale, PeakOpacity),
            1 => new RailVisual(NearScale, MutedOpacity),
            2 => new RailVisual(MidScale, MutedOpacity),
            _ => new RailVisual(1, MutedOpacity),
        };
    }

    /// <summary>
    /// 预览文本归一化（与参照实现同口径）：先按空行切段、段内折叠空白、只取前
    /// <see cref="PreviewMaxParagraphs"/> 段，再按 <see cref="PreviewMaxChars"/> 截断
    /// （截断处补 "..."）。空文本回落到 <paramref name="fallback"/>。
    /// </summary>
    public static string NormalizePreview(string text, string fallback)
    {
        var paragraphs = (text ?? "")
            .Trim()
            .Split(["\n\n"], StringSplitOptions.None)
            .Select(paragraph => string.Join(" ", paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(paragraph => paragraph.Length > 0)
            .Take(PreviewMaxParagraphs)
            .ToArray();
        if (paragraphs.Length == 0) return fallback;
        var joined = string.Join("\n", paragraphs);
        if (joined.Length <= PreviewMaxChars) return joined;
        var keep = Math.Max(8, PreviewMaxChars) - 3;
        return joined[..Math.Min(keep, joined.Length)].TrimEnd() + "...";
    }
}

/// <summary>按 Kind 选模板（模板都在 AiView.xaml 的资源里，键名 = ai.template.*）。</summary>
public sealed class AiFeedTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TurnHeaderTemplate { get; set; }
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }
    public DataTemplate? ToolTemplate { get; set; }
    public DataTemplate? ApprovalTemplate { get; set; }
    public DataTemplate? NoticeTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item switch
    {
        AiFeedItem { Kind: AiFeedItem.ItemKind.TurnHeader } => TurnHeaderTemplate,
        AiFeedItem { Kind: AiFeedItem.ItemKind.UserMessage } => UserTemplate,
        AiFeedItem { Kind: AiFeedItem.ItemKind.AssistantMessage } => AssistantTemplate,
        AiFeedItem { Kind: AiFeedItem.ItemKind.ToolCall } => ToolTemplate,
        AiFeedItem { Kind: AiFeedItem.ItemKind.Approval } => ApprovalTemplate,
        _ => NoticeTemplate,
    };
}

/// <summary>字段级 diff 的一行（字段名走键，值里字符串 = 用户数据原样、布尔/空值走键）。</summary>
public sealed class AiFieldRow
{
    /// <summary>字段名 → 文案键（引擎的字段集固定，见 ENGINE-API §1；表外字段原样显示机器名）。</summary>
    private static readonly Dictionary<string, string> FieldKeys = new(StringComparer.Ordinal)
    {
        ["url"] = "ai.field.url",
        ["title"] = "ai.field.title",
        ["description"] = "ai.field.description",
        ["folder_id"] = "ai.field.folderId",
        ["is_important"] = "ai.field.isImportant",
        ["favicon_url"] = "ai.field.faviconUrl",
        ["name"] = "ai.field.name",
        ["parent_id"] = "ai.field.parentId",
    };

    public required AiFieldChange Field { get; init; }

    public LocValue FieldName => FieldKeys.TryGetValue(Field.Field, out var key)
        ? LocValue.Of(key)
        : LocValue.Literal(Field.Field);

    public LocValue Before => ToValue(Field.Before);
    public LocValue After => ToValue(Field.After);

    private static LocValue ToValue(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => LocValue.Literal(value.Value.GetString() ?? ""),
        JsonValueKind.True => LocValue.Of("ai.diff.bool.true"),
        JsonValueKind.False => LocValue.Of("ai.diff.bool.false"),
        // 会话文件往返后"空值"与"不适用"统一为 null：界面照实说"空"
        null or JsonValueKind.Null or JsonValueKind.Undefined => LocValue.Of("ai.diff.empty"),
        _ => LocValue.Literal(value.Value.GetRawText()),
    };
}

/// <summary>台账一行（右栏）：分类 / 实体 / 结果都是键或机器面标识符，不拼界面文案。
/// 字段级明细默认折叠（前 5 条），展开后全量（功能书 §7.5）。</summary>
public sealed class AiChangeRow : INotifyPropertyChanged
{
    /// <summary>折叠时显示的字段条数。</summary>
    public const int CollapsedFieldCount = 5;

    private bool _isExpanded;
    private IReadOnlyList<AiFieldRow>? _fields;

    public required AiChange Change { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string KindKey => $"ai.change.kind.{Change.Kind.ToString().ToLowerInvariant()}";
    public string EntityLabel => Change.EntityName is { Length: > 0 } name ? name : Change.EntityId;
    public string EntitySuffix => $"{Change.EntityType} · {Change.Command}";
    public string OutcomeLabel => Change.Outcome.ToString();
    public string UndoableKey => Change.Undoable ? "ai.change.undoable" : "ai.change.notUndoable";
    public string? Reason => Change.ErrorCode;

    /// <summary>实体位置（canonical → 当前语言显示投影）；未知/无位置时为空。</summary>
    public LocValue PathValue => Change.EntityPath is { Length: > 0 } path
        ? LocValue.Projection(path)
        : LocValue.Empty;

    public IReadOnlyList<AiFieldRow> Fields
        => _fields ??= (Change.Fields ?? []).Select(field => new AiFieldRow { Field = field }).ToArray();

    public bool HasFields => Fields.Count > 0;
    public bool CanExpand => Fields.Count > CollapsedFieldCount;

    /// <summary>右栏紧凑行的字段变更条数（整句由 LocValue 出、渲染边界取词）：逐字段明细与对话流
    /// 工具卡内联的变更卡同源，右栏不再重复铺一遍。</summary>
    public LocValue FieldCountValue => Loc.K("ai.diff.fieldCount", Fields.Count);

    /// <summary>折叠 = 前 5 条；展开 = 全量。</summary>
    public IReadOnlyList<AiFieldRow> VisibleFields => IsExpanded || !CanExpand
        ? Fields
        : Fields.Take(CollapsedFieldCount).ToArray();

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Raise(nameof(IsExpanded));
            Raise(nameof(VisibleFields));
            Raise(nameof(ExpandKey));
        }
    }

    /// <summary>展开 / 收起的按钮文案键。</summary>
    public string ExpandKey => IsExpanded ? "ai.diff.collapse" : "ai.diff.expand";

    /// <summary>粒度来源标注：只有"仅实体级 / AI 对账"需要如实标注（引擎 diff 是默认口径）。</summary>
    public string? SourceKey => Change.Source switch
    {
        AiChangeSource.EntityOnly => "ai.diff.source.entityOnly",
        AiChangeSource.Reconciled => "ai.diff.source.reconciled",
        _ => null,
    };
    public bool HasSourceNote => SourceKey is not null;

    /// <summary>对账与引擎 diff 不一致的标注（功能书 §7.1：只标注、不改写引擎事实）。</summary>
    public string? MismatchKey => Change.ReconcileMismatch ? "ai.diff.reconcile.mismatch" : null;
    public bool HasMismatch => Change.ReconcileMismatch;

    /// <summary>上游截断如实标注（"另有 N 项未列出"）。</summary>
    public LocValue TruncatedValue => Change.Truncated
        ? Loc.K("ai.diff.truncated", Change.Omitted)
        : LocValue.Empty;
    public bool IsTruncated => Change.Truncated;

    public void ToggleExpand() => IsExpanded = !IsExpanded;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

