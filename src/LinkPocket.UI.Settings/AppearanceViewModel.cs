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
        // ⚠️ 预设的身份色只有 1–2 个（标定收敛的结果，见 ThemeCatalog.Presets 注释）→ 直接展示会
        // 稀疏得像"缺了几个色"。故不足 4 个时**用该主题自身的调色板补足**（同族明度档，
        // 色相/彩度仍是这套主题自己的）——补的是"主题色本身"，不是编造的装饰色。
        Swatches = new ObservableCollection<Color>(BuildSwatches(definition, table));

        // 派生示意条：强调填充 / 页面底 / 正文 —— 一眼看出"这套主题长什么样"
        Accent = ToMedia(table.Token(AppTokens.AccentFill));
        Base = ToMedia(table.Token(AppTokens.SurfaceBase));
        Text = ToMedia(table.Token(AppTokens.TextPrimary));

        var f = table.Families;
        Summary = $"主色 H{f.AccentHue:F0} · 支撑 H{f.SupportHue:F0}";
    }

    /// <summary>
    /// 主题卡的身份色圆点：原身份色优先，不足 4 个时用该主题的**色调板档位**补足。
    /// </summary>
    /// <remarks>
    /// 补足只沿"该主题强调族"的明度轴取档（同色相/同彩度）——用户看到的仍是"这套主题的颜色"，
    /// 而已有身份色永远排在前面（原色优先，派生只补位）。
    /// </remarks>
    private IEnumerable<Color> BuildSwatches(ThemeDefinition definition, TokenTable table)
    {
        const int TargetCount = 4;

        var list = definition.Palette.Select(ToMedia).ToList();
        if (list.Count >= TargetCount) return list;

        var f = table.Families;
        // 与方案 §4.5 的档位表同源（容器 90 / 填充 40 / 强调文字 30 / 深 15），取未重复的档
        foreach (var tone in new[] { 90.0, 40.0, 30.0, 15.0, 70.0 })
        {
            if (list.Count >= TargetCount) break;
            var argb = ColorMath.FromAlphaHct(0xFF, f.AccentHue, f.AccentChroma, tone);
            var candidate = ToMedia(argb);
            if (list.All(c => c != candidate)) list.Add(candidate);
        }
        return list;
    }

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

    public AppearanceViewModel()
    {
        ThemeCards = new ObservableCollection<ThemeCardViewModel>(
            ThemeCatalog.All.Select(t => new ThemeCardViewModel(t)));

        // 默认草稿 = 出厂默认的身份色（用户从"当前这套"开始改，而不是从空白开始）
        _draft.AddRange(ThemeCatalog.Default.Palette.Select(ToMedia));
        RebuildSlots();

        // 入口对齐：面板显示"当前**已应用**的主题"，而不是永远显示出厂默认
        // （用户上次选的预设要在他回到这一页时仍然高亮——否则选中态就是错的）
        _selectedThemeId = ThemeService.Current.Id;
        ProjectCardSelection();

        // 字体候选**惰性**（见 EnsureFontsLoadedAsync）：构造期不枚举系统字体 ——
        // 枚举开销与机器上装的字体数量成正比，用户没打开字体下拉就不该付这笔钱。
        // 当前选中字体直接取 ThemeService 的状态，不依赖候选列表。
    }

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

    // ── 主题 ─────────────────────────────────────────────────────────

    /// <summary>应用一张预设/默认主题卡（单击即应用，含持久化）。</summary>
    public void ApplyThemeCard(ThemeCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        try
        {
            ThemeService.Apply(card.Definition);
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
        RebuildSlots();
        RefreshDraftDiagnostics();
    }

    /// <summary>加一个色槽（4 → 5）。</summary>
    public void AddSlot()
    {
        if (!CanAddSlot) return;
        // 新槽取当前草稿的"派生建议色"：色相 +40°、略提明度（与已有色同族但可辨），不做空白槽
        var seed = _draft.Count > 0 ? _draft[^1] : Colors.Gray;
        var seedArgb = ToArgb(seed);
        ColorMath.ToHsv(seedArgb, out var h, out var s, out var v);
        _draft.Add(ToMedia(ColorMath.FromHsv(h + 40, Math.Min(1, s * 0.9), Math.Min(1, v + 0.12))));
        RebuildSlots();
        RefreshDraftDiagnostics();
    }

    /// <summary>减一个色槽（5 → 4）。</summary>
    public void RemoveSlot()
    {
        if (!CanRemoveSlot) return;
        _draft.RemoveAt(_draft.Count - 1);
        RebuildSlots();
        RefreshDraftDiagnostics();
    }

    private void RebuildSlots()
    {
        Slots.Clear();
        for (var i = 0; i < _draft.Count; i++)
            Slots.Add(new ColorSlotViewModel(i, _draft[i]));
        Raise(nameof(SlotCount));
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
            SelectedThemeId = ThemeCatalog.DefaultId;
            _draft.Clear();
            _draft.AddRange(ThemeCatalog.Default.Palette.Select(ToMedia));
            RebuildSlots();
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
