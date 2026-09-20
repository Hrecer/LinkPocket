using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using LinkPocket.Contracts;
using LinkPocket.Theming;
using LinkPocket.Theming.Color;
using LinkPocket.Theming.Fonts;
using LinkPocket.Theming.Themes;
using LinkPocket.Theming.Tokens;
using Material3.Core;

namespace LinkPocket.ViewModels;

/// <summary>一张主题卡（「外观」面板的预设网格项）。</summary>
public sealed class ThemeCardViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;

    public ThemeCardViewModel(ThemeDefinition definition)
    {
        Definition = definition;
        var table = PaletteSolver.Solve(definition);

        // 色点用**身份色**（"主题就是这些颜色"——方案 §4.2：主题卡展示用户原色，界面用其档位）。
        // 补位规则（预设只有 1–3 个身份色）= Theming 的唯一实现 `PaletteSolver.EditableSlots`，
        // 与外观面板的色槽同一份（否则"卡片 4 个点、色槽 3 格"迟早漂移）。
        Swatches = new ObservableCollection<Color>(BuildSwatches(definition));

        // 派生示意条：强调填充 / 页面底 / 正文 —— 一眼看出"这套主题长什么样"
        Accent = ToMedia(table.Token(AppTokens.AccentFill));
        Base = ToMedia(table.Token(AppTokens.SurfaceBase));
        Text = ToMedia(table.Token(AppTokens.TextPrimary));

        var f = table.Families;
        Summary = $"主色 H{f.AccentHue:F0} · 支撑 H{f.SupportHue:F0}";
    }

    /// <summary>
    /// 主题卡的身份色圆点：**唯一实现**在 Theming（`PaletteSolver.EditableSlots`）——
    /// 与外观面板的色槽共用同一套补位规则（预设身份色只有 1–3 个，直接展示会稀疏得像"缺了几个色"）。
    /// </summary>
    private IEnumerable<Color> BuildSwatches(ThemeDefinition definition) =>
        PaletteSolver.EditableSlots(definition).Select(ToMedia);

    /// <summary>Argb → WPF Color（唯一转换点在 Theming；界面层零颜色字面量）。</summary>
    private static Color ToMedia(Argb c) => ColorMath.ToMedia(c);

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public ThemeDefinition Definition { get; }

    public string Name => Definition.Name;

    public string Id => Definition.Id;

    /// <summary>是否出厂默认（带「默认」徽标，且永远排第一）。</summary>
    public bool IsDefault => Definition.Source == ThemeSource.FactoryDefault;

    /// <summary>
    /// 是否是**当前已应用**的主题（选中态的**唯一事实来源**）。
    /// </summary>
    /// <remarks>
    /// XAML 用 <c>DataTrigger</c> 直接读它——绝不在 code-behind 里"点了哪张就记住哪张"：
    /// 那是第二份状态，与 VM 的 <c>SelectedThemeId</c> 迟早不一致（架构不变量 12：唯一事实来源 + 投影）。
    /// </remarks>
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>身份色圆点。</summary>
    public ObservableCollection<Color> Swatches { get; }

    /// <summary>派生示意：强调填充。</summary>
    public Color Accent { get; }

    /// <summary>派生示意：页面底。</summary>
    public Color Base { get; }

    /// <summary>派生示意：正文。</summary>
    public Color Text { get; }

    /// <summary>一行摘要（主色/支撑色相）。</summary>
    public string Summary { get; }

}

/// <summary>一个色槽（4/5 色自选配色）。</summary>
public sealed class ColorSlotViewModel
{
    public ColorSlotViewModel(int index, Color color)
    {
        Index = index;
        Color = color;
    }

    public int Index { get; }

    public Color Color { get; }

    /// <summary>槽位序号文案（1 起）。</summary>
    public string Label => $"颜色 {Index + 1}";

    /// <summary>HEX 文案。</summary>
    public string Hex => $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";
}

