using LinkPocket.Theming.Color;
using LinkPocket.Theming.Tokens;
using Material3.Core;

namespace LinkPocket.Theming.Themes;

/// <summary>主题的**分族结果**（§5.3 第 2–5 步的产物）：三个族各自的色相与彩度。</summary>
/// <param name="AccentHue">强调族色相（填充 / 图标 / 强调文字 / 强调容器共用）。</param>
/// <param name="AccentChroma">强调族彩度（已按彩度上限档钳制）。</param>
/// <param name="AccentTone">强调族最暗成员的明度（仅诊断用）。</param>
/// <param name="SupportHue">支撑族色相（次强调容器 / 计数药丸 / 删除类药丸 / 文件夹类型色）。</param>
/// <param name="SupportChroma">支撑族彩度（已钳制）。</param>
/// <param name="SupportIsDerived">true = 没有第二个族，支撑族由强调色相 ±60° 派生。</param>
/// <param name="NeutralHue">中性色相：决定表面旋转角与文字三档的染色方向。</param>
/// <param name="NeutralVariantHue">中性**变体**色相：决定描边（主/弱）的染色方向。</param>
public sealed record ThemeFamilies(
    double AccentHue,
    double AccentChroma,
    double AccentTone,
    double SupportHue,
    double SupportChroma,
    bool SupportIsDerived,
    double NeutralHue,
    double NeutralVariantHue);

/// <summary>
/// 派生管线的**纯函数核心**（方案 §5.3 的七步）：<c>ThemeDefinition → ThemeFamilies</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>输入是多个颜色而不是一个种子</b>，所以先**分族**（色相 30° 内聚族）再定角色：
/// 强调族（最暗者优先）→ 支撑族（第二族，或 +60° 派生且回避黄区）→ 中性族（低彩度成员，或取强调族色相）。
/// </para>
/// <para>
/// <b>自研纪律</b>：只借鉴 Material You 的**颜色科学原理**（HCT 空间、色调板档位、大面积低彩度、
/// 状态层叠层），不套用它的标准档位表与 <c>SchemeVariant</c>。彩度**只钳上限、绝不放大**
/// （尊重用户配色的彩度性格，见方案 §4.6）。
/// </para>
/// <para>
/// <b>为什么中性色相要能钉住</b>：出厂默认的 5 个身份色里最浅的一个（<c>#F2EEF5</c>，C5.4）本身
/// 就是低彩度成员，会进中性池 → 派生出的中性色相是 287.7°，比今天背景的 298.7° 偏 −11°，
/// 表面族会跟着漂。定稿要求"背景逐字节不变"，所以在**数据层**（而不是代码分支里）允许主题钉住中性色相。
/// </para>
/// </remarks>
public static class PaletteSolver
{
    /// <summary>彩度低于此值的颜色进中性池（不参与分族）。</summary>
    public const double NeutralPoolChromaThreshold = 6.0;

    /// <summary>同族判据：与族色的色相差在此值以内。</summary>
    public const double FamilyHueTolerance = 30.0;

    /// <summary>黄区（45°–105°）：支撑族派生落在这里时改用 −60°（回避已废弃的黄色语义，黄在浅底上辨识度也最差）。</summary>
    public const double YellowZoneStart = 45.0;

    /// <summary>黄区上界。</summary>
    public const double YellowZoneEnd = 105.0;

    /// <summary>彩度上限档 → (强调上限, 支撑上限)（自研「只钳上限」；单色档两族同钳）。</summary>
    public static (double Accent, double Support) ChromaCaps(ChromaCap cap) => cap switch
    {
        ChromaCap.Restrained => (24.0, 16.0),
        ChromaCap.Monochrome => (8.0, 8.0),
        _ => (36.0, 24.0),
    };

