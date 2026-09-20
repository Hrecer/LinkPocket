using LinkPocket.Theming.Color;
using LinkPocket.Theming.Tokens;
using Material3.Core;

namespace LinkPocket.Theming.Themes;

/// <summary>主题的**角色分配结果**（配色成员 → 固定角色槽）：各槽的色相与彩度。</summary>
/// <param name="AccentHue">强调槽色相（主药丸填充 / 类型图标 / 强调文字）。</param>
/// <param name="AccentChroma">强调槽彩度（已按彩度上限档钳制）。</param>
/// <param name="SupportHue">支撑槽色相（次强调容器 / 计数药丸 / 删除类药丸 / 文件夹类型色）。</param>
/// <param name="SupportChroma">支撑槽彩度（已钳制）。</param>
/// <param name="SupportIsDerived">true = 配色里没有第二个成员，支撑槽由强调色相派生（同色相、彩度减半）。</param>
/// <param name="ContainerHue">强调容器槽色相（选中指示器 / 落点高亮 / 徽标底）。</param>
/// <param name="ContainerChroma">强调容器槽彩度。</param>
/// <param name="NeutralHue">表面族色相（= 配色里**最浅**的成员）：决定表面旋转角与文字三档的染色方向。</param>
/// <param name="NeutralVariantHue">描边槽色相：决定 `Line.Outline` / `Line.Variant` 的染色方向。</param>
public sealed record ThemeFamilies(
    double AccentHue,
    double AccentChroma,
    double SupportHue,
    double SupportChroma,
    bool SupportIsDerived,
    double ContainerHue,
    double ContainerChroma,
    double NeutralHue,
    double NeutralVariantHue);

/// <summary>
/// 派生管线的**纯函数核心**：<c>ThemeDefinition → ThemeFamilies</c>（配色成员 → 角色槽）。
/// </summary>
/// <remarks>
/// <para>
/// <b>用户令（2026-09-20）："我们给出的 4/5 个颜色是最高优先级"</b>——旧模型按色相聚族、单族时用 ±60°
/// 旋转**发明**一个支撑色相，且只取「色相圆均值 + 最大彩度」，于是配色成员的明度被整体丢弃
/// （实测默认主题 5 色里 3 个对界面零影响）、造出来的色相又落进肤色带/冷色带
/// （用户报障"偏黄偏肤色""怎么又变偏蓝了"）。见 `文档/WARNINGS.md` 76/77。
/// </para>
/// <para>
/// 现行规则 = **彩度降序占槽**（并列取更暗者先）+ **明度最高者管表面**：
/// ① 强调族 ← 彩度最高的成员；② 支撑族 ← 次高；③ 强调容器 ← 第三；④ 描边 ← 第四；
/// ⑤ 表面族与文字墨 ← **明度最高**的成员（含低彩度色）。
/// 每个槽只沿**自己那份色相与彩度**做明度档位化（档位表在 <see cref="ToneScale"/>；HCT 的档位→对比度
/// 与色相无关，所以"随便选 4/5 个颜色都可读"这条性质不受影响）。**界面上的色相全部来自用户给的颜色**。
/// </para>
/// <para>
/// <b>缺位回退</b>（配色只有 1–3 个颜色时走这条，内置预设即如此）：支撑缺 → 强调同色相、彩度 ×0.5
/// （M3 secondary "一半厚度"的惯例）；强调容器缺 → 强调色相；描边缺 → 强调色相。
/// </para>
/// <para>
/// <b><see cref="ThemeDefinition.NeutralHueOverride"/></b> 只对**表面族色相**生效（内置预设用它钉住
/// 各自标定过的背景色相）；它不参与任何槽位的选取。
/// </para>
/// </remarks>
public static class PaletteSolver
{
    /// <summary>彩度上限档 → (强调上限, 支撑上限)（自研「只钳上限」；单色档两族同钳）。</summary>
    public static (double Accent, double Support) ChromaCaps(ChromaCap cap) => cap switch
    {
        ChromaCap.Restrained => (24.0, 16.0),
        ChromaCap.Monochrome => (8.0, 8.0),
        _ => (36.0, 24.0),
    };

