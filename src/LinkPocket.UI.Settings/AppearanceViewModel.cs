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

/// <summary>一张主题卡（「外观」面板的主题网格项：11 张预设卡 + 第 12 张「自选颜色」卡）。</summary>
/// <remarks>
/// <para>
/// <b>第 12 张不是主题目录里的主题</b>：<see cref="ThemeCatalog.All"/> 仍然是
/// 11 套预设，第 12 张是 <see cref="CreateCustomCard"/> 追加的**自选颜色卡**（<c>Id = "user-custom"</c>）——
/// 它没有目录定义（<see cref="Definition"/> 为 <c>null</c>），点它 = 应用调色台里的自选配色。
/// </para>
/// <para>
/// <b>预设只读</b>：预设卡的色点是**展示**用的身份色拷贝；调色台里的编辑永远只动草稿，
/// 绝不写回 <see cref="Definition"/>.Palette（预设是只读的）。
/// </para>
/// </remarks>
public sealed class ThemeCardViewModel : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>「自选颜色」卡的 id（与 <c>BuildDraftDefinition</c> 的自选配色 id 同一个）。</summary>
    public const string CustomCardId = "user-custom";


    private bool _isSelected;
    private bool _isEmpty;
    private Color _cardBackground;

    public ThemeCardViewModel(ThemeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        _name = ThemeNames.Of(definition.Id);
        _id = definition.Id;
        IsCustom = false;

        var table = PaletteSolver.Solve(definition);

        // 色点用**身份色**（方案 §4.2：主题卡展示原始身份色，界面用其档位）。
        // 补位规则（预设只有 1–3 个身份色）= Theming 的唯一实现 `PaletteSolver.EditableSlots`，
        // 与外观面板的色槽同一份（否则"卡片 4 个点、色槽 3 格"迟早漂移）。
        Swatches = new ObservableCollection<Color>(BuildSwatches(definition));
        _isEmpty = Swatches.Count == 0;

        // 派生示意条：强调填充 / 页面底 / 正文 —— 一眼看出"这套主题长什么样"
        Accent = ToMedia(table.Token(AppTokens.AccentFill));
        Base = ToMedia(table.Token(AppTokens.SurfaceBase));
        Text = ToMedia(table.Token(AppTokens.TextPrimary));

        // 卡面底色 = **当前生效主题的页面底**（见 RefreshSwatches 的注释：只有"已应用"的那张卡
        // 才会与它自己的某个圆融合）。
        _cardBackground = ToMedia(ThemeService.Table.Token(AppTokens.SurfaceBase));

        var f = table.Families;
        Summary = Loc.K("appearance.palette.dualHue",
            f.AccentHue.ToString("F0", CultureInfo.InvariantCulture),
            f.SupportHue.ToString("F0", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 第 12 张卡「自选颜色」：**不是** <see cref="ThemeCatalog"/> 里的主题，没有目录定义。
    /// </summary>
    /// <remarks>
    /// <b>默认全空</b>：没有任何自选配色时 <see cref="Swatches"/> 为空、<see cref="IsEmpty"/> 为真，
    /// 卡面因此画 4 个**空心占位圆**（不显示任何颜色）。
    /// </remarks>
    private ThemeCardViewModel()
    {
        Definition = null;
        IsCustom = true;
        _name = ThemeNames.Of(CustomCardId);
        _id = CustomCardId;
        Swatches = new ObservableCollection<Color>();
        _isEmpty = true;
        Accent = default;
        Base = default;
        Text = default;
        Summary = LocValue.Empty;
    }

    /// <summary>建第 12 张「自选颜色」卡（唯一入口；它在主题网格里排最后一张）。</summary>
    public static ThemeCardViewModel CreateCustomCard() => new();

    /// <summary>
    /// 把「自选颜色」卡的色点刷成草稿里**已选**的那些颜色（空 = 卡面走占位态）。
    /// </summary>
    /// <remarks>
    /// 数量 = 当前草稿已选的槽数（4 或 5）；编辑到一半时按实际已选数量显示 ——
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

    /// <summary>按**当前配色应用方式**重建色点与卡面底色（切换「自动调整颜色」开关 / 换主题 / 进面板时调用）。</summary>
    /// <remarks>
    /// <b>色点与卡面底色必须一起重投影</b>：卡面底色取自**当前生效主题**的页面底，
    /// 而它与"当前模式下"的取值绑定 —— 只重投影其中一个，融合就会断。
    /// </remarks>
    public void RefreshSwatches()
    {
        if (Definition is { } def)
            SetSwatches(BuildSwatches(def));   // 「自选颜色」卡的色点由调色台草稿决定，不在此列
        SetCardBackground(ToMedia(ThemeService.Table.Token(AppTokens.SurfaceBase)));
    }

    /// <summary>
    /// 卡面底色 = **当前生效主题的页面底**（所有卡统一用它；见下"融合"的解释）。
    /// </summary>
    /// <remarks>
    /// 所有卡都用**当前生效主题**的页面底当卡面；而"已应用"的那张卡的页面底 == 当前页面底，
    /// 于是它色点里那枚"背景色成员"（<see cref="BuildSwatches"/> 用同一个 `SurfaceBaseColor` 替换最浅成员）
    /// 正好与卡面**逐字节同色** → 点它的瞬间能看到那个圆"化进"背景里 = 融合。
    /// 未选中的主题则保留自己的所有色点（在**当前**底色上都看得见），不会一开始就全部套用自己的背景。
    /// </remarks>
    public Color CardBackground => _cardBackground;

    private void SetCardBackground(Color color)
    {
        if (_cardBackground == color) return;
        _cardBackground = color;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CardBackground)));
    }

    /// <summary>
    /// 主题卡的身份色圆点：**唯一实现**在 Theming（`PaletteSolver.EditableSlots`）——
    /// 与外观面板的色槽共用同一套补位规则（预设身份色只有 1–3 个，直接展示会稀疏得像"缺了几个色"）。
    /// </summary>
    /// <remarks>
    /// <b>最浅那一枚换成"该主题实际生效的页面底色"</b>：卡面要显示"这套主题长什么样"—— 若某个主题的背景色成员比浅色底线还深
    /// （会被提亮一档），圆点跟着显示实际底色才与页面底同色；其余成员原样显示。
    /// </remarks>
    private IEnumerable<Color> BuildSwatches(ThemeDefinition definition)
    {
        var slots = PaletteSolver.EditableSlots(definition).ToList();
        if (slots.Count == 0) return slots.Select(ToMedia);

        var lightestIndex = 0;
        for (var i = 1; i < slots.Count; i++)
            if (ColorMath.Measure(slots[i]).T > ColorMath.Measure(slots[lightestIndex]).T) lightestIndex = i;

        slots[lightestIndex] = PaletteSolver.SurfaceBaseColor(definition);
        return slots.Select(ToMedia);
    }

    /// <summary>Argb → WPF Color（唯一转换点在 Theming；界面层零颜色字面量）。</summary>
    private static Color ToMedia(Argb c) => ColorMath.ToMedia(c);

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>目录里的主题定义；**「自选颜色」卡为 <c>null</c>**（它不是目录里的主题）。</summary>
    public ThemeDefinition? Definition { get; }

    private readonly LocValue _name;

    private readonly string _id;

    public LocValue Name => _name;

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
    public LocValue Summary { get; }

}

