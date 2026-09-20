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

/// <summary>一张主题卡（「外观」面板的主题网格项：11 张预设卡 + 第 12 张「自选颜色」卡）。</summary>
/// <remarks>
/// <para>
/// <b>第 12 张不是主题目录里的主题</b>（用户令 2026-09-20）：<see cref="ThemeCatalog.All"/> 仍然是
/// 11 套预设，第 12 张是 <see cref="CreateCustomCard"/> 追加的**自选颜色卡**（<c>Id = "user-custom"</c>）——
/// 它没有目录定义（<see cref="Definition"/> 为 <c>null</c>），点它 = 应用调色台里的自选配色。
/// </para>
/// <para>
/// <b>预设只读</b>：预设卡的色点是**展示**用的身份色拷贝；调色台里的编辑永远只动草稿，
/// 绝不写回 <see cref="Definition"/>.Palette（用户令："默认的颜色是绝对不能改的"）。
/// </para>
/// </remarks>
public sealed class ThemeCardViewModel : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>「自选颜色」卡的 id（与 <c>BuildDraftDefinition</c> 的自选配色 id 同一个）。</summary>
    public const string CustomCardId = "user-custom";

    /// <summary>「自选颜色」卡的显示名。</summary>
    public const string CustomCardName = "自选颜色";

    private bool _isSelected;
    private bool _isEmpty;

    public ThemeCardViewModel(ThemeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        _name = definition.Name;
        _id = definition.Id;
        IsCustom = false;

        var table = PaletteSolver.Solve(definition);

        // 色点用**身份色**（"主题就是这些颜色"——方案 §4.2：主题卡展示用户原色，界面用其档位）。
        // 补位规则（预设只有 1–3 个身份色）= Theming 的唯一实现 `PaletteSolver.EditableSlots`，
        // 与外观面板的色槽同一份（否则"卡片 4 个点、色槽 3 格"迟早漂移）。
        Swatches = new ObservableCollection<Color>(BuildSwatches(definition));
        _isEmpty = Swatches.Count == 0;

        // 派生示意条：强调填充 / 页面底 / 正文 —— 一眼看出"这套主题长什么样"
        Accent = ToMedia(table.Token(AppTokens.AccentFill));
        Base = ToMedia(table.Token(AppTokens.SurfaceBase));
        Text = ToMedia(table.Token(AppTokens.TextPrimary));

        var f = table.Families;
        Summary = $"主色 H{f.AccentHue:F0} · 支撑 H{f.SupportHue:F0}";
    }

    /// <summary>
    /// 第 12 张卡「自选颜色」：**不是** <see cref="ThemeCatalog"/> 里的主题，没有目录定义。
    /// </summary>
    /// <remarks>
    /// <b>默认全空</b>（用户令 2026-09-20："上面最后面的位置有一个自选颜色，并且里面全都是空的"）：
    /// 没有任何自选配色时 <see cref="Swatches"/> 为空、<see cref="IsEmpty"/> 为真，
    /// 卡面因此画 4 个**空心占位圆**（不显示任何颜色）。
    /// </remarks>
    private ThemeCardViewModel()
    {
        Definition = null;
        IsCustom = true;
        _name = CustomCardName;
        _id = CustomCardId;
        Swatches = new ObservableCollection<Color>();
        _isEmpty = true;
        Accent = default;
        Base = default;
        Text = default;
        Summary = string.Empty;
    }

    /// <summary>建第 12 张「自选颜色」卡（唯一入口；它在主题网格里排最后一张）。</summary>
    public static ThemeCardViewModel CreateCustomCard() => new();

    /// <summary>
    /// 把「自选颜色」卡的色点刷成草稿里**已选**的那些颜色（空 = 卡面走占位态）。
    /// </summary>
    /// <remarks>
    /// 数量 = 用户当前选的槽数（4 或 5）；正在编辑到一半时按实际已选数量显示 ——
    /// 卡面是草稿的投影，不假装"已经是一套完整配色"。
    /// </remarks>
    public void SetSwatches(IEnumerable<Color> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        Swatches.Clear();
        foreach (var c in colors) Swatches.Add(c);

        var empty = Swatches.Count == 0;
        if (_isEmpty == empty) return;
        _isEmpty = empty;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsEmpty)));
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

    /// <summary>目录里的主题定义；**「自选颜色」卡为 <c>null</c>**（它不是目录里的主题）。</summary>
    public ThemeDefinition? Definition { get; }

    private readonly string _name;

    private readonly string _id;

    public string Name => _name;

    public string Id => _id;

    /// <summary>是否是第 12 张「自选颜色」卡（点它 = 应用调色台里的自选配色）。</summary>
    public bool IsCustom { get; }

    /// <summary>卡面是否是**空态**（自选颜色卡还没有任何颜色 → 画 4 个空心占位圆）。</summary>
    public bool IsEmpty => _isEmpty;

    /// <summary>是否显示"派生示意条"（只对预设卡：自选颜色卡的颜色由调色台决定，不在这里假装派生）。</summary>
    public bool ShowDerivedStrip => !IsCustom;

    /// <summary>是否出厂默认（带「默认」徽标，且永远排第一）。</summary>
    public bool IsDefault => Definition?.Source == ThemeSource.FactoryDefault;

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