    /// <summary>把配色成员分配到固定角色槽（纯函数、无副作用、可单测）。</summary>
    public static ThemeFamilies SolveFamilies(ThemeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var caps = ChromaCaps(definition.ChromaCap);

        var measured = definition.Palette
            .Select(c => (Color: c, Hct: ColorMath.Measure(c)))
            .ToList();

        // ①–④ 占槽顺序：彩度降序（并列取更暗者先）——"配色里最鲜艳的那个就是主色"
        var byChroma = measured
            .OrderByDescending(m => m.Hct.C)
            .ThenBy(m => m.Hct.T)
            .ToList();

        // ⑤ 表面族来源：明度最高者——"配色里最浅的那个决定背景"
        var surface = measured.OrderByDescending(m => m.Hct.T).First();

        var accent = Slot(byChroma, 0);
        var support = Slot(byChroma, 1);
        var container = Slot(byChroma, 2);
        var outline = Slot(byChroma, 3);

        var accentHue = accent?.Hct.H ?? surface.Hct.H;
        var accentChroma = Math.Min(accent?.Hct.C ?? 0.0, caps.Accent);

        var supportDerived = support is null;
        var supportHue = support?.Hct.H ?? accentHue;
        var supportChroma = support is null
            ? Math.Min(accentChroma * 0.5, caps.Support)            // 只有一个颜色：次强调 = 主色的"一半厚度"
            : Math.Min(support.Value.Hct.C, caps.Support);

        var containerHue = container?.Hct.H ?? accentHue;
        var containerChroma = Math.Min(container?.Hct.C ?? accentChroma, caps.Accent);

        var outlineHue = outline?.Hct.H ?? accentHue;

        return new ThemeFamilies(
            ColorMath.NormalizeHue(accentHue), accentChroma,
            ColorMath.NormalizeHue(supportHue), supportChroma, supportDerived,
            ColorMath.NormalizeHue(containerHue), containerChroma,
            ColorMath.NormalizeHue(definition.NeutralHueOverride ?? surface.Hct.H),
            ColorMath.NormalizeHue(outlineHue));
    }

    /// <summary>按彩度序取第 <paramref name="index"/> 个成员（越界 = 该槽缺位 → 调用方走回退）。</summary>
    private static (Argb Color, ColorMath.Hct3 Hct)? Slot(
        IReadOnlyList<(Argb Color, ColorMath.Hct3 Hct)> byChroma, int index) =>
        index < byChroma.Count ? byChroma[index] : null;

    /// <summary>
    /// 主题的**可编辑色槽**（4 或 5 个）：身份色已够时原样返回；不足（内置预设只有 1–3 个）时
    /// 用**该主题自己的档位**补足 —— 同色相 + 同彩度的明度档，补的是主题色本身，不是编造的装饰色。
    /// </summary>
    /// <remarks>
    /// <b>唯一实现</b>：外观面板的色槽（可微调）与主题卡的色点（展示）都走它 ——
    /// 两处各写一份补位规则，迟早会漂移成"卡片显示 4 个点、色槽却有 3 格"。
    /// </remarks>
    public static IReadOnlyList<Argb> EditableSlots(ThemeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var list = definition.Palette.ToList();
        if (list.Count >= ThemeDefinition.AllowedPaletteSizes[0])
            return list.Count <= 5 ? list : list.Take(5).ToList();

        var families = SolveFamilies(definition);
        foreach (var tone in new[]
                 {
                     ToneScale.AccentContainer, ToneScale.AccentFill, ToneScale.AccentText,
                     ToneScale.AccentOnContainer, 70.0,
                 })
        {
            if (list.Count >= 4) break;
            var candidate = ColorMath.FromAlphaHct(0xFF, families.AccentHue, families.AccentChroma, tone);
            if (list.All(c => c != candidate)) list.Add(candidate);
        }
        return list;
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
        // 强调容器走**它自己的槽**（配色里第三个成员）——"每个颜色都有出口"是用户令（2026-09-20）
        var accentContainer = At(families.ContainerHue, families.ContainerChroma, ToneScale.AccentContainer);
        var accentOnContainer = At(families.ContainerHue, families.ContainerChroma, ToneScale.AccentOnContainer);

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
}