    /// <summary>派生出主题的三族（纯函数、无副作用、可单测）。</summary>
    public static ThemeFamilies SolveFamilies(ThemeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var caps = ChromaCaps(definition.ChromaCap);

        var measured = definition.Palette
            .Select(c => (Color: c, Hct: ColorMath.Measure(c)))
            .ToList();

        // 第 2 步：分族（彩度 ≥6 按色相 30° 内贪心聚族；<6 进中性池）
        var chromatics = measured
            .Where(m => m.Hct.C >= NeutralPoolChromaThreshold)
            .OrderByDescending(m => m.Hct.C)
            .ToList();
        var neutralPool = measured
            .Where(m => m.Hct.C < NeutralPoolChromaThreshold)
            .ToList();

        var families = ClusterFamilies(chromatics);

        // 第 3 步：强调族 = 族内最暗者优先（并列则彩度高者）——"用户放的最深那个色就是主色"
        ThemeFamily? accent = null;
        foreach (var f in families)
        {
            if (accent is null
                || f.MinTone < accent.MinTone
                || (Math.Abs(f.MinTone - accent.MinTone) < 0.5 && f.Chroma > accent.Chroma))
                accent = f;
        }

        // 第 4 步：支撑族 = 剩余族中同样规则取一个；没有第二个族时 +60° 派生（黄区改 −60°）
        ThemeFamily? support = null;
        foreach (var f in families)
        {
            if (ReferenceEquals(f, accent)) continue;
            if (support is null
                || f.MinTone < support.MinTone
                || (Math.Abs(f.MinTone - support.MinTone) < 0.5 && f.Chroma > support.Chroma))
                support = f;
        }

        var accentHue = accent?.Hue ?? FallbackHue(measured);
        var accentChroma = Math.Min(accent?.Chroma ?? 0.0, caps.Accent);
        var accentTone = accent?.MinTone ?? 40.0;

        double supportHue, supportChroma;
        var supportDerived = support is null;
        if (support is null)
        {
            supportHue = DeriveSupportHue(accentHue);
            // M3 secondary 惯例：没有第二族时，次强调是主色的**一半厚度**
            supportChroma = Math.Min(accentChroma * 0.5, caps.Support);
        }
        else
        {
            supportHue = support.Hue;
            supportChroma = Math.Min(support.Chroma, caps.Support);
        }

        // 中性色相优先级：主题钉值 > 中性池圆均值 > 强调族色相。
        // 中性**变体**色相不取钉值——描边不是"表面"，实测（附录 A 默认主题）描边走中性池的 287.7°，
        // 而表面走钉住的 298.7°：两者刻意分开，混用会让描边偏紫（实测差 #A19CA6 vs #9F9CA7）。
        var poolHue = neutralPool.Count > 0
            ? ColorMath.CircularMeanHue(neutralPool.Select(m => (m.Hct.H, m.Hct.C)).ToList())
            : accentHue;
        var neutralHue = definition.NeutralHueOverride ?? poolHue;

        return new ThemeFamilies(
            ColorMath.NormalizeHue(accentHue), accentChroma, accentTone,
            ColorMath.NormalizeHue(supportHue), supportChroma, supportDerived,
            ColorMath.NormalizeHue(neutralHue), ColorMath.NormalizeHue(poolHue));
    }

    /// <summary>
    /// 支撑族派生色相：强调色相 +60°；落在黄区（45°–105°）时改用 −60°。
    /// </summary>
    public static double DeriveSupportHue(double accentHue)
    {
        var plus = ColorMath.NormalizeHue(accentHue + 60.0);
        var inYellow = plus >= YellowZoneStart && plus <= YellowZoneEnd;
        return inYellow ? ColorMath.NormalizeHue(accentHue - 60.0) : plus;
    }

    /// <summary>中性族彩度（大面积必须"安静"；方案 §4.5）。</summary>
    public const double NeutralChroma = 4.0;

    /// <summary>中性变体彩度（描边档）。</summary>
    public const double NeutralVariantChroma = 8.0;