/// <summary>一个色槽（4/5 色自选配色）。**可以为空**（还没选颜色）。</summary>
/// <remarks>
/// 空槽不是"黑色"也不是"透明"这类会骗人的值：<see cref="Color"/> 为 <c>null</c>、
/// <see cref="Hex"/> 为空串，界面据此画虚线空心环 + 「+」（用户令 2026-09-20：
/// "上面最后面的位置有一个自选颜色，并且里面全都是空的"）。
/// </remarks>
public sealed class ColorSlotViewModel
{
    public ColorSlotViewModel(int index, Color? color)
    {
        Index = index;
        Color = color;
    }

    public int Index { get; }

    /// <summary>槽里的颜色；<c>null</c> = **空槽**（还没选）。</summary>
    public Color? Color { get; }

    /// <summary>是否为空槽（界面画虚线空心环 + 「+」）。</summary>
    public bool IsEmpty => Color is null;

    /// <summary>是否有颜色（应用门槛按它数"还差几个"）。</summary>
    public bool HasColor => Color is not null;

    /// <summary>槽位序号文案（1 起）。</summary>
    public string Label => $"颜色 {Index + 1}";

    /// <summary>HEX 文案（空槽 = 空串：**绝不**拿 <c>#000000</c> 这类假值冒充"已选"）。</summary>
    public string Hex => Color is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : string.Empty;

    /// <summary>槽位数值文案（空槽 = 「未选」）。</summary>
    public string ValueText => IsEmpty ? "未选" : Hex;
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

    /// <summary>
    /// 调色台草稿：<c>null</c> = **空槽**（用户还没选这个颜色）。
    /// </summary>
    /// <remarks>
    /// 默认 = <see cref="MinSlots"/> 个**空槽**（用户令 2026-09-20："自选颜色里面全都是空的"）——
    /// 旧行为是开局倒进"当前主题的颜色"，于是预设主题看起来可被直接改（其实只是复制），
    /// 而且和"预设只读、自选是另一份"这件事完全对不上。
    /// </remarks>
    private readonly List<Color?> _draft = new() { null, null, null, null };

    private readonly ThemeCardViewModel _customCard;

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

        // 第 12 张 = 「自选颜色」卡（**不是** ThemeCatalog 里的主题：目录仍是 11 套）。
        // 用户令 2026-09-20："上面最后面的位置有一个自选颜色" —— 它排在主题网格最后一张。
        _customCard = ThemeCardViewModel.CreateCustomCard();
        ThemeCards.Add(_customCard);

        // 默认草稿 = 4 个空槽（先把 Slots 建出来，界面才不会是"有 4 格数据却没有槽"）
        RebuildSlots();

