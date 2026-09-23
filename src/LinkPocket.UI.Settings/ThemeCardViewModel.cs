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