/// <summary>一个色槽（4/5 色自选配色）。**可以为空**（还没选颜色）。</summary>
/// <remarks>
/// 空槽不是"黑色"也不是"透明"这类会骗人的值：<see cref="Color"/> 为 <c>null</c>、
/// <see cref="Hex"/> 为空串，界面据此画虚线空心环 + 「+」。
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
    public LocValue Label => Loc.K("appearance.slot.n", Index + 1);

    /// <summary>HEX 文案（空槽 = 空串：**绝不**拿 <c>#000000</c> 这类假值冒充"已选"）。</summary>
    public string Hex => Color is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : string.Empty;

    /// <summary>槽位数值文案（空槽 = 「未选」）。</summary>
    public LocValue ValueText => IsEmpty ? Loc.K("common.noneSelected") : Loc.K("appearance.slot.hex", Hex);
}

/// <summary>字体来源（**先选来源，再在来源里选字体**）。</summary>
public enum FontSourceKind
{
    /// <summary>系统已装字体（只读：不可删，也不属于本应用）。</summary>
    System = 0,

    /// <summary>用户导入本应用的字体（可导入 / 可删除）。</summary>
    Custom = 1,
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
    /// 调色台草稿：<c>null</c> = **空槽**（尚未选色）。
    /// </summary>
    /// <remarks>
    /// 默认 = <see cref="MinSlots"/> 个**空槽**：早期实现开局倒进"当前主题的颜色"，
    /// 于是预设主题看起来可被直接改（其实只是复制），与"预设只读、自选是另一份"对不上。
    /// </remarks>
    private readonly List<Color?> _draft = new() { null, null, null, null };

    private readonly ThemeCardViewModel _customCard;

    private string _selectedThemeId = ThemeCatalog.DefaultId;
    private FontOptionViewModel? _selectedUiFont;
    private LocValue _status;
    private bool _hasDiagnostics;

    /// <summary>
    /// 当前生效外观的**归属**：true = 自选配色，false = 某个预设主题。
    /// </summary>
    /// <remarks>
    /// <b>互斥的唯一事实来源</b>：主题卡与自选区的高亮都由它 + <see cref="SelectedThemeId"/> 投影出来，
    /// 视图不持状态、不"点了哪边记哪边"（架构不变量 12）。此前没有这个字段，自选区的"高亮"按色槽数量
    /// 推导，曾出现"主题与自选配色同时亮着"。
    /// </remarks>
    private bool _customActive;

    /// <summary>草稿是否有**未应用**的改动（决定自选区显示「编辑中（未应用）」）。</summary>
    private bool _draftDirty;