        // 入口对齐：面板显示"当前**已应用**的外观"——主题卡高亮 + 互斥归属 + 色槽草稿
        // （用户上次选的主题/配色要在他回到这一页时仍然是对的，否则选中态就是错的）
        SyncFromAppliedTheme();
    }

    /// <summary>
    /// **唯一投影点**：把"当前已应用的外观"投影到面板（互斥归属 + 主题卡选中态 + 色槽草稿）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>种子规则（用户令 2026-09-20：预设/默认主题只读）</b>：
    /// </para>
    /// <list type="number">
    /// <item>当前生效 = **自选配色** → 色槽 = 该配色本身（重进面板要看到他正在用的那份）；</item>
    /// <item>当前生效 = **预设主题** → 色槽**保持用户自己的草稿不动**（内存里那份；从来没有过 = 空）。
    /// 旧行为是把当前主题的颜色倒进色槽 —— 用户报障的根因："默认的颜色是绝对不能改的"，
    /// 而"倒进来"看起来就像预设可以被就地改；想从某套主题改起请走 <see cref="StartFromCurrentTheme"/>（复制）；</item>
    /// <item>有**未应用改动**（<c>_draftDirty</c>）时一律不重播种（入口刷新不许冲掉用户正在编辑的东西）。</item>
    /// </list>
    /// </remarks>
    public void SyncFromAppliedTheme()
    {
        var current = ThemeService.Current;
        SetCustomActive(current.Source == ThemeSource.UserDefined);

        if (!_draftDirty && _customActive)
        {
            _draft.Clear();
            _draft.AddRange(PaletteSolver.EditableSlots(current).Select(c => (Color?)ToMedia(c)));
            RebuildSlots();
        }

        _selectedThemeId = current.Id;
        AppliedThemeName = current.Name;
        Raise(nameof(SelectedThemeId));
        Raise(nameof(AppliedThemeName));
        Raise(nameof(StartFromCurrentThemeLabel));
        ProjectCardSelection();
        RaiseCustomState();
    }

    /// <summary>当前生效主题的显示名（调色台的说明文案与「起点」按钮文案都用它）。</summary>
    public string AppliedThemeName { get; private set; } = ThemeCatalog.Default.Name;

    /// <summary>
    /// 「以当前主题为起点」按钮的文案（带上主题名，用户一眼知道复制的是哪一套）。
    /// </summary>
    public string StartFromCurrentThemeLabel => $"以「{AppliedThemeName}」为起点";

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

    /// <summary>主题卡（出厂默认排第一；**最后一张 = 自选颜色卡**）。</summary>
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
    /// <para>
    /// 卡片的高亮完全由本方法按 <see cref="SelectedThemeId"/> 覆盖式写入——视图不持状态，
    /// 也不存在"点了哪张"的第二份记忆（架构不变量 12）。
    /// </para>
    /// <para>
    /// <b>严格二选一（用户报障"两块都亮着、分不清谁在生效"）</b>：自选配色生效时**只有**
    /// 第 12 张「自选颜色」卡高亮、11 张预设卡全部不高亮；预设生效时只有那一张高亮、
    /// 自选颜色卡不高亮。两个方向都由 <see cref="IsCustomActive"/> 唯一定夺，
    /// 不靠 <c>SelectedThemeId</c> 恰好等于某个 id（那是第二份事实源）。
    /// </para>
    /// </remarks>
    private void ProjectCardSelection()
    {
        foreach (var card in ThemeCards)
            card.IsSelected = card.IsCustom
                ? _customActive
                : !_customActive && string.Equals(card.Id, _selectedThemeId, StringComparison.Ordinal);
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
    /// 与主题卡的 <c>IsSelected</c> 构成"二选一"的完整投影：自选配色生效时**只有第 12 张
    /// 「自选颜色」卡**高亮（11 张预设卡全灭）；预设生效时恰好有一张预设卡高亮、自选颜色卡不亮。
    /// 视图只绑这两个布尔，不自己判断"选了哪边"。
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
        // 归属变了 → 卡片高亮跟着重投影（自选颜色卡的高亮由 IsCustomActive 唯一决定）
        ProjectCardSelection();
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

    /// <summary>
    /// 点一张主题卡：预设卡 = 单击即应用（含持久化）；第 12 张「自选颜色」卡 = 应用调色台里的配色。
    /// </summary>
    /// <remarks>
    /// 「自选颜色」卡**没有目录定义**（<see cref="ThemeCardViewModel.Definition"/> 为 <c>null</c>），
    /// 它的应用走同一个入口 <see cref="ApplyDraft"/>（合法性门槛、播报、"应用后归属切到自选"都在那里）——
    /// 不给它另起一套应用逻辑（第二套必然与第一套漂移）。
    /// </remarks>
    public void ApplyThemeCard(ThemeCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);

        var definition = card.Definition;
        if (definition is null)
        {
            ApplyDraft();
            return;
        }

        try
        {
            ThemeService.Apply(definition);
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

    /// <summary>
    /// 「以当前主题为起点」：把**当前生效主题**的可编辑色槽**复制**进调色台（预设定义绝不被改写）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用户令 2026-09-20："默认的颜色是绝对不能改的，但是我们可以多出一个按钮，
    /// 我们可以把默认的某个主题作为我们自选色的方案"。
    /// </para>
    /// <para>
    /// <b>复制而不是引用</b>：色槽拿到的是 <see cref="PaletteSolver.EditableSlots"/> 的**值拷贝**
    /// （<c>Argb</c> 是不可变值类型），随后所有编辑都只落在草稿里；预设的
    /// <see cref="ThemeDefinition.Palette"/> 从此与面板无关 —— 这是"默认主题不可改"的结构性保证，
    /// 不靠"记得别写回去"。
    /// </para>
    /// <para>
    /// 复制后标记为**编辑中（未应用）**：还没生效，用户改完点「应用这套外观」才算数。
    /// </para>
    /// </remarks>
    public void StartFromCurrentTheme()
    {
        var current = ThemeService.Current;
        var slots = PaletteSolver.EditableSlots(current);

        _draft.Clear();
        _draft.AddRange(slots.Select(c => (Color?)ToMedia(c)));
        MarkDraftDirty();
        RebuildSlots();
        RefreshDraftDiagnostics();
        Status = $"已把「{current.Name}」的 {slots.Count} 个颜色复制到调色台（点「应用这套外观」才生效）";
    }

    /// <summary>把当前槽位当作自选配色应用。</summary>
    /// <remarks>
    /// <b>应用门槛（用户令 2026-09-20）</b>：槽里有空位（不足 4 个颜色）→ **拒绝应用**，
    /// 并在状态行说清"还差几个"；主题校验（4/5 色、可读性提示）照旧走
    /// <see cref="ThemeValidator"/>。两道门槛都不弹窗 —— 理由写在状态行上。
    /// </remarks>
    public void ApplyDraft()
    {
        var missing = _draft.Count(c => c is null);
        if (missing > 0)
        {
            Status = $"还有 {missing} 个颜色没选（点色槽用取色盘选）";
            // 与 RefreshDraftDiagnostics 同一口径：空槽是"还没选完"，**不是**"配色不合法"，
            // 所以这里不把 palette-size 那条红色错误摆出来。
            Diagnostics = $"· 还差 {missing} 个颜色：点色槽用取色盘选色（选够 4 个就能点顶部的「应用这套外观」）";
            HasDiagnostics = true;
            return;
        }

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
            SetCustomActive(true);    // 互斥：用自选配色 = 预设卡让出"当前使用"（自选颜色卡亮起）
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
        // ⚠️ 还没填满的草稿**不是**"配色不合法"：空槽数不足 4 时，`palette-size` 会以
        // "✗ 主题需要 4 或 5 个颜色（当前 0 个）" 的红色错误样子出现在刚进面板的空态上 ——
        // 那既不是用户的错、也不是错误（只是"还没选完"）。这里把它换成一句中性提示；
        // 真正的不合法（HEX 非法等）照旧显示。
        var empty = _draft.Count(c => c is null);
        if (empty > 0)
        {
            Diagnostics = $"· 还差 {empty} 个颜色：点色槽用取色盘选色（选够 4 个就能点顶部的「应用这套外观」）";
            HasDiagnostics = true;
            return;
        }

        var issues = ThemeValidator.Validate(BuildDraftDefinition());
        ShowDiagnostics(issues);
    }

    /// <summary>
    /// 草稿 → 主题定义（自选配色）。空槽**不冒充颜色**：直接不参与（于是"色数不是 4/5"会被校验抓出来）。
    /// </summary>
    private ThemeDefinition BuildDraftDefinition() => new()
    {
        Id = ThemeCardViewModel.CustomCardId,
        Name = "自选配色",
        Source = ThemeSource.UserDefined,
        Palette = _draft.Where(c => c is not null).Select(c => ToArgb(c!.Value)).ToArray(),
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

    /// <summary>
    /// 把草稿清成空（4 个空槽）——「恢复默认外观」用。
    /// </summary>
    /// <remarks>
    /// 既然回到出厂默认，调色台里留着上一份配色就是"名不副实"：用户看到 4 个色点会以为
    /// 自选配色还在生效（用户令：自选颜色默认是**全空**的）。
    /// </remarks>
    public void ClearDraft()
    {
        _draft.Clear();
        _draft.AddRange(Enumerable.Repeat<Color?>(null, MinSlots));
        ClearDraftDirty();
        RebuildSlots();
        Diagnostics = string.Empty;
        HasDiagnostics = false;
    }

    /// <summary>空槽打开取色盘时的初始颜色 = 当前主题的强调填充（令牌派生，不发明色值）。</summary>
    /// <remarks>
    /// 取色盘需要一个初值（HSV 的三个分量总要有一个起点）；用当前主题的强调色既不是"黑色"这类
    /// 会骗人的假值，也不是写死的字面量 —— 它就是用户此刻看到的界面主色。
    /// </remarks>
    public Color PickerSeedColor => ToMedia(ThemeService.Table.Token(AppTokens.AccentFill));

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
        // 新槽 = **空槽**（用户令 2026-09-20："里面全都是空的"）。
        // ⚠️ 旧行为是拿上一个色"派生"一个建议色（色相 +40°）—— 那是**编造的颜色**：
        //    用户看到的是一个他从未选过的色点，且它还会被算进"应用门槛"。空槽 + 「+」才是诚实的。
        _draft.Add(null);
    }

    private void RemoveSlotCore() => _draft.RemoveAt(_draft.Count - 1);

    private void RebuildSlots()
    {
        Slots.Clear();
        for (var i = 0; i < _draft.Count; i++)
            Slots.Add(new ColorSlotViewModel(i, _draft[i]));

        // 第 12 张「自选颜色」卡的色点 = 草稿里已选的颜色（没有 = 卡面走空态占位）
        _customCard.SetSwatches(_draft.Where(c => c is not null).Select(c => c!.Value));

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
            ClearDraft();                     // 调色台回**空态** + 清"未应用改动"（自选配色已随偏好一起清掉，
                                              // 留着色点会名不副实；默认外观 = 全新起点）
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
