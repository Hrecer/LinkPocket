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

    /// <summary>按**当前配色应用方式**重建色点（切换「自动调整颜色」开关后调用）。</summary>
    /// <remarks>
    /// 两种模式下"背景色成员"的取值可能不同（深色背景会被提亮到浅色底线），所以卡面必须重投影 ——
    /// 否则那个圆点会与它自己的底不同色（用户令：不管开关开着还是关着，色点都要能融合）。
    /// </remarks>
    public void RefreshSwatches()
    {
        if (Definition is not { } def) return;   // 「自选颜色」卡的色点由调色台草稿决定，不在此列
        SetSwatches(BuildSwatches(def));
    }

    /// <summary>
    /// 主题卡的身份色圆点：**唯一实现**在 Theming（`PaletteSolver.EditableSlots`）——
    /// 与外观面板的色槽共用同一套补位规则（预设身份色只有 1–3 个，直接展示会稀疏得像"缺了几个色"）。
    /// </summary>
    /// <remarks>
    /// <b>最浅那一枚换成"该主题实际生效的页面底色"</b>（用户令 2026-09-20："背景色那个圆与背景融合，
    /// 这正是我们想要的效果"）：卡面要显示"这套主题长什么样"—— 若某个主题的背景色成员比浅色底线还深
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