    public AppearanceViewModel()
    {
        ThemeCards = new ObservableCollection<ThemeCardViewModel>(
            ThemeCatalog.All.Select(t => new ThemeCardViewModel(t)));

        // 第 12 张 = 「自选颜色」卡（**不是** ThemeCatalog 里的主题：目录仍是 11 套），排在主题网格最后一张。
        _customCard = ThemeCardViewModel.CreateCustomCard();
        ThemeCards.Add(_customCard);

        // 先把 Slots 建出来（界面才不会是"有数据却没有槽"）；**槽数随后由 SyncFromAppliedTheme
        // 对齐到当前生效外观的实际颜色数**（5 色主题 = 5 格，见该方法的注释）。
        RebuildSlots();

        // 字体下拉的**当前值**也要在构造期就投影出来：候选是惰性的（展开下拉才装载），
        // 但"现在用的是什么字体"不依赖候选列表 —— ProjectCurrentFonts 在池为空时用当前族名补占位项
        // （**只补属于当前来源的那一个**，见 ProjectCurrentFonts）；否则装载前下拉框是空白的。
        ProjectFontPools();
        ProjectCurrentFonts(ThemeService.CurrentUiFont);

        // 入口对齐：面板显示"当前**已应用**的外观"——主题卡高亮 + 互斥归属 + 色槽草稿
        // （回到本页时显示的主题/配色必须与已应用状态一致，否则选中态就是错的）
        SyncFromAppliedTheme();
    }

    /// <summary>
    /// **唯一投影点**：把"当前已应用的外观"投影到面板（互斥归属 + 主题卡选中态 + 色槽草稿）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>种子规则（预设/默认主题只读）</b>：
    /// </para>
    /// <list type="number">
    /// <item>当前生效 = **自选配色** → 色槽 = 该配色本身（重进面板要看到正在用的那份）；</item>
    /// <item>当前生效 = **预设主题** → 色槽**保持既有草稿不动**（内存里那份；从来没有过 = 空）。
    /// 早期实现是把当前主题的颜色倒进色槽，"倒进来"看起来就像预设可以被就地改；
    /// 想从某套主题改起请走 <see cref="StartFromCurrentTheme"/>（复制）；</item>
    /// <item>有**未应用改动**（<c>_draftDirty</c>）时**颜色**不重播种（入口刷新不许冲掉正在编辑的内容），
    /// 但**槽数一律跟着当前主题走**——见下条。</item>
    /// </list>
    /// <para>
    /// <b>槽数必须等于当前主题的颜色数</b>：调色台的色槽是"这套外观能被微调的 N 个颜色"，草稿槽数恒为 4
    /// 会让 5 色主题（出厂默认）在面板上显示成 4 色 —— 与主题卡上的 5 个色点自相矛盾。
    /// 故每次都把草稿槽数对齐到 <see cref="PaletteSolver.EditableSlots"/> 的实际个数（4 或 5），
    /// **只调槽数、不碰颜色**（缩掉的槽若已填色会一并消失，见下方注释）。
    /// </para>
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
        Raise(nameof(SelectedThemeId));

        // 名字 / 按钮文案 / 整句说明 / 槽数 = 同一个投影点（`ProjectAppliedTheme`），这里不再各写一遍
        ProjectAppliedTheme();

        // 「自动调整颜色」开关也要跟着**已应用**的状态走（它是全局偏好，可能被别处改过 / 从偏好恢复）
        Raise(nameof(AutoAdjustColors));
        Raise(nameof(PaletteModeHint));

        // 主题卡色点 = **当前模式**下该主题实际生效的页面底 —— 每次入口对齐都重投影一次，
        // 不能只在切开关那一条路上重投影（否则换主题 / 重进面板后的色点可能停在旧模式的口径上）。
        RepojectAllThemeCards();