/// <summary>一个字体选项（界面字体 / 等宽字体下拉项）。</summary>
public sealed class FontOptionViewModel
{
    public FontOptionViewModel(FontChoice choice)
    {
        Choice = choice;
        Display = choice.DisplayName;
        IsImported = choice.IsImported;
    }

    public FontChoice Choice { get; }

    public string Display { get; }

    public string Family => Choice.Family;

    public bool IsImported { get; }

    /// <summary>是否有文件路径（可删除）。</summary>
    public bool CanDelete => Choice.FilePath is not null;
}

/// <summary>
/// 「外观」面板视图模型：主题（预设卡 + 自选配色）+ 字体（界面/等宽 + 导入）。
/// </summary>
/// <remarks>
/// <para>
/// <b>职责边界</b>：本 VM 只管"面板的状态与动作"，**颜色计算全在 <c>LinkPocket.Theming</c>**
/// （经 <see cref="ThemeService"/> / <see cref="PaletteSolver"/> / <see cref="ThemeValidator"/>）。
/// 面板自己不碰 Hct / TonalPalette（架构护栏 <c>颜色计算只允许出现在Theming</c> 卡住）。
/// </para>
/// <para>
/// <b>草稿 vs 已应用</b>：预设卡**单击即应用**（既定成品）；自选配色走"草稿 + 应用"
/// （需要试），<c>Esc</c> 放弃草稿——与全站 Esc 语义一致（方案 §7.3）。
/// </para>
/// </remarks>
public sealed class AppearanceViewModel : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>自选配色的槽数上下限（方案 §7.3：UI 只开放 4/5）。</summary>
    public const int MinSlots = 4;

    /// <summary>自选配色槽数上限。</summary>
    public const int MaxSlots = 5;

    private const string LogCategory = "app.theme";

    private readonly List<Color> _draft = new();
    private string _selectedThemeId = ThemeCatalog.DefaultId;
    private FontOptionViewModel? _selectedUiFont;
    private FontOptionViewModel? _selectedMonoFont;
    private string _diagnostics = string.Empty;
    private string _status = string.Empty;
    private bool _hasDiagnostics;

    /// <summary>
    /// 当前生效外观的**归属**：true = 自选配色，false = 某个预设主题。
    /// </summary>
    /// <remarks>
    /// <b>互斥的唯一事实来源</b>：主题卡与自选区的高亮都由它 + <see cref="SelectedThemeId"/> 投影出来，
    /// 视图不持状态、不"点了哪边记哪边"（架构不变量 12）。用户报障"主题与自选配色没有二选一、
    /// 两个都亮着"的根因就是原先根本没有这个字段——自选区的"高亮"是按色槽数量推的。
    /// </remarks>
    private bool _customActive;

    /// <summary>草稿是否有**未应用**的改动（决定自选区显示「编辑中（未应用）」）。</summary>
    private bool _draftDirty;

    public AppearanceViewModel()
    {
        ThemeCards = new ObservableCollection<ThemeCardViewModel>(
            ThemeCatalog.All.Select(t => new ThemeCardViewModel(t)));

        // 入口对齐：面板显示"当前**已应用**的外观"——主题卡高亮 + 互斥归属 + 色槽草稿
        // （用户上次选的主题/配色要在他回到这一页时仍然是对的，否则选中态就是错的）
        SyncFromAppliedTheme();
    }

    /// <summary>
    /// **唯一投影点**：把"当前已应用的外观"投影到面板（互斥归属 + 主题卡选中态 + 色槽草稿）。
    /// </summary>
    /// <remarks>
    /// 两条硬性口径：
    /// ① 草稿 = **当前正在用的配色**（自选配色时就是它本身）；不是永远取出厂默认那 5 色——
    ///    否则正在用自选配色时重进面板，色槽显示的是别人的颜色（实测缺陷）；
    /// ② 草稿有**未应用改动**时不重新播种（入口刷新不许冲掉用户正在编辑的东西）。
    /// </remarks>
    public void SyncFromAppliedTheme()
    {
        var current = ThemeService.Current;
        _customActive = current.Source == ThemeSource.UserDefined;

        if (!_draftDirty)
        {
            // 色槽 = **当前主题的颜色**（用户令 2026-09-20："仪表盘是仪表盘、自选是自选、选择区域是选择区域；
            // 你默认的颜色也可以移到仪表盘里微调"）。预设只有 1–3 个身份色 → 用该主题自己的档位补足到 4
            // （唯一实现 = `PaletteSolver.EditableSlots`）。
            // ⚠️ 之前在非自选模式下拿"出厂默认那 5 色"兜底 → 看着像一张与当前主题无关的示例图
            // （用户报障"自选配色里面不得有示例"）。
            _draft.Clear();
            _draft.AddRange(PaletteSolver.EditableSlots(current).Select(ToMedia));
            RebuildSlots();
        }

        _selectedThemeId = current.Id;
        AppliedThemeName = current.Name;
        Raise(nameof(SelectedThemeId));
        Raise(nameof(AppliedThemeName));
        ProjectCardSelection();
        RaiseCustomState();
    }

    /// <summary>当前生效主题的显示名（色槽区的说明文案："下面这些是「X」主题的颜色"）。</summary>
    public string AppliedThemeName { get; private set; } = ThemeCatalog.Default.Name;

    // 字体候选**惰性**（见 EnsureFontsLoadedAsync）：构造期不枚举系统字体 ——
    // 枚举开销与机器上装的字体数量成正比，用户没打开字体下拉就不该付这笔钱。
    // 当前选中字体直接取 ThemeService 的状态，不依赖候选列表。
    private bool _fontsLoaded;

    /// <summary>
    /// 惰性装载字体候选（首次真正需要列表时调用一次）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么惰性</b>：候选列表要枚举**系统全部字体**，开销与机器上装的字体数量成正比
    /// （缓存只解决第二次之后；第一次无论如何都要付）。用户没打开字体下拉就不该付这笔钱。
    /// </para>
    /// <para>
    /// <b>为什么在后台线程</b>（<see cref="Fonts.FontCatalog.LoadAsync"/>）：把它付在 UI 线程上 =
    /// 用户每装一批字体就多卡一次，且卡顿随机器变差而放大。"大多数机器上很快"不构成免责 ——
    /// 慢就是慢，正确做法是异步 + 缓存，而不是让用户"别去触发它"。
    /// </para>
    /// <para>
    /// 失败**如实播报**（不静默回退成空列表）：枚举炸了要让用户看见，而不是给他一个空下拉。
    /// </para>
    /// </remarks>
    public async Task EnsureFontsLoadedAsync()
    {
        if (_fontsLoaded) return;
        _fontsLoaded = true;
        try
        {
            await ReloadFontsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 失败可重试：标志退回 false，用户再展开一次下拉就会重跑（而不是永久卡在空列表）
            _fontsLoaded = false;
            LpLog.Error("装载字体候选失败", ex, LogCategory);
            Status = $"字体列表装载失败：{ex.GetBaseException().Message}";
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主题卡（出厂默认排第一）。</summary>
    public ObservableCollection<ThemeCardViewModel> ThemeCards { get; }

    /// <summary>自选配色的色槽。</summary>
    public ObservableCollection<ColorSlotViewModel> Slots { get; } = new();

    /// <summary>系统 + 已导入的界面字体候选。</summary>
    public ObservableCollection<FontOptionViewModel> UiFonts { get; } = new();

    /// <summary>系统 + 已导入的等宽字体候选。</summary>
    public ObservableCollection<FontOptionViewModel> MonoFonts { get; } = new();

    /// <summary>当前选中的主题 id。</summary>
    public string SelectedThemeId
    {
        get => _selectedThemeId;
        private set
        {
            _selectedThemeId = value;
            Raise(nameof(SelectedThemeId));
            ProjectCardSelection();   // 唯一投影点：选中态只在这里刷新
        }
    }

    /// <summary>
    /// 把"当前已应用的主题"投影到卡片选中态（**唯一投影点**）。
    /// </summary>
    /// <remarks>
    /// 卡片的高亮完全由本方法按 <see cref="SelectedThemeId"/> 覆盖式写入——视图不持状态，
    /// 也不存在"点了哪张"的第二份记忆（架构不变量 12）。
    /// </remarks>
    private void ProjectCardSelection()
    {
        foreach (var card in ThemeCards)
            card.IsSelected = string.Equals(card.Id, _selectedThemeId, StringComparison.Ordinal);
    }

    /// <summary>选中的界面字体。</summary>
    public FontOptionViewModel? SelectedUiFont
    {
        get => _selectedUiFont;
        set { _selectedUiFont = value; Raise(nameof(SelectedUiFont)); }
    }

    /// <summary>选中的等宽字体。</summary>
    public FontOptionViewModel? SelectedMonoFont
    {
        get => _selectedMonoFont;
        set { _selectedMonoFont = value; Raise(nameof(SelectedMonoFont)); }
    }

    /// <summary>派生诊断文案（无诊断时为空）。</summary>
    public string Diagnostics
    {
        get => _diagnostics;
        private set { _diagnostics = value; Raise(nameof(Diagnostics)); }
    }

    /// <summary>是否有诊断（控制显示）。</summary>
    public bool HasDiagnostics
    {
        get => _hasDiagnostics;
        private set { _hasDiagnostics = value; Raise(nameof(HasDiagnostics)); }
    }

    /// <summary>结果播报（状态栏口径）。</summary>
    public string Status
    {
        get => _status;
        private set { _status = value; Raise(nameof(Status)); }
    }

    /// <summary>当前草稿槽数（用于「4 色 / 5 色」分段的显示）。</summary>
    public int SlotCount => _draft.Count;

    /// <summary>是否可以再减一个色槽。</summary>
    public bool CanRemoveSlot => _draft.Count > MinSlots;

    /// <summary>是否可以再加一个色槽。</summary>
    public bool CanAddSlot => _draft.Count < MaxSlots;

    // ── 互斥归属（主题 ↔ 自选配色 二选一）─────────────────────────────

    /// <summary>当前生效外观是否来自**自选配色**（false = 来自某个预设主题）。</summary>
    /// <remarks>
    /// 与主题卡的 <c>IsSelected</c> 构成"二选一"的完整投影：<c>IsCustomActive</c> 为 true 时
    /// 所有卡片都不高亮；为 false 时恰好有一张高亮。视图只绑这两个布尔，不自己判断"选了哪边"。
    /// </remarks>
    public bool IsCustomActive => _customActive;

    /// <summary>草稿有未应用的改动（显示「编辑中（未应用）」；已在用自选配色时不显示——它就是当前值）。</summary>
    public bool IsDraftEditing => _draftDirty && !_customActive;

    /// <summary>
    /// 「4 色 / 5 色」分段控件的选中索引（0 = 4 色，1 = 5 色）——滑动指示器组件的绑定入口。
    /// </summary>
    /// <remarks>
    /// 值始终由 <see cref="SlotCount"/> 推出（唯一事实来源是草稿本身），写入走 <see cref="SetSlotCount"/>；
    /// 视图侧不另存"选了几色"，段控件只反映草稿的真实槽数。
    /// </remarks>
    public int SlotCountIndex
    {
        get => _draft.Count - MinSlots;
        set => SetSlotCount(value + MinSlots);
    }

    /// <summary>把草稿槽数设为 4 或 5（越界一律钳到合法档——UI 只开放这两档）。</summary>
    public void SetSlotCount(int count)
    {
        var target = Math.Clamp(count, MinSlots, MaxSlots);
        if (target == _draft.Count) return;

        while (_draft.Count > target) RemoveSlotCore();
        while (_draft.Count < target) AddSlotCore();

        MarkDraftDirty();
        RebuildSlots();
        RefreshDraftDiagnostics();
    }

    /// <summary>互斥归属相关的属性一起通知（三处必须同步，漏一个就是"界面不跟着变"）。</summary>
    private void RaiseCustomState()
    {
        Raise(nameof(IsCustomActive));
        Raise(nameof(IsDraftEditing));
    }

    private void SetCustomActive(bool value)
    {
        if (_customActive == value) return;
        _customActive = value;
        RaiseCustomState();
    }

    private void MarkDraftDirty()
    {
        if (_draftDirty) return;
        _draftDirty = true;
        Raise(nameof(IsDraftEditing));
    }

    private void ClearDraftDirty()
    {
        if (!_draftDirty) return;
        _draftDirty = false;
        Raise(nameof(IsDraftEditing));
    }

    // ── 主题 ─────────────────────────────────────────────────────────

    /// <summary>应用一张预设/默认主题卡（单击即应用，含持久化）。</summary>
    public void ApplyThemeCard(ThemeCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        try
        {
            ThemeService.Apply(card.Definition);
            SetCustomActive(false);   // 互斥：用预设 = 自选区让出"当前使用"
            SelectedThemeId = card.Id;
            ThemeService.SaveCurrentPreferences();
            Status = $"已应用主题「{card.Name}」";
            Diagnostics = string.Empty;
            HasDiagnostics = false;
        }
        catch (Exception ex)
        {
            // 写偏好失败不静默（观测面纪律），且保持原主题（Apply 已成功但偏好没落盘 → 如实说清）
            LpLog.Error($"应用主题「{card.Name}」后保存偏好失败", ex, LogCategory);
            Status = $"主题已应用，但偏好保存失败：{ex.Message}";
        }
    }

    /// <summary>把当前槽位当作自选配色应用。</summary>
    public void ApplyDraft()
    {
        var definition = BuildDraftDefinition();
        var issues = ThemeValidator.Validate(definition);
        ShowDiagnostics(issues);
        if (ThemeValidator.HasErrors(issues))
        {
            Status = "自选配色不合法，未应用";
            return;
        }

        try
        {
            ThemeService.Apply(definition);
            SetCustomActive(true);    // 互斥：用自选配色 = 所有主题卡让出"当前使用"
            ClearDraftDirty();        // 草稿 = 当前值，不再有"未应用改动"
            SelectedThemeId = definition.Id;
            ThemeService.SaveCurrentPreferences();
            Status = $"已应用自选配色（{definition.Palette.Count} 色）";
        }
        catch (Exception ex)
        {
            LpLog.Error("应用自选配色后保存偏好失败", ex, LogCategory);
            Status = $"配色已应用，但偏好保存失败：{ex.Message}";
        }
    }

    /// <summary>实时校验当前草稿并把诊断写进面板（无副作用、不应用）。</summary>
    public void RefreshDraftDiagnostics()
    {
        var issues = ThemeValidator.Validate(BuildDraftDefinition());
        ShowDiagnostics(issues);
    }

    private ThemeDefinition BuildDraftDefinition() => new()
    {
        Id = "user-custom",
        Name = "自选配色",
        Source = ThemeSource.UserDefined,
        Palette = _draft.Select(ToArgb).ToArray(),
        NeutralHueOverride = null,   // 自选配色按自己的中性池派生（不钉值）
    };

    private void ShowDiagnostics(IReadOnlyList<ThemeIssue> issues)
    {
        if (issues.Count == 0)
        {
            Diagnostics = string.Empty;
            HasDiagnostics = false;
            return;
        }
        // 错误在前（必须拒绝的），提示在后
        Diagnostics = string.Join("\n", issues
            .OrderByDescending(i => i.Severity)
            .Select(i => (i.Severity == ThemeIssueSeverity.Error ? "✗ " : "· ") + i.Message));
        HasDiagnostics = true;
    }

    // ── 色槽 ─────────────────────────────────────────────────────────

    /// <summary>改一个色槽的颜色（只改草稿，不应用）。</summary>
    public void SetSlotColor(int index, Color color)
    {
        if (index < 0 || index >= _draft.Count) return;
        _draft[index] = color;
        MarkDraftDirty();
        RebuildSlots();
        RefreshDraftDiagnostics();
    }

    /// <summary>加一个色槽（4 → 5）。</summary>
    public void AddSlot()
    {
        if (!CanAddSlot) return;
        AddSlotCore();
        MarkDraftDirty();
        RebuildSlots();
        RefreshDraftDiagnostics();
    }

    /// <summary>减一个色槽（5 → 4）。</summary>
    public void RemoveSlot()
    {
        if (!CanRemoveSlot) return;
        RemoveSlotCore();
        MarkDraftDirty();
        RebuildSlots();
        RefreshDraftDiagnostics();
    }

    /// <summary>纯槽位操作（不重建视图、不标脏）：<see cref="SetSlotCount"/> 反复调用它凑到目标档。</summary>
    private void AddSlotCore()
    {
        // 新槽取当前草稿的"派生建议色"：色相 +40°、略提明度（与已有色同族但可辨），不做空白槽
        var seed = _draft.Count > 0 ? _draft[^1] : Colors.Gray;
        var seedArgb = ToArgb(seed);
        ColorMath.ToHsv(seedArgb, out var h, out var s, out var v);
        _draft.Add(ToMedia(ColorMath.FromHsv(h + 40, Math.Min(1, s * 0.9), Math.Min(1, v + 0.12))));
    }

    private void RemoveSlotCore() => _draft.RemoveAt(_draft.Count - 1);

    private void RebuildSlots()
    {
        Slots.Clear();
        for (var i = 0; i < _draft.Count; i++)
            Slots.Add(new ColorSlotViewModel(i, _draft[i]));
        Raise(nameof(SlotCount));
        Raise(nameof(SlotCountIndex));
        Raise(nameof(CanAddSlot));
        Raise(nameof(CanRemoveSlot));
    }

    // ── 字体 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 重新装载字体候选（导入 / 删除 / 恢复默认后调用）—— **后台枚举 + 投影**。
    /// </summary>
    /// <remarks>
    /// <b>唯一装载路径</b>：候选集合只在 <see cref="ReloadFontsAsync"/> 里被重写，
    /// 导入/删除/恢复默认都汇到它 —— 不给自己留"顺手再拼一次列表"的第二条路
    /// （第二份实现必然与第一份漂移，这是本仓踩过的老坑）。
    /// </remarks>
    public async Task ReloadFontsAsync()
    {
        var currentUi = ThemeService.CurrentUiFont;
        var currentMono = ThemeService.CurrentMonoFont;

        var all = await FontCatalog.LoadAsync().ConfigureAwait(true);

        UiFonts.Clear();
        MonoFonts.Clear();
        foreach (var f in all)
        {
            UiFonts.Add(new FontOptionViewModel(f));
            MonoFonts.Add(new FontOptionViewModel(f));
        }

        SelectedUiFont = UiFonts.FirstOrDefault(f => string.Equals(f.Family, currentUi, StringComparison.OrdinalIgnoreCase))
                         ?? UiFonts.FirstOrDefault(f => string.Equals(f.Family, FontCatalog.DefaultUiFamily, StringComparison.OrdinalIgnoreCase));
        SelectedMonoFont = MonoFonts.FirstOrDefault(f => string.Equals(f.Family, currentMono, StringComparison.OrdinalIgnoreCase))
                           ?? MonoFonts.FirstOrDefault(f => string.Equals(f.Family, FontCatalog.DefaultMonoFamily, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>同步重载（仅测试与"已经不持有 UI 上下文"的收尾路径用；界面一律走异步版）。</summary>
    /// <remarks>
    /// 存在的理由是**可测性**：单元测试要断言投影结果，而 <c>async void</c> 式的入口无法 await。
    /// 它不构成第二套实现 —— 装载本身仍然只有 <see cref="FontCatalog.LoadAsync"/> 一条路。
    /// </remarks>
    public void ReloadFonts() => ReloadFontsAsync().GetAwaiter().GetResult();

    /// <summary>
    /// 导入一个字体文件（失败明确报错给用户 + 留痕 + **不写偏好**）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 失败**必须让用户在界面上看见**：这里把 <see cref="Status"/> 写成"导入失败：…"，
    /// 面板把它显示在状态行上（N1 要求"导入失败的用户可见反馈"）。
    /// 只写日志不播报 = 用户点了"导入字体…"什么都没发生 —— 那是本仓禁止的静默失败。
    /// </remarks>
    public async Task<bool> ImportFontAsync(string path)
    {
        try
        {
            var choice = FontCatalog.Import(path);
            await ReloadFontsAsync().ConfigureAwait(true);
            SelectedUiFont = UiFonts.FirstOrDefault(f => string.Equals(f.Family, choice.Family, StringComparison.OrdinalIgnoreCase))
                             ?? SelectedUiFont;
            Status = $"已导入字体「{choice.Family}」（点「应用字体」生效）";
            return true;
        }
        catch (Exception ex)
        {
            LpLog.Error($"导入字体失败：{path}", ex, LogCategory);
            Status = $"导入失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>删除一个已导入字体（系统字体不可删）。</summary>
    public async Task DeleteFontAsync(FontOptionViewModel option)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (!option.CanDelete)
        {
            Status = "系统字体不可删除";
            return;
        }
        try
        {
            FontCatalog.Remove(option.Choice);
            await ReloadFontsAsync().ConfigureAwait(true);
            Status = $"已删除导入字体「{option.Family}」";
        }
        catch (Exception ex)
        {
            LpLog.Error($"删除导入字体失败：{option.Family}", ex, LogCategory);
            Status = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>应用当前选中的界面/等宽字体（含持久化）。</summary>
    public void ApplyFonts()
    {
        var ui = SelectedUiFont?.Family ?? FontCatalog.DefaultUiFamily;
        var mono = SelectedMonoFont?.Family ?? FontCatalog.DefaultMonoFamily;

        try
        {
            ThemeService.ApplyFonts(ui, mono);
            ThemeService.SaveCurrentPreferences();
            Status = $"已应用字体：界面「{ui}」/ 等宽「{mono}」";
        }
        catch (Exception ex)
        {
            LpLog.Error("应用字体后保存偏好失败", ex, LogCategory);
            Status = $"字体已应用，但偏好保存失败：{ex.Message}";
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
            ClearDraftDirty();                // 默认外观 = 全新起点，没有"未应用改动"
            SyncFromAppliedTheme();           // 互斥归属 + 主题卡 + 草稿一起回默认（唯一投影点）
            if (_fontsLoaded) await ReloadFontsAsync().ConfigureAwait(true);
            Diagnostics = string.Empty;
            HasDiagnostics = false;
            Status = "已恢复默认外观";
        }
        catch (Exception ex)
        {
            LpLog.Error("恢复默认外观失败", ex, LogCategory);
            Status = $"恢复默认失败：{ex.Message}";
        }
    }

    /// <summary>字体度量自检（提示用；不阻止应用）。</summary>
    public string InspectFont(FontOptionViewModel option)
    {
        var verdict = FontMetricsProbe.Inspect(FontCatalog.BuildTokenValue(option.Family));
        return verdict.Ok ? string.Empty : verdict.Message;
    }

    // ── 小工具 ───────────────────────────────────────────────────────

    /// <summary>Argb → WPF Color（唯一转换点在 Theming）。</summary>
    private static Color ToMedia(Argb c) => ColorMath.ToMedia(c);

    private static Argb ToArgb(Color c) => ColorMath.FromMedia(c);

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
