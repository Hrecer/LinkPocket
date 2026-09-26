using System.Collections.ObjectModel;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.ViewModels;

namespace LinkPocket.UI.Ai;

/// <summary>
/// AI 页 VM 的宏管理面（2026-09-26；此前宏只有引擎命令、只能靠对话让 AI 操作）：
/// 清单（macro.list）+ 脚本查看（macro.get）+ 新建/编辑（macro.save，JSON 文本校验交给引擎）
/// + 删除（macro.delete）+ 运行（macro.run，事务批语义）。与技能 chips 同区入口。
/// </summary>
public sealed partial class AiViewModel
{
    private bool _isMacroPanelOpen;
    private string? _selectedMacroName;
    private bool _isMacroEditing;
    private string? _editingMacroOriginalName;   // null = 新建
    private string _macroDraftName = "";
    private string _macroDraftScript = "";
    private string _macroErrorKey = "";

    /// <summary>宏清单（名称 + 更新时间；脚本按需拉取，不随清单搬运）。</summary>
    public ObservableCollection<AiMacroInfo> Macros { get; } = [];

    public bool IsMacroPanelOpen
    {
        get => _isMacroPanelOpen;
        private set => Set(ref _isMacroPanelOpen, value, nameof(IsMacroPanelOpen));
    }

    /// <summary>当前选中（查看/编辑目标）的宏名；null = 没选中。
    /// setter 公开：ListBox 的 SelectedValue 以 TwoWay 绑定（只读属性会在 XAML 加载时直接炸启动）。</summary>
    public string? SelectedMacroName
    {
        get => _selectedMacroName;
        set => Set(ref _selectedMacroName, value, nameof(SelectedMacroName));
    }

    /// <summary>选中宏的脚本原文（macro.get 的原文；查看与"载入编辑"共用）。</summary>
    public string SelectedMacroScript { get; private set; } = "";

    public bool IsMacroEditing
    {
        get => _isMacroEditing;
        private set => Set(ref _isMacroEditing, value, nameof(IsMacroEditing));
    }

    /// <summary>编辑器标题（新建 / 编辑：整句由 VM 出 LocValue）。</summary>
    public LocValue MacroEditorTitleValue => _editingMacroOriginalName is null
        ? Loc.K("ai.macro.editor.new")
        : Loc.K("ai.macro.editor.edit", _editingMacroOriginalName);

    public string MacroDraftName
    {
        get => _macroDraftName;
        set
        {
            if (!Set(ref _macroDraftName, value, nameof(MacroDraftName))) return;
            Raise(nameof(CanSaveMacro));
            CommandRefresh.Request();
        }
    }

    public string MacroDraftScript
    {
        get => _macroDraftScript;
        set
        {
            if (!Set(ref _macroDraftScript, value, nameof(MacroDraftScript))) return;
            Raise(nameof(CanSaveMacro));
            CommandRefresh.Request();
        }
    }

    public bool CanSaveMacro => MacroDraftName.Trim().Length > 0 && MacroDraftScript.Trim().Length > 0;

    /// <summary>面板底部的一行错误（JSON 语法 / 引擎拒绝的本地化键）；空 = 无错。</summary>
    public string MacroErrorKey
    {
        get => _macroErrorKey;
        private set => Set(ref _macroErrorKey, value, nameof(MacroErrorKey));
    }

    /// <summary>打开宏面板（进页即拉清单；失败如实暴露在错误行）。</summary>
    public async Task OpenMacroPanelAsync()
    {
        IsMacroPanelOpen = true;
        await RefreshMacrosAsync().ConfigureAwait(true);
    }

    public void CloseMacroPanel()
    {
        IsMacroPanelOpen = false;
        CloseMacroEditor();
        SelectedMacroName = null;
        SelectedMacroScript = "";
        Raise(nameof(SelectedMacroScript));
    }

    /// <summary>刷新清单（进面板 / 保存 / 删除后）。</summary>
    public async Task RefreshMacrosAsync()
    {
        try
        {
            var macros = await _assistant.ListMacrosAsync().ConfigureAwait(true);
            Macros.Clear();
            foreach (var macro in macros) Macros.Add(macro);
            MacroErrorKey = "";
        }
        catch (Exception)
        {
            MacroErrorKey = "err.unexpected";
        }
    }