        ProjectCardSelection();
        RaiseCustomState();
    }

    /// <summary>
    /// 把草稿槽数对齐到目标值（4/5），**只动槽数、不动颜色、不改"未应用"标记**。
    /// </summary>
    /// <remarks>
    /// "未应用标记"（<c>_draftDirty</c>）表达的是**改过但没应用**：槽数跟着当前主题走是**投影**，
    /// 不是编辑动作，所以这里不标脏、也不清脏（既有的未应用改动依然如实显示为"编辑中"）。
    /// </remarks>
    private void SyncDraftSlotCount(int target)
    {
        target = Math.Clamp(target, MinSlots, MaxSlots);
        if (target == _draft.Count) return;

        while (_draft.Count > target) RemoveSlotCore();
        while (_draft.Count < target) AddSlotCore();
        RebuildSlots();
    }

    /// <summary>
    /// **主题一变就刷新的那一族投影**：名字 / 按钮文案 / 整句说明 / 色槽格数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须存在</b>：这套投影最早只写在 <see cref="SyncFromAppliedTheme"/> 里，
    /// 而它只在"进面板"（`Refresh`）时被调用 —— 点主题卡走 <see cref="ApplyThemeCard"/>、
    /// 点起点走 <see cref="StartFromCurrentTheme"/>，两条路都不经过它，于是换主题之后
    /// 按钮文案与说明句照旧写着**上一套**外观的名字（VM 单测同样卡住过它）。
    /// </para>
    /// <para>
    /// 现在它是**唯一投影点**：任何"当前生效外观变了"的地方都调它一次，三处文案与槽数一起跟上。
    /// 它**不碰颜色**（草稿独立于主题，换主题不许冲掉）也不碰"未应用"标记，因此可以随便调。
    /// </para>
    /// </remarks>
    private void ProjectAppliedTheme()
    {
        var current = ThemeService.Current;
        AppliedThemeName = ThemeNames.Of(current.Id);
        SyncDraftSlotCount(PaletteSolver.EditableSlots(current).Count);
        Raise(nameof(AppliedThemeName));
        Raise(nameof(StartFromCurrentThemeLabel));
        Raise(nameof(DraftIntro));
    }

    /// <summary>当前生效外观的显示名（调色台的说明文案与「起点」按钮文案都用它）。</summary>
    public LocValue AppliedThemeName { get; private set; } = ThemeNames.Of(ThemeCatalog.DefaultId);

    /// <summary>
    /// 「以当前外观为起点」按钮的文案（带上名字，可一眼看出复制的是哪一套）。
    /// </summary>
    public LocValue StartFromCurrentThemeLabel => Loc.K("appearance.btn.startFrom", AppliedThemeName);

    /// <summary>
    /// 调色台卡片的整句说明文案 = **整句 + 当前外观名 + 尾句**（一个字符串属性）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么整句都在 VM 里</b>：这句话原先在 XAML 里拆成 `<c>Run</c> 字面量 +
    /// <c>Run Text="{Binding …}"</c> + <c>Run</c> 字面量</b> 三段 —— 断句与绑定分散在两处，
    /// 运行期是否跟着主题走无法在 VM 层被观测（VM 单测测不到、探针也只断言按钮不看这句）。
    /// 现行口径：**这类"含变量的整句"由 VM 出一个字符串属性**，视图只摆一个 <c>TextBlock</c>，
    /// 于是"这句里写的是哪套外观"与按钮文案同源（同一个 <see cref="AppliedThemeName"/>）、
    /// 变更通知也只有一个出口（<see cref="SyncFromAppliedTheme"/>）。
    /// </para>
    /// </remarks>
    public LocValue DraftIntro => Loc.K("appearance.palette.draftIntro", AppliedThemeName);

    // 字体候选**惰性**（见 EnsureFontsLoadedAsync）：构造期不枚举系统字体 ——
    // 枚举开销与机器上装的字体数量成正比，不打开字体下拉就不该付这笔钱。
    // 当前选中字体直接取 ThemeService 的状态，不依赖候选列表。
    private bool _fontsLoaded;

    /// <summary>
    /// 惰性装载字体候选（首次真正需要列表时调用一次）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么惰性</b>：候选列表要枚举**系统全部字体**，开销与机器上装的字体数量成正比
    /// （缓存只解决第二次之后；第一次无论如何都要付）。不打开字体下拉就不该付这笔钱。
    /// </para>
    /// <para>
    /// <b>为什么在后台线程</b>（<see cref="Fonts.FontCatalog.LoadAsync"/>）：付在 UI 线程上 =
    /// 每装一批字体就多卡一次，且卡顿随机器变差而放大。正确做法是异步 + 缓存，
    /// 而不是要求"别去触发它"。
    /// </para>
    /// <para>
    /// 失败**如实播报**（不静默回退成空列表）：枚举失败要在界面上可见，而不是给一个空下拉。
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
            // 失败可重试：标志退回 false，再次展开下拉就会重跑（而不是永久卡在空列表）
            _fontsLoaded = false;
            LpLog.Error("failed to load font candidates", ex, LogCategory);
            Status = Loc.K("appearance.status.fontLoadFailed");
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主题卡（出厂默认排第一；**最后一张 = 自选颜色卡**）。</summary>
    public ObservableCollection<ThemeCardViewModel> ThemeCards { get; }

    /// <summary>自选配色的色槽。</summary>
    public ObservableCollection<ColorSlotViewModel> Slots { get; } = new();

    /// <summary>界面字体候选（**当前来源**那一份）。</summary>
    public ObservableCollection<FontOptionViewModel> UiFonts { get; } = new();

    // ── 字体来源二选一 ────────────────────────────────────────────────
    // 两个池：{系统,自定义}。下拉只显示当前来源那份；另一个保留"当前已选字体"的来源。
    // ⚠️ 等宽字体行**已删除**：等宽字体（ID / 网址显示用）固定为默认族，面板不再提供任何入口。

    private ObservableCollection<FontOptionViewModel> SystemUiFonts { get; } = new();
    private ObservableCollection<FontOptionViewModel> CustomUiFonts { get; } = new();

    private FontSourceKind _fontSource = FontSourceKind.System;

    /// <summary>
    /// 当前在用的字体来源（系统已装 / 自定义导入）—— **先选来源，再在来源里选字体**（二选一）。
    /// </summary>
    /// <remarks>
    /// 投影口径：已应用字体落在哪个池里，来源就是哪个（与颜色那边的"互斥归属"同一套做法：
    /// 归属由**事实**推出来，不额外存一个可能与事实矛盾的开关）。
    /// </remarks>
    public FontSourceKind FontSource
    {
        get => _fontSource;
        set
        {
            if (_fontSource == value) return;
            _fontSource = value;
            // 先投影、后播报：通知的语义是"来源已经换了"，而换了就意味着候选池与当前字体都已就位。
            // 反过来（先 Raise 再投影）会让监听方在这一瞬看到"来源=系统、候选还是上一份（空）"的半成品，
            // 面板据此往下拉里塞一个不在候选里的选中项 —— ComboBox 会就此钉死成空白框。
            ProjectCurrentFonts(ThemeService.CurrentUiFont);
            Raise(nameof(FontSource));
            Raise(nameof(FontSourceIndex));
            Raise(nameof(IsCustomFontSource));
            // 说明句也依赖来源（旧实现漏了这一条 → 切来源后那句还写着上一种来源的话）
            Raise(nameof(FontSourceHint));
            // 目标来源的候选池是空的（本次会话还没装载成功过）→ 当场把装载踢起来；
            // 装载完成会自动重投影（ReloadFontsAsync 收尾），不必"再展开一次下拉"。
            // 否则切到自定义字体再切回系统字体时，系统字体那一栏会是空的。
            if (UiFonts.Count == 0) _ = EnsureFontsLoadedAsync();
        }
    }

    /// <summary>来源分段的选中索引（0 = 系统字体，1 = 自定义字体）——共享滑动分段组件的绑定入口。</summary>
    public int FontSourceIndex
    {
        get => (int)_fontSource;
        set => FontSource = value == 0 ? FontSourceKind.System : FontSourceKind.Custom;
    }

    /// <summary>当前来源是否是"自定义（导入）"（控制导入/删除按钮与说明文案的可见性）。</summary>
    public bool IsCustomFontSource => _fontSource == FontSourceKind.Custom;

    /// <summary>当前来源的说明文案（导入/删除按钮的可用性也据此）。</summary>
    public LocValue FontSourceHint => _fontSource == FontSourceKind.System
        ? Loc.K("appearance.font.systemHint")
            : Loc.K("appearance.font.customHint");

    // ── 「自动调整颜色」开关 ───────────────────────────────────────────

    /// <summary>
    /// 「自动调整颜色」：<c>true</c>（**出厂缺省**）= 按明度档位表自动重排（层感与可读性有保证）；
    /// <c>false</c> = **直配**（尽量原样使用所选颜色，只在颜色不够时按本色补）。
    /// </summary>
    /// <remarks>
    /// 开关一变就**当场重新应用当前外观**（<see cref="ThemeService.SetPaletteMode"/> 内含落盘），
    /// 并把主题卡色点一起重投影 —— 两种模式下"背景色成员"的取值可能不同，
    /// 卡面必须显示**该模式下实际生效的底色**，否则那个圆点又会与背景不同色（两种状态都要能融合）。
    /// </remarks>
    public bool AutoAdjustColors
    {
        get => ThemeService.PaletteMode == PaletteMode.Auto;
        set
        {
            var target = value ? PaletteMode.Auto : PaletteMode.Exact;
            if (ThemeService.PaletteMode == target) return;

            try
            {
                ThemeService.SetPaletteMode(target);
                RepojectAllThemeCards();
                // 状态行不播报成功 —— 开关自身的开/关位置 + 卡片里的说明句已经说清了；失败照报（catch 里那条）。
                Status = LocValue.Empty;
            }
            catch (Exception ex)
            {
                LpLog.Error($"failed to switch palette application mode (auto={value})", ex, LogCategory);
                Status = Loc.K("appearance.status.switchFailed");
            }
            Raise(nameof(AutoAdjustColors));
            Raise(nameof(PaletteModeHint));
        }
    }

    /// <summary>开关的说明文案（两种模式各自说清"界面会怎么变"）。</summary>
    public LocValue PaletteModeHint => AutoAdjustColors
        ? Loc.K("appearance.palette.autoOn")
        : Loc.K("appearance.palette.autoOff");

    /// <summary>把所有主题卡的色点按**当前模式**重投影（切换开关 / 重新应用主题后调用）。</summary>
    private void RepojectAllThemeCards()
    {
        foreach (var card in ThemeCards) card.RefreshSwatches();
    }

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
    /// <b>严格二选一</b>：自选配色生效时**只有**第 12 张「自选颜色」卡高亮、11 张预设卡全部不高亮；
    /// 预设生效时只有那一张高亮、自选颜色卡不高亮。两个方向都由 <see cref="IsCustomActive"/> 唯一定夺，
    /// 不靠 <c>SelectedThemeId</c> 恰好等于某个 id（那是第二份事实源）——否则会出现"两块都亮着、
    /// 分不清谁在生效"。
    /// </para>
    /// </remarks>
    private void ProjectCardSelection()
    {
        foreach (var card in ThemeCards)
            card.IsSelected = card.IsCustom
                ? _customActive
                : !_customActive && string.Equals(card.Id, _selectedThemeId, StringComparison.Ordinal);
    }

    /// <summary>
    /// 选中的界面字体。**忽略 null 写入**（见下）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要忽略 null</b>：下拉的 <c>SelectedItem</c> 是**双向绑定**到这里的，
    /// 而 `ItemsSource` 在候选装载时被整体替换 —— 替换的那一刻 WPF 会把 <c>SelectedItem</c>
    /// 变成 null 并**回写**给本属性，于是"当前字体"被自己的空候选擦掉，下拉框显示成**空白**
    /// （看起来像"拉取不到字体"）。
    /// </para>
    /// <para>
    /// 处置：**候选为空导致的清除不是选择动作**，一律忽略；"当前该用哪个字体"只由
    /// <c>ProjectCurrentFonts</c>（唯一的投影点）负责，它在没有候选时用当前族名补一个占位项，
    /// 装载完成后换成真候选。要真正清空选择请直接写字段（本类型内部不需要这种操作）。
    /// </para>
    /// </remarks>
    public FontOptionViewModel? SelectedUiFont
    {
        get => _selectedUiFont;
        set
        {
            if (value is null) return;
            _selectedUiFont = value;
            Raise(nameof(SelectedUiFont));
            Raise(nameof(FontInspection));
            Raise(nameof(HasFontInspection));
        }
    }

    /// <summary>派生诊断的每一行（语言无关的级别标记 + 在当前语言下取词的文案值；空 = 无诊断）。</summary>
    public IReadOnlyList<DiagnosticLine> DiagnosticLines { get; private set; } = Array.Empty<DiagnosticLine>();

    /// <summary>诊断行 = 标记（<c>✗</c> 拒绝级 / <c>·</c> 提示）+ 文案值。</summary>
    public readonly record struct DiagnosticLine(string Marker, LocValue Text);

    /// <summary>是否有诊断（控制显示）。</summary>
    public bool HasDiagnostics
    {
        get => _hasDiagnostics;
        private set { _hasDiagnostics = value; Raise(nameof(HasDiagnostics)); }
    }

    /// <summary>结果播报（状态栏口径）。</summary>
    public LocValue Status
    {
        get => _status;
        private set { if (!_status.Equals(value)) { _status = value; Raise(nameof(Status)); } }
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
            SetCustomActive(false);   // 互斥：用预设 = 自选区让出Loc.K("common.current")
            SelectedThemeId = card.Id;
            // ⚠️ 换主题之后**必须**刷新"当前外观"那一族投影（名字 / 起点按钮文案 / 说明句 / 槽数）：
            //    漏掉这一步，换了主题后按钮文案仍是上一套外观的名字。
            ProjectAppliedTheme();
            // 色点也要按**当前配色应用方式**重投影 —— 换主题同样属于"当前外观变了"，
            // 色点不能停在旧模式的口径上。
            RepojectAllThemeCards();
            ThemeService.SaveCurrentPreferences();
            // 状态行不播报"已应用主题「X」"：当前生效的是哪套外观由**主题卡高亮 + 「当前使用」徽标**表达，
            // 再写一行文字是重复信息。失败仍然照报（下面 catch 里那两条），那是必须看见的。
            DiagnosticLines = Array.Empty<DiagnosticLine>();
            HasDiagnostics = false;
        }
        catch (Exception ex)
        {
            // 写偏好失败不静默（观测面纪律），且保持原主题（Apply 已成功但偏好没落盘 → 如实说清）
            LpLog.Error($"failed to save preferences after applying theme '{card.Name}'", ex, LogCategory);
            Status = Loc.K("appearance.status.themeAppliedPrefFailed");
        }
    }

    /// <summary>
    /// 「以当前外观为起点」：把**当前生效外观**的可编辑色槽**复制**进调色台并**立即应用**为自选配色。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>复制而不是引用</b>：色槽拿到的是 <see cref="PaletteSolver.EditableSlots"/> 的**值拷贝**
    /// （<c>Argb</c> 是不可变值类型），随后所有编辑都只落在草稿里；预设的
    /// <see cref="ThemeDefinition.Palette"/> 从此与面板无关 —— 这是"默认主题不可改"的结构性保证，
    /// 不靠"记得别写回去"。
    /// </para>
    /// <para>
    /// <b>点一下就该生效</b>：早期实现只把颜色倒进草稿、把归属留在预设上，于是按了按钮却"什么都没发生"
    /// （主题卡还是预设那张、界面一点没变），还得再点一次全局的「应用」（该按钮已删除）——
    /// 自选配色的唯一应用入口 = 第 12 张「自选颜色」卡。
    /// 现行 = 复制完**当场**走 <see cref="ApplyDraft"/> 的同一条应用路径：归属切到自选、
    /// 第 12 张「自选颜色」卡亮起、界面立刻换成这份配色（它是当前主题的拷贝，视觉上与刚才一致，
    /// 但从此每一格都可微调）。**不另写第二套应用逻辑**——合法性门槛、播报与落盘全在 <see cref="ApplyDraft"/>。
    /// </para>
    /// </remarks>
    public void StartFromCurrentTheme()
    {
        var source = ThemeService.Current;
        var slots = PaletteSolver.EditableSlots(source);

        _draft.Clear();
        _draft.AddRange(slots.Select(c => (Color?)ToMedia(c)));
        MarkDraftDirty();                 // 先标脏：ApplyDraft 的门槛/诊断按Loc.K("common.draft")口径走，成功后才清
        RebuildSlots();

        ApplyDraft();                     // 唯一应用入口（空槽门槛 + 校验 + 归属切换 + 落盘）
        // 成功不播报；失败时 ApplyDraft 已把原因写在状态行上，这里不许覆盖
        // ⚠️ 判据用 **IsCustomActive**（公开投影），不是 `_customActive` 字段——"生效了没"本来就该问投影。
    }

    /// <summary>把当前槽位当作自选配色应用。</summary>
    /// <remarks>
    /// <b>应用门槛</b>：槽里有空位（不足 4 个颜色）→ **拒绝应用**，并在状态行说清"还差几个"；
    /// 主题校验（4/5 色、可读性提示）照旧走 <see cref="ThemeValidator"/>。
    /// 两道门槛都不弹窗 —— 理由写在状态行上。
    /// </remarks>
    public void ApplyDraft()
    {
        var missing = _draft.Count(c => c is null);
        if (missing > 0)
        {
            Status = Loc.K("appearance.palette.missingSlots", missing);
            // 与 RefreshDraftDiagnostics 同一口径：空槽是"还没选完"，**不是**"配色不合法"，
            // 所以这里不把 palette-size 那条红色错误摆出来。
            DiagnosticLines = new[] { new DiagnosticLine("·", EmptySlotsHint(missing)) };
            HasDiagnostics = true;
            return;
        }

        var definition = BuildDraftDefinition();
        var issues = ThemeValidator.Validate(definition);
        ShowDiagnostics(issues);
        if (ThemeValidator.HasErrors(issues))
        {
            Status = Loc.K("appearance.status.paletteInvalid");
            return;
        }

        try
        {
            ThemeService.Apply(definition);
            SetCustomActive(true);    // 互斥：用自选配色 = 预设卡让出Loc.K("common.current")（自选颜色卡亮起）
            ClearDraftDirty();        // 草稿 = 当前值，不再有Loc.K("common.draftUnapplied")
            SelectedThemeId = definition.Id;
            ProjectAppliedTheme();    // 名字 / 按钮文案 / 说明句 / 槽数一起跟上（换主题的公共出口）
            RepojectAllThemeCards();  // 色点按当前模式重投影（与 ApplyThemeCard 同一口径）
            ThemeService.SaveCurrentPreferences();
            // 状态行同样不播报成功 —— 只报失败
            DiagnosticLines = Array.Empty<DiagnosticLine>();
            HasDiagnostics = false;
        }
        catch (Exception ex)
        {
            LpLog.Error("failed to save preferences after applying the custom palette", ex, LogCategory);
            Status = Loc.K("appearance.status.paletteAppliedPrefFailed");
        }
    }

    /// <summary>实时校验当前草稿并把诊断写进面板（无副作用、不应用）。</summary>
    public void RefreshDraftDiagnostics()
    {
        // ⚠️ 还没填满的草稿**不是**"配色不合法"：空槽数不足 4 时，`palette-size` 会以
        // "✗ 主题需要 4 或 5 个颜色（当前 0 个）" 的红色错误样子出现在刚进面板的空态上 ——
        // 那不是错误（只是"还没选完"）。这里把它换成一句中性提示；
        // 真正的不合法（HEX 非法等）照旧显示。
        var empty = _draft.Count(c => c is null);
        if (empty > 0)
        {
            DiagnosticLines = new[] { new DiagnosticLine("·", EmptySlotsHint(empty)) };
            HasDiagnostics = true;
            return;
        }

        var issues = ThemeValidator.Validate(BuildDraftDefinition());
        ShowDiagnostics(issues);
    }

    /// <summary>
    /// 空槽态的中性提示（唯一实现：<see cref="RefreshDraftDiagnostics"/> 与 <see cref="ApplyDraft"/>
    /// 的拒绝路径共用）。"选够几个"必须跟着**当前槽数**走（4 色主题 4 个、5 色主题 5 个）——
    /// 写死"选够 4 个"在 5 色草稿上是假话。
    /// </summary>
    private LocValue EmptySlotsHint(int empty) => Loc.K("theme.issue.emptySlots", empty, _draft.Count);

    /// <summary>
    /// 草稿 → 主题定义（自选配色）。空槽**不冒充颜色**：直接不参与（于是"色数不是 4/5"会被校验抓出来）。
    /// </summary>
    private ThemeDefinition BuildDraftDefinition() => new()
    {
        Id = ThemeCardViewModel.CustomCardId,
        Source = ThemeSource.UserDefined,
        Palette = _draft.Where(c => c is not null).Select(c => ToArgb(c!.Value)).ToArray(),
        NeutralHueOverride = null,   // 自选配色按自己的中性池派生（不钉值）
    };

    private void ShowDiagnostics(IReadOnlyList<ThemeIssue> issues)
    {
        if (issues.Count == 0)
        {
            DiagnosticLines = Array.Empty<DiagnosticLine>();
            HasDiagnostics = false;
            return;
        }
        // 错误在前（必须拒绝的），提示在后。句子由本层按**码**取词——引擎的 Message 是英文技术文案，只进日志
        DiagnosticLines = issues
            .OrderByDescending(i => i.Severity)
            .Select(i => new DiagnosticLine(
                i.Severity == ThemeIssueSeverity.Error ? "✗" : "·",
                IssueText(i)))
            .ToArray();
        HasDiagnostics = true;
    }

    /// <summary>
    /// 主题校验码 → 界面文案。未登记的码<b>照抛</b>：校验器与界面两处必须同批改，
    /// 悄悄把引擎原文画上屏就是混语残留（护栏 G9 禁的正是这个形状）。
    /// </summary>
    private static LocValue IssueText(ThemeIssue issue) => issue.Code switch
    {
        "palette-size" => Loc.K("theme.issue.paletteSize", issue.Args!),
        "no-dark" => Loc.K("theme.issue.noDark"),
        "no-light" => Loc.K("theme.issue.noLight"),
        "near-duplicate" => Loc.K("theme.issue.nearDuplicate", issue.Args!),
        _ => throw new NotSupportedException($"theme issue '{issue.Code}' has no display text key"),
    };

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
    /// 把一个色槽清回**空槽**（取色盘的「清除」= 把该槽设回空槽，**不是设成黑色**）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SetSlotColor"/> **同一条草稿路径**（标脏 → 重建投影 → 刷新诊断），
    /// 不给自己留第二条改草稿的路。空槽是真实语义（虚线空心环 + 「未选」），
    /// 清空后会自然反映到"还差 N 个颜色"的门槛与第 12 张「自选颜色」卡的色点上。
    /// </remarks>
    public void ClearSlot(int index)
    {
        if (index < 0 || index >= _draft.Count) return;
        _draft[index] = null;
        MarkDraftDirty();
        RebuildSlots();
        RefreshDraftDiagnostics();
    }

    /// <summary>
    /// 「清空颜色」：草稿清成空槽，并**回到出厂默认（紫罗兰）外观**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>清空 = 回到紫罗兰</b>：早期实现只清草稿、**不动已应用的外观** ——
    /// 按了"清空"界面上还是上一套配色（"清空"名不副实）。现行 = 一并回出厂默认主题：
    /// <see cref="ThemeService.ApplyDefault"/> + 落盘 + "当前外观"那一族投影（名字 / 起点按钮 /
    /// 说明句 / 槽数 / 主题卡高亮）。
    /// </para>
    /// <para>
    /// 与 <see cref="ResetToDefaultAsync"/> 的分工：那个是**整套外观恢复出厂**（主题 + 字体 +
    /// 清掉偏好文件）；这里只回主题、不碰字体、也不删偏好文件（回默认这件事本身要写进偏好）。
    /// </para>
    /// <para>
    /// 清完必须走 <see cref="RefreshDraftDiagnostics"/>（而不是把诊断一清了之）：
    /// 空槽态要如实显示"还差 N 个颜色"这条中性提示，否则面板上没有任何一处说明还差几个。
    /// </para>
    /// </remarks>
    public void ClearDraft()
    {
        ClearDraftCore();

        try
        {
            ThemeService.ApplyDefault();
            SetCustomActive(false);          // 互斥：回预设 = 自选区让出"当前使用"
            SelectedThemeId = ThemeCatalog.DefaultId;
            ProjectAppliedTheme();           // 名字 / 起点按钮文案 / 说明句 / 槽数一起跟上（换外观的公共出口）
            RepojectAllThemeCards();         // 色点按当前模式重投影（与点主题卡同一口径）
            ThemeService.SaveCurrentPreferences();
            // 成功不播报 —— 主题卡高亮 + 「当前使用」徽标就是结果
            Status = LocValue.Empty;
        }
        catch (Exception ex)
        {
            LpLog.Error("failed to fall back to the default theme after clearing colours", ex, LogCategory);
            Status = Loc.K("appearance.status.clearedFallbackFailed");
        }

        RefreshDraftDiagnostics();
    }

    /// <summary>
    /// 只把草稿清成空槽（<see cref="MinSlots"/> 个空槽），**不碰已应用外观**——
    /// 「恢复默认外观」自己会清主题/字体/偏好文件，故它用这条纯草稿路径。
    /// </summary>
    private void ClearDraftCore()
    {
        _draft.Clear();
        _draft.AddRange(Enumerable.Repeat<Color?>(null, MinSlots));
        ClearDraftDirty();
        RebuildSlots();
    }

    /// <summary>空槽打开取色盘时的初始颜色 = 当前主题的强调填充（令牌派生，不发明色值）。</summary>
    /// <remarks>
    /// 取色盘需要一个初值（HSV 的三个分量总要有一个起点）；用当前主题的强调色既不是"黑色"这类
    /// 会骗人的假值，也不是写死的字面量 —— 它就是此刻界面上的主色。
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
        // 新槽 = **空槽**。
        // ⚠️ 早期实现是拿上一个色"派生"一个建议色（色相 +40°）—— 那是**编造的颜色**：
        //    界面上会出现一个从未选过的色点，且它还会被算进"应用门槛"。空槽 + 「+」才是诚实的。
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

    // ── 小工具 ───────────────────────────────────────────────────────

    /// <summary>Argb → WPF Color（唯一转换点在 Theming）。</summary>
    private static Color ToMedia(Argb c) => ColorMath.ToMedia(c);

    private static Argb ToArgb(Color c) => ColorMath.FromMedia(c);

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