    /// <summary>
    /// **完整派生**（方案 §5.3 七步）：<c>ThemeDefinition → TokenTable</c>。纯函数、无副作用、可单测。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 步骤 6（落明度档位）与步骤 7（全量发布）在这里合流。关键设计：<b>语义令牌是唯一真值，
    /// 库键从语义令牌映射</b>（不是两边各算一遍）——"同一个语义只有一个真值"因此是结构性成立的，不靠约定。
    /// 表面族反过来：**逐键取自锚定表**（层感恒定的来源），再绑到语义令牌上。
    /// </para>
    /// <para>
    /// 只有 <c>Secondary*</c> 没法这样做（方案把库 <c>Secondary</c> 定义为支撑族，
    /// 而本应用不消费它 → 没有对应应用令牌）；它不是精确不动点，但<b>不变量成立</b>：
    /// <c>rot = 0</c> 且派生值等于锚点的主题下逐字节相同。
    /// </para>
    /// </remarks>
    public static TokenTable Solve(ThemeDefinition definition)
    {
        var families = SolveFamilies(definition);
        var rot = definition.SurfaceRotation(families.NeutralHue);

        // ── 第 6 步：落明度档位（HCT → 色；α 显式携带，禁止用 Hct 的 α）──────
        Argb At(double hue, double chroma, double tone, byte alpha = 0xFF) =>
            ColorMath.FromAlphaHct(alpha, ColorMath.NormalizeHue(hue), chroma, tone);

        var textPrimary = At(families.NeutralHue, NeutralChroma, ToneScale.TextPrimary);
        var textSecondary = At(families.NeutralHue, NeutralChroma, ToneScale.TextSecondary);
        var textMuted = At(families.NeutralHue, NeutralChroma, ToneScale.TextMuted);
        var outline = At(families.NeutralVariantHue, NeutralVariantChroma, ToneScale.LineOutline);
        var outlineVariant = At(families.NeutralVariantHue, NeutralVariantChroma, ToneScale.LineVariant);

        var accentFill = At(families.AccentHue, families.AccentChroma, ToneScale.AccentFill);
        var accentText = At(families.AccentHue, families.AccentChroma, ToneScale.AccentText);
        var accentContainer = At(families.AccentHue, families.AccentChroma, ToneScale.AccentContainer);
        var accentOnContainer = At(families.AccentHue, families.AccentChroma, ToneScale.AccentOnContainer);

        var supportContainer = At(families.SupportHue, families.SupportChroma, ToneScale.SupportContainer);
        var supportOnContainer = At(families.SupportHue, families.SupportChroma, ToneScale.SupportOnContainer);
        var supportIcon = At(families.SupportHue, families.SupportChroma, ToneScale.AccentFill);

        // ── 第 7 步：全量发布 ──────────────────────────────────────────────
        var anchored = new Dictionary<string, Argb>(StringComparer.Ordinal);
        foreach (var anchor in SurfaceAnchors.All)
            anchored[anchor.Key] = Resolve(anchor, rot);

        var white = Argb.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        // 过渡期需要"今天的 OnSurface"（覆写前的锚定值）——必须在下面覆写**之前**取。
        var anchoredOnSurface = anchored["OnSurface"];

        // 覆写：语义族是唯一真值（文字带主题墨韵；默认主题 rot=0 时 = 锚点，逐字节相等）
        anchored["OnSurface"] = textPrimary;
        anchored["OnSurfaceVariant"] = textSecondary;
        anchored["OnSurfaceMuted"] = textMuted;
        anchored["Outline"] = outline;
        anchored["OutlineVariant"] = outlineVariant;

        anchored["Primary"] = accentFill;
        anchored["SurfaceTint"] = accentFill;
        anchored["OnPrimary"] = white;
        anchored["PrimaryContainer"] = accentContainer;
        anchored["OnPrimaryContainer"] = accentOnContainer;
        anchored["Secondary"] = At(families.SupportHue, families.SupportChroma, ToneScale.AccentFill);
        anchored["OnSecondary"] = white;
        anchored["SecondaryContainer"] = supportContainer;
        anchored["OnSecondaryContainer"] = supportOnContainer;

        // 语义令牌：表面族绑到锚定值，其余绑到上面的唯一真值
        var tokens = new Dictionary<string, Argb>(StringComparer.Ordinal)
        {
            [AppTokens.SurfaceBase] = anchored["SurfaceContainerLow"],
            [AppTokens.SurfaceCard] = anchored["SurfaceContainerHigh"],
            [AppTokens.SurfaceHover] = anchored["SurfaceContainerHighest"],
            [AppTokens.SurfaceTintCard] = anchored["TintCard"],
            [AppTokens.SurfaceTint] = anchored["TintSurface"],
            [AppTokens.SurfacePanel] = anchored["TintPanel"],
            [AppTokens.SurfaceDialog] = anchored["TintBg"],
            [AppTokens.SurfaceFloating] = anchored["Surface"],

            [AppTokens.TextPrimary] = textPrimary,
            [AppTokens.TextSecondary] = textSecondary,
            [AppTokens.TextMuted] = textMuted,
            [AppTokens.TextOnAccent] = white,
            // 容器字 = 支撑族 T15。界面上这个令牌落在**两种**容器上（强调容器 / 次强调容器），
            // 而两族的容器字本来就是同一档（T15）、对各自容器的对比度都 ≥11.7 —— 故只有一个真值
            // （`App.*.OnContainer` 这一族已删除：它们与文字族同值，留着就是"同一语义两个键"）。
            [AppTokens.TextOnContainer] = supportOnContainer,

            [AppTokens.AccentFill] = accentFill,
            [AppTokens.AccentIcon] = accentFill,
            [AppTokens.AccentText] = accentText,
            [AppTokens.AccentContainer] = accentContainer,

            [AppTokens.SupportContainer] = supportContainer,
            [AppTokens.SupportIcon] = supportIcon,

            [AppTokens.TypeFolder] = supportIcon,
            [AppTokens.TypeLink] = accentFill,

            [AppTokens.LineOutline] = outline,
            [AppTokens.LineVariant] = outlineVariant,
            [AppTokens.LineInvalid] = textPrimary,
        };

        // 状态层 = 叠层（不是另找一个颜色）。方向规则：底色 tone 低（强调填充这类深底）时叠白，
        // 否则叠 OnSurface——否则深底上的悬停会被"更深的叠层"吃掉，反馈反而变弱。
        var accentIsDeep = ColorMath.Measure(accentFill).T <= ToneScale.AccentFill;
        tokens[AppTokens.StateHover] = ColorMath.Overlay(
            accentIsDeep ? accentFill : anchored["SurfaceContainerHigh"],
            accentIsDeep ? white : textPrimary,
            ToneScale.StateHoverOpacity);
        tokens[AppTokens.StatePressed] = ColorMath.Overlay(
            anchored["SurfaceContainerHigh"], textPrimary, ToneScale.StatePressedOpacity);
        tokens[AppTokens.StateDisabledFill] = ColorMath.Overlay(
            anchored["SurfaceContainerHigh"], textPrimary, ToneScale.DisabledFillOpacity);
        tokens[AppTokens.StateDisabledContent] = ColorMath.Overlay(
            anchored["SurfaceContainerHigh"], textPrimary, ToneScale.DisabledContentOpacity);

        // 标题栏/无底按钮的中性墨状态层（历史值即"中性黑 10% / 20% 叠在任意底上"）
        tokens[AppTokens.StateTitleBarHover] = ColorMath.Overlay(
            anchored["SurfaceContainerHigh"], textPrimary, ToneScale.TitleBarHoverOpacity);
        tokens[AppTokens.StateTitleBarPressed] = ColorMath.Overlay(
            anchored["SurfaceContainerHigh"], textPrimary, ToneScale.TitleBarPressedOpacity);

        // 遮罩 / 阴影：按"常量 + α"口径（方案 §5.4 例外 ②，Scrim/Shadow 无彩度、不参与旋转）
        var black = Argb.FromInt(unchecked((int)0xFF000000));
        tokens[AppTokens.OverlayScrim] = ColorMath.Overlay(anchored["SurfaceContainerLow"], black, ToneScale.ScrimOpacity);
        tokens[AppTokens.OverlayBusy] = ColorMath.Overlay(
            anchored["SurfaceContainerLow"], anchored["SurfaceContainerLow"], ToneScale.BusyOverlayOpacity);
        tokens[AppTokens.OverlayShadow] = ColorMath.Overlay(black, black, ToneScale.ShadowOpacity);

        // 颜色型令牌（值是 Color 而非 Brush；与同名 Brush 令牌**同源**，只是介质不同）
        var shadowRgb = ColorMath.Unpack(tokens[AppTokens.OverlayShadow]);
        tokens[AppTokens.ShadowColor] = ColorMath.Pack(0xFF, shadowRgb.R, shadowRgb.G, shadowRgb.B);
        tokens[AppTokens.GradientStart] = ColorMath.WithAlpha(tokens[AppTokens.SurfaceBase], 0x00);
        var accentRgb = ColorMath.Unpack(accentFill);
        tokens[AppTokens.GradientEnd] = ColorMath.Pack(ToneScale.CardPanelGradientAlpha, accentRgb.R, accentRgb.G, accentRgb.B);
        tokens[AppTokens.SvTransparent] = ColorMath.Pack(0x00, 0x00, 0x00, 0x00);

        return new TokenTable
        {
            Anchored = anchored,
            Tokens = tokens,
            SurfaceRotation = rot,
            Families = families,
        };
    }

