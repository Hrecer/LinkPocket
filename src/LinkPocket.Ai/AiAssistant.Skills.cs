using System.Globalization;
using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 技能库面（AiAssistant 的 partial；口径见功能书 §5.4 技能条）：
/// 技能 = 名称 + 说明（何时用）+ 提示模板（可带 `{参数}`）+ 可选绑定的宏；运行 = 渲染模板作为**用户消息**
/// 发起回合（走正常回合与审批链，绝不绕过）。绑定宏以机器面一行追加到渲染结果（让模型知道用 `macro.run`）。
/// </summary>
public sealed partial class AiAssistant
{
    public Task<IReadOnlyList<AiSkill>> ListSkillsAsync(CancellationToken ct = default)
        => Task.FromResult(_skillStore.List());

    public async Task<AiSkill> SaveSkillAsync(AiSkillDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var macro = string.IsNullOrWhiteSpace(draft.MacroName) ? null : draft.MacroName!.Trim();
        if (macro is not null && !await MacroExistsAsync(macro, ct).ConfigureAwait(false))
            throw new AiException(AiErrors.Of(AiErrors.InvalidInput,
                $"unknown macro: {macro}",
                details: System.Text.Json.JsonSerializer.SerializeToElement(new { field = "macro_name" })));
        var saved = _skillStore.Save(draft with { MacroName = macro });
        LpLog.Debug($"skill saved: {saved.Name}", category: "ai.skill");
        return saved;
    }

    public Task DeleteSkillAsync(string skillId, CancellationToken ct = default)
    {
        if (_skillStore.Delete(skillId))
            LpLog.Debug($"skill deleted: {skillId}", category: "ai.skill");
        return Task.CompletedTask;
    }

    public async Task RunSkillAsync(string sessionId, string skillId,
        IReadOnlyDictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var skill = _skillStore.Get(skillId)
                    ?? throw new AiException(AiErrors.Of(AiErrors.InvalidInput, "unknown skill",
                        details: System.Text.Json.JsonSerializer.SerializeToElement(new { field = "skill_id" })));
        var text = AiSkillStore.Render(skill.PromptTemplate, parameters);
        if (skill.MacroName is { Length: > 0 } macro)
            text += $"\n\nBound macro: `{macro}` - run it with the macro.run tool when the plan is confirmed.";
        await SendAsync(sessionId, text,
                new AiTurnContext(NavId: "ai", LanguageCode: CultureInfo.CurrentUICulture.TwoLetterISOLanguageName))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListMacroNamesAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await _client.QueryAsync<object>("macro.list", null,
                    new CallOptions(Caller: new CallerRef(CallerKind.Agent, null)), ct)
                .ConfigureAwait(false);
            var element = ToElement(data);
            // 引擎 macro.list 的形状：{ macros: [{ name, updated_at }] }
            var items = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("macros", out var macros)
                ? macros
                : element;
            var names = new List<string>();
            foreach (var item in AiJsonWalk.EnumerateArrayOrEmpty(items))
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } value)
                    names.Add(value);
            }
            return names.OrderBy(n => n, NameOrder.Comparer).ToArray();
        }
        catch (EngineException ex)
        {
            LpLog.Warn("macro list lookup failed (skill editor)", ex, category: "ai.skill");
            return [];
        }
    }

    private async Task<bool> MacroExistsAsync(string name, CancellationToken ct)
    {
        try
        {
            await _client.QueryAsync<object>("macro.get", new { name },
                    new CallOptions(Caller: new CallerRef(CallerKind.Agent, null)), ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (EngineException ex) when (ex.Error.Code == EngineErrors.EntityNotFound)
        {
            return false;
        }
    }
}
