using System.Collections.ObjectModel;
using System.ComponentModel;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.ViewModels;

namespace LinkPocket.UI.Ai;

/// <summary>
/// AI 页 VM 的技能面（输入区技能条 + 技能编辑器 + 参数行；口径见功能书 §5.4）：
/// 技能 = 名称 + 说明 + 提示模板（`{参数}` 占位）+ 可选绑定的宏；运行走 <see cref="IAiAssistant.RunSkillAsync"/>
/// （渲染成用户消息、正常回合与审批链）。技能与宏名都是本地文件 / 引擎只读面，加载失败如实暴露。
/// </summary>
public sealed partial class AiViewModel
{
    private bool _isSkillEditorOpen;
    private string _skillName = "";
    private string _skillDescription = "";
    private string _skillTemplate = "";
    private string? _skillMacro;
    private string? _editingSkillId;
    private string _skillEditorErrorKey = "";

    private bool _isSkillRunOpen;
    private AiSkill? _runningSkill;

    /// <summary>技能条（有技能才显示；顺序 = 存储层名称序）。</summary>
    public ObservableCollection<AiSkill> Skills { get; } = [];

    /// <summary>绑定宏候选（数据源 = 引擎 <c>macro.list</c>）。</summary>
    public ObservableCollection<string> MacroNames { get; } = [];

    /// <summary>待填的技能参数（运行有占位的技能时展开）。</summary>
    public ObservableCollection<AiSkillParameter> SkillParameters { get; } = [];

    public bool HasSkills => Skills.Count > 0;

    public bool IsSkillEditorOpen
    {
        get => _isSkillEditorOpen;
        private set => Set(ref _isSkillEditorOpen, value, nameof(IsSkillEditorOpen));
    }

    /// <summary>编辑器标题（含变量的整句由 VM 出 LocValue，渲染边界取词）。</summary>
    public LocValue SkillEditorTitleValue
        => _editingSkillId is null ? Loc.K("ai.skill.editor.new") : Loc.K("ai.skill.editor.edit");

    public string SkillName
    {
        get => _skillName;
        set
        {
            if (!Set(ref _skillName, value, nameof(SkillName))) return;
            Raise(nameof(CanSaveSkill));
            CommandRefresh.Request();
        }
    }

    public string SkillDescription
    {
        get => _skillDescription;
        set => Set(ref _skillDescription, value, nameof(SkillDescription));
    }

    public string SkillTemplate
    {
        get => _skillTemplate;
        set
        {
            if (!Set(ref _skillTemplate, value, nameof(SkillTemplate))) return;
            Raise(nameof(CanSaveSkill));
            CommandRefresh.Request();
        }
    }

    public string? SkillMacro
    {
        get => _skillMacro;
        set => Set(ref _skillMacro, value, nameof(SkillMacro));
    }

    public string SkillEditorErrorKey
    {
        get => _skillEditorErrorKey;
        private set => Set(ref _skillEditorErrorKey, value, nameof(SkillEditorErrorKey));
    }

    public bool CanSaveSkill => SkillName.Trim().Length > 0 && SkillTemplate.Trim().Length > 0;

    public bool IsSkillRunOpen
    {
        get => _isSkillRunOpen;
        private set => Set(ref _isSkillRunOpen, value, nameof(IsSkillRunOpen));
    }

    public string RunningSkillName => _runningSkill?.Name ?? "";

    /// <summary>进页 / 保存 / 删除后刷新技能条（损坏如实暴露）。</summary>
    public async Task RefreshSkillsAsync()
    {
        try
        {
            var skills = await _assistant.ListSkillsAsync().ConfigureAwait(true);
            Skills.Clear();
            foreach (var skill in skills) Skills.Add(skill);
            Raise(nameof(HasSkills));
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>打开技能编辑器（<paramref name="preselectedTemplate"/> = 从助手消息「存为技能」预填的正文）。</summary>
    public async Task OpenSkillEditorAsync(AiSkill? skill, string? preselectedTemplate = null)
    {
        _editingSkillId = skill?.SkillId;
        SkillName = skill?.Name ?? "";
        SkillDescription = skill?.Description ?? "";
        SkillTemplate = skill?.PromptTemplate ?? preselectedTemplate ?? "";
        SkillMacro = skill?.MacroName;
        SkillEditorErrorKey = "";
        Raise(nameof(SkillEditorTitleValue));
        IsSkillEditorOpen = true;
        await RefreshMacroNamesAsync().ConfigureAwait(true);
    }

    public void CancelSkillEditor()
    {
        IsSkillEditorOpen = false;
        SkillEditorErrorKey = "";
    }

    public async Task SaveSkillAsync()
    {
        if (!CanSaveSkill) return;
        try
        {
            var saved = await _assistant.SaveSkillAsync(new AiSkillDraft(_editingSkillId, SkillName.Trim(),
                SkillDescription.Trim(), SkillTemplate.Trim(), SkillMacro)).ConfigureAwait(true);
            IsSkillEditorOpen = false;
            SkillEditorErrorKey = "";
            await RefreshSkillsAsync().ConfigureAwait(true);
            Notice(Loc.K("ai.skill.saved", saved.Name));
        }
        catch (AiException ex)
        {
            SkillEditorErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    public async Task DeleteSkillAsync(AiSkill skill)
    {
        try
        {
            await _assistant.DeleteSkillAsync(skill.SkillId).ConfigureAwait(true);
            await RefreshSkillsAsync().ConfigureAwait(true);
            Notice(Loc.K("ai.skill.deleted", skill.Name));
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>点技能 chip：无 `{参数}` 直接运行；有则展开参数行等填。</summary>
    public void BeginRunSkill(AiSkill skill)
    {
        if (skill.Parameters is { Count: > 0 } parameters)
        {
            _runningSkill = skill;
            SkillParameters.Clear();
            foreach (var name in parameters) SkillParameters.Add(new AiSkillParameter(name));
            Raise(nameof(RunningSkillName));
            IsSkillRunOpen = true;
            return;
        }
        _runningSkill = skill;
        _ = RunSkillAsync();
    }

    public void CancelSkillRun()
    {
        _runningSkill = null;
        SkillParameters.Clear();
        IsSkillRunOpen = false;
    }

    /// <summary>运行技能（渲染模板 → 作为用户消息发起回合；回合在跑时由 AI 层如实拒绝）。</summary>
    public async Task RunSkillAsync()
    {
        if (_runningSkill is not { } skill || _activeSessionId is not { } sessionId) return;
        var parameters = SkillParameters.Count == 0
            ? null
            : SkillParameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        CancelSkillRun();
        try
        {
            await _assistant.RunSkillAsync(sessionId, skill.SkillId, parameters).ConfigureAwait(true);
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    private async Task RefreshMacroNamesAsync()
    {
        try
        {
            var names = await _assistant.ListMacroNamesAsync().ConfigureAwait(true);
            MacroNames.Clear();
            foreach (var name in names) MacroNames.Add(name);
        }
        catch (AiException ex)
        {
            LastErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }
}

/// <summary>技能参数行（一个 `{占位}` = 一个输入框；值原样替换进模板）。</summary>
public sealed class AiSkillParameter(string name) : INotifyPropertyChanged
{
    private string _value = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; } = name;

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
}
