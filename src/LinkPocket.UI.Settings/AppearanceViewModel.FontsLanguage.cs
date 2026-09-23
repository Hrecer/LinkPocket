using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using LinkPocket.Contracts;
using LinkPocket.Theming;
using LinkPocket.Theming.Color;
using LinkPocket.Theming.Fonts;
using LinkPocket.Theming.Preferences;
using LinkPocket.Theming.Themes;
using LinkPocket.Theming.Tokens;
using Material3.Core;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels;

public sealed partial class AppearanceViewModel : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>
    /// 重新装载字体候选（导入 / 删除 / 恢复默认 / 切来源后调用）—— **后台枚举 + 投影**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>唯一装载路径</b>：候选集合只在 <see cref="ReloadFontsAsync"/> 里被重写，
    /// 导入/删除/恢复默认/切来源都汇到它 —— 不给自己留"顺手再拼一次列表"的第二条路。
    /// </para>
    /// <para>
    /// <b>按来源分池</b>：两个来源各自建集合，下拉只显示**当前来源**那一份 ——
    /// 在"系统已装"里绝不会误删到应用自己的东西（系统字体根本删不掉），在"自定义"里能导入/删除。
    /// </para>
    /// </remarks>
    public async Task ReloadFontsAsync()
    {
        var currentUi = ThemeService.CurrentUiFont;

        var all = await FontCatalog.LoadAsync().ConfigureAwait(true);

        UiFonts.Clear();
        SystemUiFonts.Clear();
        CustomUiFonts.Clear();
        foreach (var f in all)
        {
            var option = new FontOptionViewModel(f);
            var pool = option.IsImported ? CustomUiFonts : SystemUiFonts;
            pool.Add(option);
        }
        ProjectCurrentFonts(currentUi);
    }

    /// <summary>把"当前来源"那一份灌进下拉（切换来源 / 装载完成后调用）。</summary>
    private void ProjectFontPools()
    {
        var source = FontSource == FontSourceKind.System ? SystemUiFonts : CustomUiFonts;
        UiFonts.Clear();
        foreach (var f in source) UiFonts.Add(f);
    }

    /// <summary>
    /// 把"当前已应用字体"投影到选中项（找不到同名就落到默认字体）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>候选还没装载时也要显示当前字体</b>：字体候选是**惰性**的（进页面不枚举系统字体，
    /// 真正展开下拉才装载），而下拉的 `SelectedItem` 原先只在"候选里找得到同名项"时才被赋值 ——
    /// 于是装载之前下拉框**空白**（看起来像"拉不到任何字体"，其实只是"还没去拉"）。
    /// </para>
    /// <para>
    /// 判据修正：**当前生效的字体必须始终显示在框里**，无论候选是否已装载。
    /// 候选里没有同名项（尚未装载 / 字体已卸载但偏好还留着）就用当前族名造一个**占位项**补上，
    /// 装载完成后 <see cref="ReloadFontsAsync"/> 会再投影一次，占位项自然被真候选替换。
    /// 注意占位项的 <see cref="FontOptionViewModel.Choice"/> 带 <c>FilePath = null</c>（= 系统字体），
    /// 因此它**不可删** —— 与"系统字体只读"同一条口径。
    /// </para>
    /// <para>
    /// <b>占位项只补"属于当前来源"的那一个</b>：早期实现把当前族名无条件塞进**当前来源**的池 ——
    /// 于是切到「自定义字体」时，一个**系统字体**（比如 Microsoft YaHei UI）会顶着"当前字体"
    /// 的名义出现在自定义列表里。
    /// 现行判据：该族是不是**导入过的**（<see cref="FontCatalog.ImportedFonts"/>，读字体目录、不枚举系统字体）；
    /// 不属于本来源就**什么都不补** —— 自定义侧没导入过就是空下拉（如实，不拿系统字体冒充）。
    /// </para>
    /// </remarks>
    private void ProjectCurrentFonts(string currentUi)
    {
        // 候选还没装载（池为空）时先补一个**占位项**：下拉框里必须始终看得见"现在用的是什么字体"，
        // 否则空白框会被读成"拉取不到任何字体"。
        // ⚠️ 占位项要真的进集合：combo 的 `SelectedItem` 指向一个**不在 Items 里**的对象时
        //    WPF 会把它显示成**空白**（实测：SelectedItem 是占位项、SelectedIndex 却是 -1、框里没字）。
        EnsureActivePlaceholder(currentUi);
        // 补完必须**重新镜像可见池**：占位项落在"它自己那一侧"的池里，而可见池是那一侧的副本，
        // 不重镜像就会出现"选中项不在 Items 里"（=上面那条空洞）。
        ProjectFontPools();

        // 池被重建过（清空再补齐）：控件内部的 SelectedIndex 会停在 -1，而 SelectedItem 仍指着旧对象 ——
        // 框里画的是空白。**同值重写不算变化**（实测：SelectedItem ∈ Items、同一实例、SelectedIndex 仍是 -1），
        // 所以先置空再赋值：绑定才会把"池里的那一项"重新推进控件（先投影、后播报，仍由本 VM 单驱动）。
        _selectedUiFont = null;
        Raise(nameof(SelectedUiFont));
        SelectedUiFont = PickOrPlaceholder(UiFonts, currentUi, ThemeService.DefaultUiFont);

        static FontOptionViewModel? PickOrPlaceholder(
            ObservableCollection<FontOptionViewModel> pool, string current, string fallbackFamily)
        {
            var family = string.IsNullOrWhiteSpace(current) ? fallbackFamily : current;
            return pool.FirstOrDefault(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase))
                   ?? pool.FirstOrDefault(f => string.Equals(f.Family, fallbackFamily, StringComparison.OrdinalIgnoreCase))
                   ?? pool.FirstOrDefault();
        }

        // 把当前生效的字体补进**它自己那一侧**的池（候选装载后同名项已存在 → 不重复添加）
        void EnsureActivePlaceholder(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return;

            var imported = FontCatalog.ImportedFonts()
                .Any(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase));
            var belongsToCurrentSource = FontSource == FontSourceKind.Custom ? imported : !imported;
            if (!belongsToCurrentSource) return;   // 不属于本来源 → 不塞（自定义里绝不出现系统字体）

            var target = FontSource == FontSourceKind.System ? SystemUiFonts : CustomUiFonts;
            if (target.All(f => !string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)))
                target.Add(new FontOptionViewModel(new FontChoice(family, family)));

            UiFonts.Clear();
            foreach (var f in target) UiFonts.Add(f);
        }
    }

    /// <summary>同步重载（仅测试与"已经不持有 UI 上下文"的收尾路径用；界面一律走异步版）。</summary>
    /// <remarks>
    /// 存在的理由是**可测性**：单元测试要断言投影结果，而 <c>async void</c> 式的入口无法 await。
    /// 它不构成第二套实现 —— 装载本身仍然只有 <see cref="FontCatalog.LoadAsync"/> 一条路。
    /// </remarks>
    public void ReloadFonts() => ReloadFontsAsync().GetAwaiter().GetResult();

    /// <summary>
    /// 导入一个字体文件（失败明确报错 + 留痕 + **不写偏好**）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 失败**必须在界面上可见**：这里把 <see cref="Status"/> 写成"导入失败：…"，
    /// 面板把它显示在状态行上（N1 要求"导入失败的用户可见反馈"）。
    /// 只写日志不播报 = 点了"导入字体…"什么都没发生 —— 那是本仓禁止的静默失败。
    /// </remarks>
    public async Task<bool> ImportFontAsync(string path)
    {
        try
        {
            var choice = FontCatalog.Import(path);
            // 导入的字体属于"自定义"这一侧：**切过去再选中它**（否则"系统"列表里看不到刚导入的东西）。
            // 走 <see cref="FontSource"/> 属性而不是手写一遍 Raise —— 切来源只有一条流水线。
            FontSource = FontSourceKind.Custom;

            await ReloadFontsAsync().ConfigureAwait(true);
            SelectedUiFont = UiFonts.FirstOrDefault(f => string.Equals(f.Family, choice.Family, StringComparison.OrdinalIgnoreCase))
                             ?? SelectedUiFont;
            // 导入成功不播报 ——「自定义字体」侧的下拉出现该项就是结果；失败照报
            Status = LocValue.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LpLog.Error($"font import failed: {path}", ex, LogCategory);
            Status = Loc.K("backup.importFailed");
            return false;
        }
    }

    /// <summary>删除一个已导入字体（**只删本应用目录里那份副本**，系统字体不可删也不该删）。</summary>
    /// <remarks>
    /// <b>删除后的三处收尾，一个都不能少</b>：
    /// ① 删文件（<see cref="FontCatalog.Remove"/>）；② 若删的正是**当前生效**的字体 →
    /// <see cref="ThemeService.ResetFontIfDeleted"/> 退回默认并落盘（否则偏好里留着一个不存在的族名，
    /// 重启弹"已不可用"、再点应用还会把死族名写回去）；③ 重载候选并刷新按钮可用性。
    /// </remarks>
    public async Task DeleteFontAsync(FontOptionViewModel option)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (!option.CanDelete)
        {
            Status = Loc.K("font.systemNotDeletable");
            return;
        }
        try
        {
            var deleted = FontCatalog.Remove(option.Choice);
            var fellBack = ThemeService.ResetFontIfDeleted(option.Family);
            await ReloadFontsAsync().ConfigureAwait(true);
            // 成功不播报；但"删的正是当前生效字体 → 已回退默认"是**必须知道**的一件事
            // （不只是删了一个候选，而是界面字体变了），故这一类照报。
            Status = fellBack is null
                ? LocValue.Empty
                : Loc.K("appearance.font.deletedActive", option.Family,
                    deleted ? Loc.K("appearance.font.deletedFileGone") : Loc.K("appearance.font.deletedFileKept"));
        }
        catch (Exception ex)
        {
            // WPF 会把解析过的字体文件**内存映射持有到进程退出**（WARNINGS 75）——这是平台事实，
            // 不是"路径写错了"：必须把"下一步怎么办"讲清楚，而不是原样丢一个"访问被拒绝"。
            LpLog.Error($"failed to delete imported font: {option.Family}", ex, LogCategory);
            Status = ex is UnauthorizedAccessException
                ? Loc.K("appearance.font.deleteInUse", option.Family)
                : Loc.K("appearance.font.deleteFailed");
        }
    }

    /// <summary>应用当前选中的界面字体（含持久化）。</summary>
    /// <remarks>
    /// 等宽字体**不再可改**：面板动作不碰它，把当前值原样带过去
    /// （<c>ApplyFonts</c> 的 mono 参数收到 null 会回默认族）。
    /// </remarks>
    public void ApplyFonts()
    {
        var ui = SelectedUiFont?.Family ?? ThemeService.DefaultUiFont;

        try
        {
            ThemeService.ApplyFonts(ui, ThemeService.CurrentMonoFont);
            ThemeService.SaveCurrentPreferences();
            // 成功不播报 —— 界面本身已经换成新字体，那就是结果；失败照报
            Status = LocValue.Empty;
        }
        catch (Exception ex)
        {
            LpLog.Error("failed to save preferences after applying fonts", ex, LogCategory);
            Status = Loc.K("appearance.status.fontAppliedPrefFailed");
        }
    }

    /// <summary>
    /// 把界面字体**恢复默认族**（并落盘）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="ApplyFonts"/>（应用"当前选中"）是两件事：这里不读下拉的选中项，
    /// 直接走 <see cref="ThemeService.ApplyFonts(string?, string?, System.Windows.ResourceDictionary?)"/>
    /// 的**无参形态**——它取 <see cref="ThemeService.DefaultUiFont"/>：
    /// <b>当前语言的默认族</b>（中文 <c>Microsoft YaHei UI</c> / 英文 <c>Segoe UI</c>，决策 5）。
    /// </para>
    /// <para>
    /// <b>落盘的写法是有意的</b>：<c>SaveCurrentPreferences</c> 把"等于当前语言默认族"归一成空 ——
    /// 于是"恢复默认字体"落成<b>没选过</b>，之后切语言时默认族才会跟着语言走；
    /// 若写成具体族名，它会被当成"用户显式选过的族"（用户选择优先于语言）。
    /// </para>
    /// <para>
    /// 等宽字体已不再可改 —— 无参调用顺手把它也归默认，这正是"恢复默认字体"该做的。
    /// </para>
    /// <para>
    /// 收尾三条与"删除当前生效字体"同口径：应用 → 落盘 → 候选重载 + 当前值重投影
    /// （候选没装载时用占位项投影，下拉里照样立刻显示默认族 —— 不能假设"会先展开一次下拉"）。
    /// </para>
    /// </remarks>
    public async Task ResetFontsAsync()
    {
        try
        {
            ThemeService.ApplyFonts();                 // 无参 = 当前语言的默认族（不读下拉选中项）
            ThemeService.SaveCurrentPreferences();     // 默认族归一成"没选过"（见上）
            if (_fontsLoaded) await ReloadFontsAsync().ConfigureAwait(true);
            else ProjectCurrentFonts(ThemeService.CurrentUiFont);
            // 成功不播报 —— 下拉里换回默认族就是结果；失败照报
            Status = LocValue.Empty;
        }
        catch (Exception ex)
        {
            LpLog.Error("failed to restore the default font", ex, LogCategory);
            Status = Loc.K("appearance.status.resetFailed");
        }
    }

    /// <summary>恢复默认外观（主题 + 字体一起回默认，并清掉偏好文件）。</summary>
    public async Task ResetToDefaultAsync()
    {
        try
        {
            LinkPocket.Theming.Preferences.UiPreferenceStore.Clear();
            ThemeService.ApplyDefault();
            ThemeService.ApplyFonts();
            ClearDraftCore();                 // 调色台回**空态** + 清Loc.K("common.draftUnapplied")（自选配色已随偏好一起清掉，
                                              // 留着色点会名不副实；默认外观 = 全新起点）。
                                              // ⚠️ 走**纯草稿**路径：公有的 ClearDraft 会回默认主题并落盘，
                                              //    在这里会把刚清掉的偏好文件又写回来（本方法的口径 = 偏好文件也要清）
            SyncFromAppliedTheme();           // 互斥归属 + 主题卡 + 草稿一起回默认（唯一投影点）
            if (_fontsLoaded) await ReloadFontsAsync().ConfigureAwait(true);
            DiagnosticLines = Array.Empty<DiagnosticLine>();
            HasDiagnostics = false;
            // 成功不播报 —— 界面回到默认就是结果；失败照报（catch 里那条）
            Status = LocValue.Empty;
        }
        catch (Exception ex)
        {
            LpLog.Error("failed to restore the default appearance", ex, LogCategory);
            Status = Loc.K("appearance.status.resetFailed");
        }
    }

    /// <summary>
    /// 字体度量自检的提示（在容差内 = <see cref="LocValue.Empty"/>）。只提示、不阻止应用。
    /// 本层给的是<b>句子 + 数字</b>，不是成品文本——语言一切换它跟着重算。
    /// </summary>
    /// <remarks>
    /// 基准 = <see cref="ThemeService.DefaultUiFont"/>（<b>当前语言的默认族</b>）：
    /// 自检问的是"这个字体比本语言的基准宽/高多少"，拿另一种语言的默认族当基准，
    /// 会把中英默认族本身的宽度差算进结论里（默认族自己检自己却不等于 0）。
    /// </remarks>
    public LocValue InspectFont(FontOptionViewModel option)
    {
        var verdict = FontMetricsProbe.Inspect(
            FontCatalog.BuildTokenValue(option.Family), Loc.T("metric.sample"), ThemeService.DefaultUiFont);
        return verdict.Code switch
        {
            FontMetricsProbe.Verdict.TooWide => Loc.K("appearance.font.tooWide", Math.Abs(verdict.WidthDelta) * 100),
            FontMetricsProbe.Verdict.TooTall => Loc.K("appearance.font.tooTall", verdict.HeightRatio),
            _ => LocValue.Empty,
        };
    }

    /// <summary>当前选中字体的度量提示（空 = 不显示）；<b>由 <see cref="SelectedUiFont"/> 投影出来</b>。</summary>
    public LocValue FontInspection
    {
        get => _selectedUiFont is null ? LocValue.Empty : InspectFont(_selectedUiFont);
    }

    /// <summary>有没有度量提示（决定提示框可见性；与 <see cref="FontInspection"/> 同源）。</summary>
    public bool HasFontInspection => !FontInspection.IsEmpty;

    // ── 语言卡（跟随系统 / 简体中文 / English） ───────────────────────
    // 三枚分段的取值域是"模式 + 语言"两件事，但对用户只是一个三选一；索引口径见 SetLanguage。

    /// <summary>语言分段的选中索引：<c>0</c> = 跟随系统、<c>1</c> = 简体中文、<c>2</c> = English。</summary>
    /// <remarks>
    /// <b>投影（读）与写入（切）是两个方向</b>：读的是偏好里"用户选了什么"
    /// （<see cref="ThemeService.LanguageMode"/> + <see cref="ThemeService.LanguageOverride"/>），
    /// 写的是一次真实切换（<see cref="SetLanguage"/>）。<b>把"当前生效语言"画成选中项是错的</b>——
    /// 跟随系统时生效的可能是英文，但用户选的是"跟随系统"，分段必须停在第一枚。
    /// </remarks>
    public int LanguageIndex
    {
        get => ProjectLanguageIndex();
        set
        {
            if (value == ProjectLanguageIndex()) return;   // 同值重写不算变化（分段控件会回写）
            SetLanguage(value);
        }
    }

    /// <summary>语言卡的说明句（两段，随当前界面语言变）。</summary>
    public LocValue LanguageHint => Loc.K("appearance.language.hint");

    /// <summary>语言卡的次要说明（"不影响已存数据"那一段）。</summary>
    public LocValue LanguagePathNote => Loc.K("appearance.language.pathNote");

    /// <summary>分段三枚的文案键（跟随系统 / 简体中文 / English）——由 XAML 用 <c>{loc:Loc …}</c> 直接取，故不在此处暴露。</summary>
    private static int ProjectLanguageIndex()
    {
        if (ThemeService.LanguageMode != LocalePreference.ModeFixed) return 0;
        if (!AppLocales.TryParse(ThemeService.LanguageOverride, out var fixedLocale)) return 0;
        return ToLanguageIndex(fixedLocale);
    }

    /// <summary>语言 → 分段索引（加一门语言时这一处 + <see cref="FromLanguageIndex"/> 一起改）。</summary>
    private static int ToLanguageIndex(AppLocale locale) => locale switch
    {
        AppLocale.En => 2,
        _ => 1,
    };

    /// <summary>分段索引 → 语言（<c>0</c> 是"跟随系统"，不是某门语言，故返回 false）。</summary>
    private static bool FromLanguageIndex(int index, out AppLocale locale)
    {
        switch (index)
        {
            case 1: locale = AppLocale.ZhCn; return true;
            case 2: locale = AppLocale.En; return true;
            default: locale = AppLocales.Default; return false;
        }
    }

    /// <summary>
    /// 切换界面语言（分段的唯一写入入口）：改偏好 → 生效 → 换表 → 落盘。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>生效路径只有一条</b>：<see cref="LocaleService.Apply"/> 换表并让版本 +1，
    /// 所有取词绑定（含 <c>{loc:FitValue}</c> 这类自适应通道）当场重算——本方法<b>不做任何"重投影"</b>。
    /// 宿主级副作用（把语言码登记给 <c>ThemeService</c>、写回偏好）由组合根的
    /// <c>LocaleService.LanguageChanged</c> 那一处完成。
    /// </para>
    /// <para>
    /// <b>没显式选过字体时，界面字体跟着换成新语言的默认族</b>：默认族按语言给
    /// （中文 <c>Microsoft YaHei UI</c> / 英文 <c>Segoe UI</c>）。判据是"偏好里存的是不是空"，
    /// 不是"当前族等不等于某个默认族"——用户显式选过的族跨语言不变（用户选择优先于语言）。
    /// </para>
    /// <para>
    /// <b>失败要暴露</b>：语言已按新值生效、但偏好没落盘（下次启动会退回旧语言）——
    /// 这一类必须在状态行说清，不能只写日志。异常消息是引擎/运行时的英文散文，
    /// <b>不上屏</b>（界面只按码说话），原文进日志。
    /// </para>
    /// </remarks>
    public void SetLanguage(int index)
    {
        var (mode, overrideCode) = FromLanguageIndex(index, out var target)
            ? target.FixedPreference()
            : AppLocales.FollowSystemPreference();
        var fontTouched = false;

        try
        {
            ThemeService.SetLanguagePreference(mode, overrideCode);
            // **先登记语言码，再换表**：登记是"默认字体族按语言给"的输入，
            // 而换表会触发宿主的副作用（`LanguageChanged` → 写回偏好）——那一步里就要用到它。
            // 顺序反了会出现"语言已换、默认族还是上一种语言的"（偏好里被写上旧语言的默认族）。
            ThemeService.SetActiveLanguage(target.CodeOf());
            // 生效（换表 + 版本 +1；宿主副作用经 `LanguageChanged` 完成：再登记一次 + 写回偏好）
            LocaleService.Apply(target);

            // 字体跟着语言走 —— 仅当用户**没显式选过**（偏好里的 Ui 为空）。
            // 用户显式选过的族跨语言不变（用户选择优先于语言）。
            if (UiPreferenceStore.ExplicitUiFont() is null)
            {
                ThemeService.ApplyFonts();          // 无参 = 当前语言的默认族（语言码已登记）
                ThemeService.SaveCurrentPreferences();
                fontTouched = true;
            }

            // 先投影、后播报：语言与字体都已就位，监听方读到的是完整状态
            Raise(nameof(LanguageIndex));
            if (fontTouched) ProjectCurrentFonts(ThemeService.CurrentUiFont);
            Status = LocValue.Empty;                // 成功不播报：界面本身已经换成新语言，那就是结果
        }
        catch (Exception ex)
        {
            LpLog.Error($"failed to switch the interface language (index={index})", ex, LogCategory);
            Status = Loc.K("appearance.status.languageFailed");
            Raise(nameof(LanguageIndex));           // 失败也要把分段投影回**事实**（偏好里的选择）
        }

        Raise(nameof(LanguageHint));
        Raise(nameof(LanguagePathNote));
        Raise(nameof(FontSourceHint));
    }
}
