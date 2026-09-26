using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.ViewModels;

namespace LinkPocket.UI.Ai;

/// <summary>
/// AI 页 VM 的斜杠命令面：输入以 <c>/</c> 开头即按**本地命令**解析，不发给模型（功能书 §5.4）。
/// 命令集：<c>/new</c> 新会话 · <c>/model</c> 切换模型 · <c>/mode</c> 切换模式 · <c>/undo</c> 撤销上一轮 AI 变更 ·
/// <c>/audit</c> 打开引擎审计页签 · <c>/export</c> 导出报告（文件对话框归视图） · <c>/help</c>。
/// 结果以提示条回话（<see cref="AiFeedItem.NoticeValue"/>），文案一律键 + 参数、渲染边界取词。
/// </summary>
public sealed partial class AiViewModel
{
    private int _noticeSeq;

    /// <summary>/export 需要文件路径——对话框是视图的事，VM 只发请求（功能书 §7.6 导出三格式）。</summary>
    public event Action? ExportRequested;

    /// <summary>解析并执行一条斜杠命令；返回 true = 已按本地命令处理、不得发给模型。</summary>
    public async Task<bool> TrySlashAsync(string sessionId, string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('/')) return false;

        var split = trimmed.IndexOf(' ');
        var name = (split < 0 ? trimmed : trimmed[..split]).ToLowerInvariant();
        var arg = split < 0 ? "" : trimmed[(split + 1)..].Trim();

        try
        {
            switch (name)
            {
                case "/help":
                    Notice(Loc.K("ai.slash.help"));
                    break;
                case "/new":
                    await NewSessionAsync().ConfigureAwait(true);
                    break;
                case "/export":
                    ExportRequested?.Invoke();
                    break;
                case "/mode":
                    await SlashModeAsync(arg).ConfigureAwait(true);
                    break;
                case "/model":
                    await SlashModelAsync(arg).ConfigureAwait(true);
                    break;
                case "/undo":
                    await SlashUndoAsync(sessionId).ConfigureAwait(true);
                    break;
                default:
                    Notice(Loc.K("ai.slash.unknown", name));
                    break;
            }
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
        return true;
    }

    private void Notice(LocValue value) => Feed.Add(AiFeedItem.ForNotice($"n-{++_noticeSeq}", value));

    // ── /mode [readonly|confirm|auto]：缺省轮转 ─────────────────────────

    private async Task SlashModeAsync(string arg)
    {
        AiMode? mode = arg.ToLowerInvariant() switch
        {
            "" => (AiMode)(((int)Mode + 1) % 3),
            "readonly" or "read_only" or "ro" => AiMode.ReadOnly,
            "confirm" or "confirm_each" => AiMode.ConfirmEach,
            "auto" or "auto_apply" => AiMode.AutoApply,
            _ => null,
        };
        if (mode is not { } chosen)
        {
            Notice(Loc.K("ai.slash.modeUsage"));
            return;
        }

        await SetModeAsync(chosen).ConfigureAwait(true);
        Notice(Loc.K(chosen switch
        {
            AiMode.ReadOnly => "ai.slash.modeSet.readonly",
            AiMode.AutoApply => "ai.slash.modeSet.autoApply",
            _ => "ai.slash.modeSet.confirmEach",
        }));
    }

    // ── /model [provider/model | model]：缺省切到下一个已启用模型 ─────────

    private async Task SlashModelAsync(string arg)
    {
        var choices = new List<(string ProviderId, string ModelId, string Label)>();
        foreach (var provider in await _assistant.ListProvidersAsync().ConfigureAwait(true))
            foreach (var model in provider.Models.Where(m => m.Enabled))
                choices.Add((provider.Id, model.Id, $"{provider.Id}/{model.Id}"));

        if (choices.Count == 0)
        {
            Notice(Loc.K("ai.slash.modelNone"));
            return;
        }

        var preferences = await _assistant.GetPreferencesAsync().ConfigureAwait(true);
        int at;
        if (arg.Length == 0)
        {
            // 轮转：当前选择不在清单里（未选/已停用）= 从头开始
            at = choices.FindIndex(c => c.ProviderId == preferences.ProviderId && c.ModelId == preferences.ModelId);
            at = (at + 1) % choices.Count;
        }
        else
        {
            at = choices.FindIndex(c =>
                string.Equals(c.Label, arg, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.ModelId, arg, StringComparison.OrdinalIgnoreCase)
                || c.Label.EndsWith($"/{arg}", StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                Notice(Loc.K("ai.slash.modelUnknown", arg));
                return;
            }
        }

        var chosen = choices[at];
        await _assistant.SavePreferencesAsync(preferences with
        {
            ProviderId = chosen.ProviderId,
            ModelId = chosen.ModelId,
        }).ConfigureAwait(true);
        RefreshSelection();
        Notice(Loc.K("ai.slash.modelSet", chosen.Label));
    }

    // ── /undo：撤销上一轮 AI 变更（引擎 undo.undo 定点撤销；不可撤销的如实跳过）──

    private async Task SlashUndoAsync(string sessionId)
        => NoticeForUndo(await _assistant.UndoLastTurnAsync(sessionId).ConfigureAwait(true));

    /// <summary>撤销回执 → 提示条（回合级 <c>/undo</c> 与会话级「撤销本会话」共用：结果形状相同、文案同一套）。</summary>
    private void NoticeForUndo(AiUndoResult result)
    {
        _ = RefreshUndoableAsync();   // 撤完重新数一遍可撤销批次（按钮给不给跟着变）
        if (result.TotalCalls == 0)
        {
            Notice(Loc.K("ai.slash.undoNone"));
            return;
        }

        if (result.ErrorCode is { } code) LastErrorKey = LinkPocket.Views.AiKeyMap.Error(code);
        Notice(result.ErrorCode is null && result.MissingCalls == 0
            ? Loc.K("ai.slash.undoDone", result.UndoneCalls)
            : Loc.K("ai.slash.undoPartial", result.UndoneCalls, result.TotalCalls));
    }
}