    /// <summary>选中宏 → 拉脚本原文展示（拉取失败清空并如实报错）。</summary>
    public async Task SelectMacroAsync(string? name)
    {
        if (IsMacroEditing) return;   // 编辑中不跟随选择（防丢稿）
        SelectedMacroName = name;
        if (name is null)
        {
            SelectedMacroScript = "";
            Raise(nameof(SelectedMacroScript));
            return;
        }
        try
        {
            SelectedMacroScript = await _assistant.GetMacroScriptAsync(name).ConfigureAwait(true);
            MacroErrorKey = "";
        }
        catch (Exception)
        {
            SelectedMacroScript = "";
            MacroErrorKey = "err.unexpected";
        }
        Raise(nameof(SelectedMacroScript));
    }

    /// <summary>进入编辑（载入选中宏的脚本；<paramref name="name"/> null = 新建）。</summary>
    public async Task BeginMacroEditorAsync(string? name)
    {
        _editingMacroOriginalName = name;
        MacroDraftName = name ?? "";
        MacroDraftScript = name is null ? "{\n  \"steps\": []\n}" : "";
        if (name is not null)
        {
            try
            {
                MacroDraftScript = await _assistant.GetMacroScriptAsync(name).ConfigureAwait(true);
            }
            catch (Exception)
            {
                MacroErrorKey = "err.unexpected";
                return;
            }
        }
        MacroErrorKey = "";
        IsMacroEditing = true;
        Raise(nameof(MacroEditorTitleValue));
    }

    public void CloseMacroEditor()
    {
        IsMacroEditing = false;
        MacroErrorKey = "";
    }

    /// <summary>保存（JSON 语法本地先验一次；引擎再校验脚本结构与命令合法性，无效拒绝入库）。</summary>
    public async Task SaveMacroAsync()
    {
        if (!CanSaveMacro) return;
        var name = MacroDraftName.Trim();
        try
        {
            await _assistant.SaveMacroAsync(name, MacroDraftScript).ConfigureAwait(true);
            IsMacroEditing = false;
            MacroErrorKey = "";
            await RefreshMacrosAsync().ConfigureAwait(true);
            await SelectMacroAsync(name).ConfigureAwait(true);
            Notice(Loc.K("ai.macro.saved", name));
        }
        catch (System.Text.Json.JsonException)
        {
            MacroErrorKey = "ai.macro.error.json";
        }
        catch (Exception)
        {
            MacroErrorKey = "ai.macro.error.rejected";
        }
    }

    /// <summary>删除宏（确认后；与技能删除同口径——无撤销，但可重建）。</summary>
    public async Task DeleteMacroAsync(AiMacroInfo macro)
    {
        if (!LinkPocket.Views.ConfirmDialog.Show(
                Loc.T("ai.macro.delete.title"), Loc.T("ai.macro.delete.confirm", macro.Name),
                Loc.T("common.delete")))
            return;
        try
        {
            await _assistant.DeleteMacroAsync(macro.Name).ConfigureAwait(true);
            if (SelectedMacroName == macro.Name)
            {
                SelectedMacroName = null;
                SelectedMacroScript = "";
                Raise(nameof(SelectedMacroScript));
            }
            await RefreshMacrosAsync().ConfigureAwait(true);
            Notice(Loc.K("ai.macro.deleted", macro.Name));
        }
        catch (Exception)
        {
            MacroErrorKey = "err.unexpected";
        }
    }

    /// <summary>运行宏（引擎 macro.run：事务批，中止整体回滚；结果走 Notice 如实报告）。</summary>
    public async Task RunMacroAsync(AiMacroInfo macro)
    {
        try
        {
            await _assistant.RunMacroAsync(macro.Name).ConfigureAwait(true);
            Notice(Loc.K("ai.macro.ran", macro.Name));
        }
        catch (Exception)
        {
            Notice(Loc.K("ai.macro.runFailed", macro.Name));
        }
    }
}