/// <summary>字体来源（用户令 2026-09-20：**先选来源，再在来源里选字体**）。</summary>
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

        // 先把 Slots 建出来（界面才不会是"有数据却没有槽"）；**槽数随后由 SyncFromAppliedTheme
        // 对齐到当前生效外观的实际颜色数**（5 色主题 = 5 格，见该方法的注释）。
        RebuildSlots();

        // 字体下拉的**当前值**也要在构造期就投影出来（用户报障 2026-09-20 第二轮："我选系统字体，
        // 此时根本就拉取不到任何字体"——两个框当时是空白的）：候选是惰性的（展开下拉才装载），
        // 但"现在用的是什么字体"不依赖候选列表 —— ProjectCurrentFonts 在池为空时用当前族名补占位项。
        ProjectFontPools();
        ProjectCurrentFonts(ThemeService.CurrentUiFont, ThemeService.CurrentMonoFont);

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
    /// <item>有**未应用改动**（<c>_draftDirty</c>）时**颜色**不重播种（入口刷新不许冲掉用户正在编辑的东西），
    /// 但**槽数一律跟着当前主题走**——见下条。</item>
    /// </list>
    /// <para>
    /// <b>槽数必须等于当前主题的颜色数（用户报障 2026-09-20："我们很多默认主题不是五色的吗？
    /// 为什么到了这里变成四色"）</b>：调色台的色槽是"这套外观能被微调的 N 个颜色"，草稿槽数恒为 4
    /// 会让 5 色主题（出厂默认）在面板上显示成 4 色 —— 与主题卡上的 5 个色点自相矛盾。
    /// 故每次都把草稿槽数对齐到 <see cref="PaletteSolver.EditableSlots"/> 的实际个数（4 或 5），
    /// **只调槽数、不碰颜色**（缩掉的槽若是用户已填的颜色会一并消失，这一点由下方注释明说）。
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

        // 主题卡色点 = **当前模式**下该主题实际生效的页面底（用户令 2026-09-20 第二轮：
        // "当我们开关自动调整颜色的按钮时，主题那个色点也会同步修改"）——所以每次入口对齐都重投影一次，
        // 不能只在切开关那一条路上重投影（否则换主题 / 重进面板后的色点可能停在旧模式的口径上）。
        RepojectAllThemeCards();

        ProjectCardSelection();
        RaiseCustomState();
    }

    /// <summary>
    /// 把草稿槽数对齐到目标值（4/5），**只动槽数、不动颜色、不改"未应用"标记**。
    /// </summary>
    /// <remarks>
    /// "未应用标记"（<c>_draftDirty</c>）表达的是**用户改过没应用**：槽数跟着当前主题走是**投影**，
    /// 不是用户的编辑，所以这里不标脏、也不清脏（用户之前那份未应用改动依然如实显示为"编辑中"）。
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
    /// <b>为什么必须存在（用户报障 2026-09-20："为什么这里一直显示以默认紫罗兰为起点，一直都没有更改过"）</b>：
    /// 这套投影原先**只有 <see cref="SyncFromAppliedTheme"/> 里有**，而它只在"进面板"（`Refresh`）时被调用
    /// —— 点主题卡走 <see cref="ApplyThemeCard"/>、点起点走 <see cref="StartFromCurrentTheme"/>，
    /// 两条路都不经过它，于是换主题之后按钮文案与说明句照旧写着**上一套**外观的名字
    /// （用户看到的"从来没变过"就是这个；VM 单测同样卡住过它）。
    /// </para>
    /// <para>
    /// 现在它是**唯一投影点**：任何"当前生效外观变了"的地方都调它一次，三处文案与槽数一起跟上。
    /// 它**不碰颜色**（草稿是用户的，换主题不许冲掉）也不碰"未应用"标记，因此可以随便调。
    /// </para>
    /// </remarks>
    private void ProjectAppliedTheme()
    {
        var current = ThemeService.Current;
        AppliedThemeName = current.Name;
        SyncDraftSlotCount(PaletteSolver.EditableSlots(current).Count);
        Raise(nameof(AppliedThemeName));
        Raise(nameof(StartFromCurrentThemeLabel));
        Raise(nameof(DraftIntro));
    }

    /// <summary>当前生效外观的显示名（调色台的说明文案与「起点」按钮文案都用它）。</summary>
    public string AppliedThemeName { get; private set; } = ThemeCatalog.Default.Name;

    /// <summary>
    /// 「以当前外观为起点」按钮的文案（带上名字，用户一眼知道复制的是哪一套）。
    /// </summary>
    public string StartFromCurrentThemeLabel => $"以「{AppliedThemeName}」为起点";

    /// <summary>
    /// 调色台卡片的整句说明文案 = **整句 + 当前外观名 + 尾句**（一个字符串属性）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么整句都在 VM 里（用户报障 2026-09-20："为什么这里一直显示以默认紫罗兰为起点，
    /// 一直都没有更改过"）</b>：这句话原先在 XAML 里拆成 `<c>Run</c> 字面量 + <c>Run Text="{Binding …}"</c> +
    /// <c>Run</c> 字面量</b> 三段 —— 断句与绑定分散在两处，运行期是否跟着主题走无法在 VM 层被观测
    /// （VM 单测测不到、探针也只断言按钮不看这句）。
    /// 现行口径：**这类"含变量的整句"由 VM 出一个字符串属性**，视图只摆一个 <c>TextBlock</c>，
    /// 于是"这句里写的是哪套外观"与按钮文案同源（同一个 <see cref="AppliedThemeName"/>）、
    /// 变更通知也只有一个出口（<see cref="SyncFromAppliedTheme"/>）。
    /// </para>
    /// </remarks>
    public string DraftIntro =>
        "这里只编辑你自己的配色，上面的主题是只读的。点色槽用取色盘选色；想从现成外观改起，"
        + $"点下面的按钮把「{AppliedThemeName}」的颜色复制进来 —— 复制会立即应用为自选配色"
        + "（「自选颜色」卡随即高亮），之后点色槽微调即可。";

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

    /// <summary>系统 + 已导入的界面字体候选（**当前来源**那一份）。</summary>
    public ObservableCollection<FontOptionViewModel> UiFonts { get; } = new();

    /// <summary>系统 + 已导入的等宽字体候选（**当前来源**那一份）。</summary>
    public ObservableCollection<FontOptionViewModel> MonoFonts { get; } = new();

    // ── 字体来源二选一（用户令 2026-09-20）─────────────────────────────
    // 四个池：{界面,等宽} × {系统,自定义}。下拉只显示当前来源那份；另两份保留"当前已选字体"的来源。

    private ObservableCollection<FontOptionViewModel> SystemUiFonts { get; } = new();
    private ObservableCollection<FontOptionViewModel> SystemMonoFonts { get; } = new();
    private ObservableCollection<FontOptionViewModel> CustomUiFonts { get; } = new();
    private ObservableCollection<FontOptionViewModel> CustomMonoFonts { get; } = new();

    private FontSourceKind _fontSource = FontSourceKind.System;

    /// <summary>
    /// 当前在用的字体来源（系统已装 / 自定义导入）—— **先选来源，再在来源里选字体**（二选一）。
    /// </summary>
    /// <remarks>
    /// 投影口径：已应用字体落在哪个池里，来源就是哪个（与颜色那边的"互斥归属"同一套做法：
    /// 归属由**事实**推出来，不是让用户另存一个可能与事实矛盾的开关）。
    /// </remarks>
    public FontSourceKind FontSource
    {
        get => _fontSource;
        set
        {
            if (_fontSource == value) return;
            _fontSource = value;
            Raise(nameof(FontSource));
            Raise(nameof(FontSourceIndex));
            Raise(nameof(IsCustomFontSource));
            ProjectFontPools();
            ProjectCurrentFonts(ThemeService.CurrentUiFont, ThemeService.CurrentMonoFont);
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
    public string FontSourceHint => _fontSource == FontSourceKind.System
        ? "系统已装字体：只读（属于系统，本应用不修改、也删不掉）"
        : "自定义字体：导入的字体文件存在本应用目录里，可随时删除（不会动系统字体）";

    // ── 「自动调整颜色」开关（用户令 2026-09-20）────────────────────────
    // 用户原话："单独做一个开关按钮，默认关闭，就是这个按钮大概的表述就是关闭那种自动调整颜色的功能，
    // 纯按照你输入的颜色尽量直接优先按照你的颜色，除非颜色不够……尽量把你选的颜色全部应用上"。

    /// <summary>
    /// 「自动调整颜色」：<c>true</c>（**出厂缺省**）= 按明度档位表自动重排（层感与可读性有保证）；
    /// <c>false</c> = **直配**（尽量原样用你给的颜色，只在颜色不够时按本色补）。
    /// </summary>
    /// <remarks>
    /// 用户令 2026-09-20 第二轮："我们默认是打开自动调整颜色的，自动调整颜色是一个那种滑动开关"。
    /// 开关一变就**当场重新应用当前外观**（<see cref="ThemeService.SetPaletteMode"/> 内含落盘），
    /// 并把主题卡色点一起重投影 —— 两种模式下"背景色成员"的取值可能不同，
    /// 卡面必须显示**该模式下实际生效的底色**，否则那个圆点又会与背景不同色（用户令：开关两种状态都要能融合）。
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
                // 状态行不播报成功（用户令 2026-09-20 第二轮："下面根本就不需要这个提示，删掉"）——
                // 开关自身的开/关位置 + 卡片里的说明句已经说清了；失败照报（catch 里那条）。
                Status = string.Empty;
            }
            catch (Exception ex)
            {
                LpLog.Error($"切换配色应用方式失败（auto={value}）", ex, LogCategory);
                Status = $"切换失败：{ex.Message}";
            }
            Raise(nameof(AutoAdjustColors));
            Raise(nameof(PaletteModeHint));
        }
    }

    /// <summary>开关的说明文案（两种模式各自说清"界面会怎么变"）。</summary>
    public string PaletteModeHint => AutoAdjustColors
        ? "已开启（缺省）：按明度档位自动重排你的配色"
        : "已关闭：尽量原样使用你给的颜色（只在某个角色确实缺色时才按本色补一档）";

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

    /// <summary>
    /// 选中的界面字体。**忽略 null 写入**（见下）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要忽略 null（用户报障 2026-09-20 第二轮："我选系统字体，此时根本就拉取不到任何字体"）</b>：
    /// 两个下拉的 <c>SelectedItem</c> 是**双向绑定**到这里的，而 `ItemsSource` 在候选装载时被整体替换 ——
    /// 替换的那一刻 WPF 会把 <c>SelectedItem</c> 变成 null 并**回写**给本属性，
    /// 于是"当前字体"被自己的空候选擦掉，两个框显示成**空白**（用户读成"拉取不到字体"）。
    /// </para>
    /// <para>
    /// 处置：**候选为空导致的清除不是用户的选择**，一律忽略；"当前该用哪个字体"只由
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
        }
    }

    /// <summary>选中的等宽字体（null 写入同样忽略，理由见 <see cref="SelectedUiFont"/>）。</summary>
    public FontOptionViewModel? SelectedMonoFont
    {
        get => _selectedMonoFont;
        set
        {
            if (value is null) return;
            _selectedMonoFont = value;
            Raise(nameof(SelectedMonoFont));
        }
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
            // ⚠️ 换主题之后**必须**刷新"当前外观"那一族投影（名字 / 起点按钮文案 / 说明句 / 槽数）：
            //    这一步原先漏了，用户看到的正是"换了主题，下面还写着以「默认（紫罗兰）」为起点"。
            ProjectAppliedTheme();
            // 色点也要按**当前配色应用方式**重投影（用户令 2026-09-20："当我们开关自动调整颜色的按钮时，
            // 主题那个色点也会同步修改"）——换主题同样属于"当前外观变了"，色点不能停在旧模式的口径上。
            RepojectAllThemeCards();
            ThemeService.SaveCurrentPreferences();
            // 状态行不播报"已应用主题「X」"（用户令 2026-09-20 第二轮："下面根本就不需要这个提示，删掉"）：
            // 当前生效的是哪套外观由**主题卡高亮 + 「当前使用」徽标**表达，再写一行文字是重复信息。
            // 失败仍然照报（下面 catch 里那两条），因为那是用户必须看见的。
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
    /// 「以当前外观为起点」：把**当前生效外观**的可编辑色槽**复制**进调色台并**立即应用**为自选配色。
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
    /// <b>点一下就该生效（用户报障 2026-09-20："当我们选择以什么为起点的时候，应该立即切换到自选颜色这一栏，
    /// 也就是主题应该立即更改"）</b>：旧行为只把颜色倒进草稿、把归属留在预设上，于是用户按了按钮却看到
    /// "什么都没发生"（主题卡还是预设那张、界面一点没变），还得再点一次那个全局的「应用」（该按钮已随
    /// 2026-09-20 第三轮删除 —— 自选配色的唯一应用入口 = 第 12 张「自选颜色」卡）。
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
        MarkDraftDirty();                 // 先标脏：ApplyDraft 的门槛/诊断按"草稿"口径走，成功后才清
        RebuildSlots();

        ApplyDraft();                     // 唯一应用入口（空槽门槛 + 校验 + 归属切换 + 落盘）
        // 成功不播报（用户令：删掉状态行提示）；失败时 ApplyDraft 已把原因写在状态行上，这里不许覆盖
        // ⚠️ 判据用 **IsCustomActive**（公开投影），不是 `_customActive` 字段——"生效了没"本来就该问投影。
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
            Diagnostics = EmptySlotsHint(missing);
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
            ProjectAppliedTheme();    // 名字 / 按钮文案 / 说明句 / 槽数一起跟上（换主题的公共出口）
            RepojectAllThemeCards();  // 色点按当前模式重投影（与 ApplyThemeCard 同一口径）
            ThemeService.SaveCurrentPreferences();
            // 状态行同样不播报成功（用户令："下面根本就不需要这个提示"）——只报失败
            Diagnostics = string.Empty;
            HasDiagnostics = false;
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
            Diagnostics = EmptySlotsHint(empty);
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
    private string EmptySlotsHint(int empty) =>
        $"· 还差 {empty} 个颜色：点色槽用取色盘选色（选够 {_draft.Count} 个就能点上面第 12 张「自选颜色」卡应用）";

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
    /// 把一个色槽清回**空槽**（用户令 2026-09-20：取色盘的「清除」= 把该槽设回空槽，**不是设成黑色**）。
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
    /// <b>清空 = 回到紫罗兰（用户令 2026-09-21："清空颜色的时候应该回到紫罗兰"）</b>：
    /// 旧行为只清草稿、**不动已应用的外观** —— 用户按了"清空"，界面上还是上一套配色
    /// （什么都没变，"清空"名不副实）。现行 = 一并回出厂默认主题：
    /// <see cref="ThemeService.ApplyDefault"/> + 落盘 + "当前外观"那一族投影（名字 / 起点按钮 /
    /// 说明句 / 槽数 / 主题卡高亮）。
    /// </para>
    /// <para>
    /// 与 <see cref="ResetToDefaultAsync"/> 的分工：那个是**整套外观恢复出厂**（主题 + 字体 +
    /// 清掉偏好文件）；这里只回主题、不碰字体、也不删偏好文件（回默认这件事本身要写进偏好）。
    /// </para>
    /// <para>
    /// 清完必须走 <see cref="RefreshDraftDiagnostics"/>（而不是把诊断一清了之）：
    /// 空槽态要如实显示"还差 N 个颜色"这条中性提示，否则面板上没有任何一处告诉用户还差几个。
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
            // 成功不播报（用户令：状态行只留失败）——主题卡高亮 + 「当前使用」徽标就是结果
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            LpLog.Error("清空颜色后回默认主题失败", ex, LogCategory);
            Status = $"已清空颜色，但回默认主题失败：{ex.Message}";
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
    /// 重新装载字体候选（导入 / 删除 / 恢复默认 / 切来源后调用）—— **后台枚举 + 投影**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>唯一装载路径</b>：候选集合只在 <see cref="ReloadFontsAsync"/> 里被重写，
    /// 导入/删除/恢复默认/切来源都汇到它 —— 不给自己留"顺手再拼一次列表"的第二条路。
    /// </para>
    /// <para>
    /// <b>按来源分池</b>（用户令 2026-09-20："将系统本身的字体和我们导入的字体区分开来，
    /// 我们先要选择系统本身的字体，然后还是自定义字体，然后只能二选一"）：
    /// 两个来源各自建集合，下拉只显示**当前来源**那一份 —— 用户在"系统已装"里绝不会
    /// 误删到应用自己的东西（系统字体根本删不掉），在"自定义"里能导入/删除。
    /// </para>
    /// </remarks>
    public async Task ReloadFontsAsync()
    {
        var currentUi = ThemeService.CurrentUiFont;
        var currentMono = ThemeService.CurrentMonoFont;

        var all = await FontCatalog.LoadAsync().ConfigureAwait(true);

        UiFonts.Clear();
        MonoFonts.Clear();
        SystemUiFonts.Clear();
        SystemMonoFonts.Clear();
        CustomUiFonts.Clear();
        CustomMonoFonts.Clear();
        foreach (var f in all)
        {
            var option = new FontOptionViewModel(f);
            // 系统池 / 自定义池（两个"字体用途"各存一份 VM，避免同一实例被两个下拉共用）
            var uiPool = option.IsImported ? CustomUiFonts : SystemUiFonts;
            var monoPool = option.IsImported ? CustomMonoFonts : SystemMonoFonts;
            uiPool.Add(new FontOptionViewModel(f));
            monoPool.Add(new FontOptionViewModel(f));
        }
        ProjectFontPools();

        ProjectCurrentFonts(currentUi, currentMono);
    }

    /// <summary>把"当前来源"那一份灌进两个下拉（切换来源 / 装载完成后调用）。</summary>
    private void ProjectFontPools()
    {
        ReplaceAll(UiFonts, FontSource == FontSourceKind.System ? SystemUiFonts : CustomUiFonts);
        ReplaceAll(MonoFonts, FontSource == FontSourceKind.System ? SystemMonoFonts : CustomMonoFonts);

        static void ReplaceAll(ObservableCollection<FontOptionViewModel> target,
            ObservableCollection<FontOptionViewModel> source)
        {
            target.Clear();
            foreach (var f in source) target.Add(f);
        }
    }

    /// <summary>
    /// 把"当前已应用字体"投影到选中项（找不到同名就落到默认字体）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>候选还没装载时也要显示当前字体（用户报障 2026-09-20 第二轮："我选系统字体，此时根本就拉取不到
    /// 任何字体"）</b>：字体候选是**惰性**的（进页面不枚举系统字体，用户真的展开下拉才装载），
    /// 而两个下拉的 `SelectedItem` 原先只在"候选里找得到同名项"时才被赋值 —— 于是装载之前两个框**空白**，
    /// 用户读成"拉不到任何字体"（它其实只是"还没去拉"）。
    /// </para>
    /// <para>
    /// 判据修正：**当前生效的字体必须始终显示在框里**，无论候选是否已装载。
    /// 候选里没有同名项（尚未装载 / 字体已卸载但偏好还留着）就用当前族名造一个**占位项**补上，
    /// 装载完成后 <see cref="ReloadFontsAsync"/> 会再投影一次，占位项自然被真候选替换。
    /// 注意占位项的 <see cref="FontOptionViewModel.Choice"/> 带 <c>FilePath = null</c>（= 系统字体），
    /// 因此它**不可删** —— 与"系统字体只读"同一条口径。
    /// </para>
    /// </remarks>
    private void ProjectCurrentFonts(string currentUi, string currentMono)
    {
        // 候选还没装载（池为空）时先补一个**占位项**：下拉框里必须始终看得见"现在用的是什么字体"，
        // 否则用户看到两个空白框，读成"拉取不到任何字体"（用户报障 2026-09-20 第二轮）。
        // ⚠️ 占位项要真的进集合：combo 的 `SelectedItem` 指向一个**不在 Items 里**的对象时
        //    WPF 会把它显示成空（实测组合框 `SelectedItem` 是占位项、界面却是空白）。
        EnsureActivePlaceholder(currentUi);
        EnsureActivePlaceholder(currentMono);

        SelectedUiFont = PickOrPlaceholder(UiFonts, currentUi, FontCatalog.DefaultUiFamily);
        SelectedMonoFont = PickOrPlaceholder(MonoFonts, currentMono, FontCatalog.DefaultMonoFamily);

        static FontOptionViewModel? PickOrPlaceholder(
            ObservableCollection<FontOptionViewModel> pool, string current, string fallbackFamily)
        {
            var family = string.IsNullOrWhiteSpace(current) ? fallbackFamily : current;
            return pool.FirstOrDefault(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase))
                   ?? pool.FirstOrDefault(f => string.Equals(f.Family, fallbackFamily, StringComparison.OrdinalIgnoreCase))
                   ?? pool.FirstOrDefault();
        }

        // 把当前生效的字体补进**当前来源**那份池（候选装载后同名项已存在 → 不重复添加；装载后不再被调用）
        void EnsureActivePlaceholder(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return;
            var target = FontSource == FontSourceKind.System ? SystemUiFonts : CustomUiFonts;
            var targetMono = FontSource == FontSourceKind.System ? SystemMonoFonts : CustomMonoFonts;
            if (target.All(f => !string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(new FontOptionViewModel(new FontChoice(family, family)));
                targetMono.Add(new FontOptionViewModel(new FontChoice(family, family)));
            }
            ReplaceAll(UiFonts, target);
            ReplaceAll(MonoFonts, targetMono);

            static void ReplaceAll(ObservableCollection<FontOptionViewModel> to,
                ObservableCollection<FontOptionViewModel> from)
            {
                to.Clear();
                foreach (var f in from) to.Add(f);
            }
        }
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
            // 导入的字体属于"自定义"这一侧：**切过去再选中它**（否则用户还在"系统"列表里看不到刚导入的东西）
            _fontSource = FontSourceKind.Custom;
            Raise(nameof(FontSource));
            Raise(nameof(FontSourceIndex));
            Raise(nameof(IsCustomFontSource));
            Raise(nameof(FontSourceHint));

            await ReloadFontsAsync().ConfigureAwait(true);
            SelectedUiFont = UiFonts.FirstOrDefault(f => string.Equals(f.Family, choice.Family, StringComparison.OrdinalIgnoreCase))
                             ?? SelectedUiFont;
            // 导入成功不播报（用户令：删掉状态行提示）——「自定义字体」侧的下拉出现该项就是结果；失败照报
            Status = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LpLog.Error($"导入字体失败：{path}", ex, LogCategory);
            Status = $"导入失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>删除一个已导入字体（**只删本应用目录里那份副本**，系统字体不可删也不该删）。</summary>
    /// <remarks>
    /// <b>删除后的三处收尾，一个都不能少</b>（用户令 2026-09-20："有没有潜在的 bug 修掉"）：
    /// ① 删文件（<see cref="FontCatalog.Remove"/>）；② 若删的正是**当前生效**的字体 →
    /// <see cref="ThemeService.ResetFontIfDeleted"/> 退回默认并落盘（否则偏好里留着一个不存在的族名，
    /// 重启弹"已不可用"、再点应用还会把死族名写回去）；③ 重载候选并刷新按钮可用性。
    /// </remarks>
    public async Task DeleteFontAsync(FontOptionViewModel option)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (!option.CanDelete)
        {
            Status = "系统字体不可删除（它属于系统；只有「自定义」里导入的字体才能删）";
            return;
        }
        try
        {
            var deleted = FontCatalog.Remove(option.Choice);
            var fellBack = ThemeService.ResetFontIfDeleted(option.Family);
            await ReloadFontsAsync().ConfigureAwait(true);
            // 成功不播报（用户令：删掉状态行提示）；但"删的正是当前生效字体 → 已回退默认"是**用户必须知道**的
            // 一件事实（他不是只删了一个候选，而是界面字体变了），故这一类照报。
            Status = fellBack is null
                ? string.Empty
                : $"「{option.Family}」正是当前生效的字体，已回退默认字体（文件{(deleted ? "已删除" : "本就不存在")}）";
        }
        catch (Exception ex)
        {
            // WPF 会把解析过的字体文件**内存映射持有到进程退出**（WARNINGS 75）——这是平台事实，
            // 不是"路径写错了"：必须把"下一步怎么办"告诉用户，而不是原样丢一个"访问被拒绝"。
            LpLog.Error($"删除导入字体失败：{option.Family}", ex, LogCategory);
            Status = ex is UnauthorizedAccessException
                ? $"删除失败：「{option.Family}」正被本进程占用（已加载的字体在退出前无法删除），重启应用后再删"
                : $"删除失败：{ex.Message}";
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
            // 成功不播报（用户令：删掉状态行提示）——界面本身已经换成新字体，那就是结果；失败照报
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            LpLog.Error("应用字体后保存偏好失败", ex, LogCategory);
            Status = $"字体已应用，但偏好保存失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 把界面/等宽字体一起**恢复默认族**（并落盘）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="ApplyFonts"/>（应用"当前选中"）是两件事：这里不读下拉的选中项，
    /// 直接走 <see cref="ThemeService.ApplyFonts(string?, string?, System.Windows.ResourceDictionary?)"/>
    /// 的**无参形态**（= 回退链第一段：`Microsoft YaHei UI` / `Consolas`）。
    /// </para>
    /// <para>
    /// 收尾三条与"删除当前生效字体"同口径：应用 → 落盘 → 候选重载 + 当前值重投影
    /// （候选没装载时用占位项投影，下拉里照样立刻显示默认族 —— 别赌"用户会先展开一次下拉"）。
    /// </para>
    /// </remarks>
    public async Task ResetFontsAsync()
    {
        try
        {
            ThemeService.ApplyFonts();                 // 无参 = 默认族（不读下拉选中项）
            ThemeService.SaveCurrentPreferences();
            if (_fontsLoaded) await ReloadFontsAsync().ConfigureAwait(true);
            else ProjectCurrentFonts(ThemeService.CurrentUiFont, ThemeService.CurrentMonoFont);
            // 成功不播报（用户令：删掉状态行提示）——下拉里换回默认族就是结果；失败照报
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            LpLog.Error("恢复默认字体失败", ex, LogCategory);
            Status = $"恢复默认字体失败：{ex.Message}";
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
            ClearDraftCore();                 // 调色台回**空态** + 清"未应用改动"（自选配色已随偏好一起清掉，
                                              // 留着色点会名不副实；默认外观 = 全新起点）。
                                              // ⚠️ 走**纯草稿**路径：公有的 ClearDraft 会回默认主题并落盘，
                                              //    在这里会把刚清掉的偏好文件又写回来（本方法的口径 = 偏好文件也要清）
            SyncFromAppliedTheme();           // 互斥归属 + 主题卡 + 草稿一起回默认（唯一投影点）
            if (_fontsLoaded) await ReloadFontsAsync().ConfigureAwait(true);
            Diagnostics = string.Empty;
            HasDiagnostics = false;
            // 成功不播报（用户令：删掉状态行提示）——界面回到默认就是结果；失败照报（catch 里那条）
            Status = string.Empty;
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
