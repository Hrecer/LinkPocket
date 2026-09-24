using System.Collections.ObjectModel;
using LinkPocket.Contracts;

namespace LinkPocket.UI.Ai;

/// <summary>
/// AI 页 VM 的提及面（输入区 @ 面板 + chip 区；口径见功能书 §5.4）：
/// 候选来自 <see cref="IAiAssistant.SearchMentionsAsync"/>（只读检索）；选入 = 文本里插入 `@显示名`
/// + chip 记 <c>{kind, id, name}</c>（**稳定标识符进消息序列化**）；
/// 发送时只送"文本里仍含有该标记"的项（被删掉标记的 chip 当场消失——不静默多送）。
/// </summary>
public sealed partial class AiViewModel
{
    /// <summary>@token 的最大长度（超过即当普通文本，不再检索——避免把长段文本当查询打给引擎）。</summary>
    private const int MaxMentionQueryChars = 40;

    private int _mentionTokenStart = -1;
    private int _mentionTokenEnd;
    private string _mentionQuery = "";
    private int _mentionSelection;
    private int? _pendingCaret;
    private bool _isMentionPanelOpen;

    /// <summary>待发的提及（chip 区）。</summary>
    public ObservableCollection<AiMentionRef> Mentions { get; } = [];

    /// <summary>提及候选（面板列表）。</summary>
    public ObservableCollection<AiMentionCandidate> MentionCandidates { get; } = [];

    public bool IsMentionPanelOpen
    {
        get => _isMentionPanelOpen;
        private set => Set(ref _isMentionPanelOpen, value, nameof(IsMentionPanelOpen));
    }

    public bool HasMentions => Mentions.Count > 0;

    /// <summary>面板里当前高亮的候选下标（**唯一事实源 = VM**：视图的 ListBox 与键盘都写它、都读它）。</summary>
    public int MentionSelectionIndex
    {
        get => _mentionSelection;
        set
        {
            var clamped = MentionCandidates.Count == 0 ? 0 : Math.Clamp(value, 0, MentionCandidates.Count - 1);
            Set(ref _mentionSelection, clamped, nameof(MentionSelectionIndex));
        }
    }

    /// <summary>视图在输入框文本 / 光标变化时调用（<paramref name="caretIndex"/> = 光标位置）。</summary>
    public void UpdateMentionQuery(string text, int caretIndex)
    {
        var start = FindTokenStart(text ?? "", caretIndex);
        if (start < 0)
        {
            CloseMentionPanel();
            return;
        }

        var query = text![(start + 1)..caretIndex];
        if (query.Length > MaxMentionQueryChars || query.Contains('\n'))
        {
            CloseMentionPanel();
            return;
        }

        // 已经提及过（比如刚选入、光标停在 `@名称` 末尾）：不重复弹面板
        if (Mentions.Any(m => string.Equals("@" + m.Name, text[start..caretIndex], StringComparison.Ordinal)))
        {
            CloseMentionPanel();
            return;
        }

        _mentionTokenStart = start;
        _mentionTokenEnd = caretIndex;
        _mentionQuery = query;
        if (query.Trim().Length == 0)
        {
            CloseMentionPanel();   // 只敲了一个 @：候选未定，不检索（引擎侧不做空查询）
            return;
        }
        _ = SearchMentionsAsync(query);
    }

    /// <summary>↑/↓ 移动候选高亮（到边界停住）。</summary>
    public void MoveMentionSelection(int delta)
    {
        if (!IsMentionPanelOpen || MentionCandidates.Count == 0) return;
        var next = Math.Clamp(MentionSelectionIndex + delta, 0, MentionCandidates.Count - 1);
        MentionSelectionIndex = next;
    }

    /// <summary>Enter / 点击：把高亮候选选入（文本插 `@名称`、chip 记稳定 ID）。</summary>
    public void CommitMentionSelection()
    {
        if (!IsMentionPanelOpen || MentionSelectionIndex < 0 || MentionSelectionIndex >= MentionCandidates.Count)
            return;
        var candidate = MentionCandidates[MentionSelectionIndex];
        var text = ComposerText;
        if (_mentionTokenStart >= 0 && _mentionTokenStart < text.Length && _mentionTokenEnd <= text.Length)
        {
            var inserted = "@" + candidate.Name;
            ComposerText = text[.._mentionTokenStart] + inserted + text[_mentionTokenEnd..];
            _pendingCaret = _mentionTokenStart + inserted.Length;
        }
        var mention = new AiMentionRef(candidate.Kind, candidate.Id, candidate.Name);
        if (!Mentions.Any(m => m.Id == candidate.Id && m.Kind == candidate.Kind))
        {
            Mentions.Add(mention);
            Raise(nameof(HasMentions));
        }
        CloseMentionPanel();
    }

    /// <summary>Esc：关掉面板（不动文本）。</summary>
    public void CancelMentions() => CloseMentionPanel();

    /// <summary>摘除一个提及 chip（文本里的 `@名称` 原样保留，用户可自行删）。</summary>
    public void RemoveMention(AiMentionRef mention)
    {
        if (Mentions.Remove(mention)) Raise(nameof(HasMentions));
    }

    /// <summary>视图取一次"提交后应把光标放哪"（一次性；无请求返回 null）。</summary>
    public int? TakePendingCaret()
    {
        var value = _pendingCaret;
        _pendingCaret = null;
        return value;
    }

    /// <summary>发送前取本次要随消息带走的提及（只含文本里仍含 `@名称` 的项；其余当场从 chip 区消失）。</summary>
    private IReadOnlyList<AiMentionRef> MentionsForSend(string text)
    {
        var kept = Mentions.Where(m => text.Contains("@" + m.Name, StringComparison.Ordinal)).ToArray();
        if (kept.Length != Mentions.Count)
        {
            Mentions.Clear();
            foreach (var mention in kept) Mentions.Add(mention);
            Raise(nameof(HasMentions));
        }
        return kept;
    }

    /// <summary>发送成功后清空 chip 区（提及已随消息带走）。</summary>
    private void ClearMentions()
    {
        if (Mentions.Count == 0) return;
        Mentions.Clear();
        Raise(nameof(HasMentions));
    }

    private async Task SearchMentionsAsync(string query)
    {
        try
        {
            var items = await _assistant.SearchMentionsAsync(query, 8).ConfigureAwait(true);
            if (!string.Equals(_mentionQuery, query, StringComparison.Ordinal)) return;   // 期间又敲了：本次结果作废
            MentionCandidates.Clear();
            foreach (var item in items) MentionCandidates.Add(item);
            MentionSelectionIndex = 0;
            IsMentionPanelOpen = MentionCandidates.Count > 0;
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
            CloseMentionPanel();
        }
    }

    private void CloseMentionPanel()
    {
        _mentionTokenStart = -1;
        _mentionQuery = "";
        if (MentionCandidates.Count > 0) MentionCandidates.Clear();
        IsMentionPanelOpen = false;
    }

    /// <summary>光标前那个 `@` 的位置（前一个字符必须是空白或行首；跨行不算——多行文本里 @ 只在当前行起效）。</summary>
    private static int FindTokenStart(string text, int caretIndex)
    {
        var caret = Math.Clamp(caretIndex, 0, text.Length);
        for (var index = caret - 1; index >= 0; index--)
        {
            var ch = text[index];
            if (ch == '\n') return -1;
            if (ch != '@') continue;
            if (index > 0 && !char.IsWhiteSpace(text[index - 1])) return -1;
            return index;
        }
        return -1;
    }
}