    /// <summary>一个锚点在该旋转角下的取值（α 取自锚点；不旋转键保持库基线）。</summary>
    private static Argb Resolve(SurfaceAnchors.Anchor anchor, double rotation)
    {
        if (!anchor.Rotates) return anchor.Today;
        var c = ColorMath.Unpack(anchor.Today);
        var hct = ColorMath.Measure(anchor.Today);
        return ColorMath.FromAlphaHct(c.A, ColorMath.RotateHue(hct.H, rotation), hct.C, hct.T);
    }

    /// <summary>无法选出强调族时的兜底色相（= 今天背景色相；正常配色不会走到，只保证纯函数总有值）。</summary>
    private static double FallbackHue(IReadOnlyList<(Argb Color, ColorMath.Hct3 Hct)> measured) =>
        measured.Count > 0 ? measured[0].Hct.H : ThemeDefinition.ReferenceNeutralHue;

    /// <summary>
    /// 按彩度降序贪心聚族：与族色相在 <see cref="FamilyHueTolerance"/> 内即并入。
    /// </summary>
    /// <remarks>
    /// <b>单链聚族 + 跨度闸</b>：只用"与族色相距离"判定时，A≈30°、B≈60° 的两个色会经"平均色相 45°"
    /// 被接成一条链（A、B 各自都在 30° 内，但彼此差 30°），把两个本不相干的群并成一个、
    /// 并使色相均值滑到中间。故并入后还要复核**成员两两跨度**（≤ 2×容差），超了就另起一族——
    /// 判据落在"这一族到底包含什么"，而不是"能不能接上"。
    /// </remarks>
    private static List<ThemeFamily> ClusterFamilies(
        IReadOnlyList<(Argb Color, ColorMath.Hct3 Hct)> orderedByChromaDesc)
    {
        var families = new List<ThemeFamily>();
        foreach (var (_, hct) in orderedByChromaDesc)
        {
            var target = families.FirstOrDefault(f =>
                ColorMath.HueDistance(f.Hue, hct.H) <= FamilyHueTolerance && f.CanAccept(hct.H));
            if (target is null)
                families.Add(new ThemeFamily(hct.H, hct.C, hct.T));
            else
                target.Add(hct.H, hct.C, hct.T);
        }
        return families;
    }

    /// <summary>一族允许的最大成员跨度（度）：两倍容差。</summary>
    private const double FamilyMaxSpread = FamilyHueTolerance * 2.0;

    /// <summary>一个色族：色相 = 彩度加权圆均值、彩度 = 成员最大彩度、最暗明度 = 成员最小 tone。</summary>
    private sealed class ThemeFamily
    {
        private readonly List<(double Hue, double Weight)> _members = new();

        public ThemeFamily(double hue, double chroma, double tone)
        {
            Hue = ColorMath.NormalizeHue(hue);
            Chroma = chroma;
            MinTone = tone;
            _members.Add((hue, chroma));
        }

        public double Hue { get; private set; }

        public double Chroma { get; private set; }

        public double MinTone { get; private set; }

        /// <summary>跨度闸：并入 <paramref name="hue"/> 后，成员两两色相差仍须 ≤ <see cref="FamilyMaxSpread"/>。</summary>
        public bool CanAccept(double hue)
        {
            foreach (var (memberHue, _) in _members)
                if (ColorMath.HueDistance(memberHue, hue) > FamilyMaxSpread)
                    return false;
            return true;
        }

        public void Add(double hue, double chroma, double tone)
        {
            _members.Add((hue, chroma));
            Hue = ColorMath.CircularMeanHue(_members);
            Chroma = Math.Max(Chroma, chroma);
            MinTone = Math.Min(MinTone, tone);
        }
    }
}
