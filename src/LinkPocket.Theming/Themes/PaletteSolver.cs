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
    double NeutralVariantHue)
{
    /// <summary>强调槽直接取自配色的原色（<c>null</c> = 该槽没有对应成员，走档位生成）。</summary>
    public Argb? AccentSource { get; init; }

    /// <summary>支撑槽直接取自配色的原色。</summary>
    public Argb? SupportSource { get; init; }

    /// <summary>强调容器槽直接取自配色的原色（配色里第一个"足够浅"的成员）。</summary>
    public Argb? ContainerSource { get; init; }

    /// <summary>表面族来源 = 配色里最浅的成员（**背景就是它本身**，因此色点与背景同色 = 融合）。</summary>
    public Argb? SurfaceSource { get; init; }

    /// <summary>描边槽直接取自配色的原色（最接近"中间调"的那一个）。</summary>
    public Argb? OutlineSource { get; init; }
}

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
    /// <remarks>
    /// <para>
    /// <b>调色板优先（用户令 2026-09-20）</b>：用户原话"我说过优先应用我们选中的这 5 个颜色的，
    /// 而不是深一点浅一点" —— 交付界面的颜色**必须是用户给的那几个颜色本身**，不是"取色相 + 彩度、
    /// 按档位表重新生成"的近似色。旧实现只用 (H, C) 重建，于是宇治抹茶的 4 个青绿在界面上变成了
    /// 灰绿 + 粉紫（`#EEDDF7` 那种配色里根本不存在的颜色），用户读成"偏粉"。
    /// </para>
    /// <para>
    /// 各槽因此记录**来源原色**（<see cref="ThemeFamilies.AccentSource"/> 等），由 <see cref="Solve"/>
    /// 优先直接采用；只有在"配色里确实没有这个角色可用的成员"（例如没有任何浅色可作容器、或强调色太浅
    /// 撑不住白字）时才回退到档位生成 —— 回退是**保可读性的兜底**，不是默认路径。
    /// </para>
    /// </remarks>
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

        // ⑤ 表面族来源：明度最高者——"配色里最浅的那个决定背景"（**背景就是它本身**，所以色点与背景融合）
        var surface = measured.OrderByDescending(m => m.Hct.T).First();

        // 强调容器要"浅且安静"：优先取**浅的低彩度成员**（配色里的浅中性色，做选中底/徽标底最稳），
        // 其次取任何够浅的成员；都没有（4 色全是高彩度浅色）→ 用强调容器槽的色相提亮生成。
        // ⚠️ 不能直接拿"够浅的成员"里最浅那个：那往往就是背景色本身（页面底），选中底与页面底同色 = 看不出来。
        var containerSource = measured
            .Where(m => m.Hct.T >= ContainerSourceMinTone && m.Hct.C <= ContainerNeutralChroma)
            .OrderByDescending(m => m.Hct.T)
            .FirstOrDefault();
        if (containerSource.Color == default)
            containerSource = measured
                .Where(m => m.Hct.T >= ContainerSourceMinTone)
                .OrderByDescending(m => m.Hct.T)
                .FirstOrDefault();

        // 描边要"中间调"：取离线档最近的那个成员（>线档的优先，避免和正文一样深）
        var outlineSource = measured
            .OrderBy(m => Math.Abs(m.Hct.T - ToneScale.LineOutline) + (m.Hct.T < ToneScale.LineOutline ? 100 : 0))
            .First();

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
            ColorMath.NormalizeHue(outlineHue))
        {
            AccentSource = accent?.Color,
            SupportSource = support?.Color,
            ContainerSource = containerSource.Color == default ? null : containerSource.Color,
            SurfaceSource = surface.Color,
            OutlineSource = outlineSource.Color == default ? null : outlineSource.Color,
        };
    }

    /// <summary>按彩度序取第 <paramref name="index"/> 个成员（越界 = 该槽缺位 → 调用方走回退）。</summary>
    private static (Argb Color, ColorMath.Hct3 Hct)? Slot(
        IReadOnlyList<(Argb Color, ColorMath.Hct3 Hct)> byChroma, int index) =>
        index < byChroma.Count ? byChroma[index] : null;

    /// <summary>
    /// 该主题**实际生效的页面底色**（<c>App.Surface.Base</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// = 配色里最浅成员**本色的色相与彩度**，明度**夹在 <see cref="SurfaceBaseToneMin"/>–<see cref="SurfaceBaseToneMax"/>
    /// 之间**（落档公式见 <see cref="SurfaceBaseOf(ThemeFamilies)"/>）。因此它一般不等于那个成员的原色 ——
    /// 但**同色相、同彩度**，主题卡上那枚色点显示的仍是这套配色的颜色，且与页面底**同色 = 融合**。
    /// </para>
    /// <para>
    /// 主题卡上的色点用**这个值**而不是原始成员值来渲染最浅那一枚（见 <c>ThemeCardViewModel</c>）：
    /// 卡面显示的必须是"这套主题实际长什么样"，否则那个圆点会与它自己的底不同色（用户令 2026-09-20：
    /// "背景色那个圆与背景融合，这正是我们想要的效果"）。
    /// </para>
    /// </remarks>
    public static Argb SurfaceBaseColor(ThemeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return SurfaceBaseOf(SolveFamilies(definition));
    }

    /// <summary>
    /// 页面底 = 配色"背景色成员"**本色**落到 <see cref="SurfaceBaseToneMin"/>–<see cref="SurfaceBaseToneMax"/> 档内。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要夹档（2026-09-20 用户报障"整个主题界面底色都被改，颜色发灰，虽然是融合了"）</b>：
    /// 原样采用"最浅成员"会让页面底漂到很浅的档位（默认主题 `#F2EEF5` T94.6，而定稿底色是 T91.3），
    /// 层感随之塌掉：卡面只能提亮 1–2 档、悬停底压深后**反而比页面底更浅**（实·晴王青提饮 T97.9 /
    /// 薄荷气泡水 T98.1 的卡面与页面底对比度 = **1.003 / 1.000**，卡片根本看不出是卡片）。
    /// 夹到 87–91 之后：页面底有深度、卡面必然浮起来、选中底也腾得出位置。
    /// </para>
    /// <para>
    /// <b>不发明颜色</b>：只动明度，色相与彩度仍是那个成员本身的（用户令 2026-09-20：
    /// "优先应用我们选中的这几个颜色，而不是深一点浅一点"）—— 界面上的色相依然全部来自配色。
    /// </para>
    /// </remarks>
    private static Argb SurfaceBaseOf(ThemeFamilies families)
    {
        // 表面色相：预设/默认由"明度最高的成员"给出（或主题自己的钉值）
        var source = families.SurfaceSource;
        var hue = source is { } s ? ColorMath.Measure(s).H : families.NeutralHue;
        // 彩度取"背景色成员"自己的彩度；缺成员时用中性档（绝不放大）
        var chroma = source is { } s2 ? ColorMath.Measure(s2).C : NeutralChroma;
        var tone = source is { } s3 ? ColorMath.Measure(s3).T : SurfaceBaseToneMax;

        // ① 本色在档内 **且本色在 sRGB 里表示得出来** → 原样返回：这就是"融合"（色点与页面底同一色值），
        //    也避免无谓的 HCT 往返（贴色域边界的高彩度浅色往返会漂 4° 色相，实测宇治抹茶 `#E8F2EF`）。
        if (source is { } exactColor
            && tone >= SurfaceBaseToneMin && tone <= SurfaceBaseToneMax
            && ColorMath.IsRepresentable(hue, chroma, tone))
            return exactColor;

        // ② 本色更浅（超上限）：只动明度压到上限档（色相与彩度仍是那个成员的）——
        //    融合不靠"本色原样"：主题卡那枚色点显示的是**实际生效页面底**本身（见 BuildSwatches），
        //    所以这里压档不会破坏融合，只把整页压到 87–91 的深度档。
        if (source is { } lighter)
        {
            _ = lighter;
            for (var t = SurfaceBaseToneMax; t >= SurfaceBaseToneMin; t -= 1.0)
            {
                if (!ColorMath.IsRepresentable(hue, chroma, t)) continue;
                var candidate = ColorMath.FromAlphaHct(0xFF, hue, chroma, t);
                var cardAt = ColorMath.FromAlphaHct(0xFF, hue, chroma,
                    Math.Min(t + SurfaceCardLift, SurfaceCardMaxTone));
                if (ColorMath.ContrastRatio(candidate, cardAt) <= SurfaceFusionMaxContrast)
                    return candidate;
            }
            return ColorMath.FromAlphaHct(0xFF, hue, chroma, SurfaceBaseToneMax);
        }

        // ③ 本色更深（低于下限，例如暮色玫瑰 T89）→ 提到下限档
        var target = Math.Clamp(tone, SurfaceBaseToneMin, SurfaceBaseToneMax);
        for (var c = chroma; c >= 0; c -= 1.0)
        {
            if (ColorMath.IsRepresentable(hue, c, target))
                return ColorMath.FromAlphaHct(0xFF, hue, c, target);
        }
        return ColorMath.FromAlphaHct(0xFF, hue, 0, target);
    }

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

    // ── 表面三层由**用户给的背景色成员**推出（用户令 2026-09-20："优先应用我们选中的那几个颜色"）──
    /// <summary>
    /// 页面底的明度档**上限**：背景色成员比它更浅就压到这个档（87–91 深度档的顶）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>三轮回溯（用户报障 2026-09-20）</b>：① 原样取"最浅成员"、不设上限 → 卡面提亮撞上 T98 上限、
    /// 与页面底同色（薄荷气泡水 / 青梨冻冻实测 1.000 = 卡片看不出是卡片）；② 一律夹 87–91（上一版）
    /// → 深度有了，但"背景色成员"与页面底永远差一档，用户读成"许多颜色都无法融合"（第二轮报障）；
    /// ③ 87–95（`7988403`）→ 融合回来了，但用户第三轮报"**灰成葬礼、卡片贴脸看不出层级**"。
    /// </para>
    /// <para>
    /// **现行 = 87–91 的深度 + 两个保住另两项目标的机制**：① 主题卡上那枚"背景色"色点显示的是
    /// **实际生效的页面底**（`ThemeCardViewModel.BuildSwatches` 用 <see cref="SurfaceBaseColor"/> 替换最浅成员）
    /// → 色点与页面底**逐字节同色 = 融合**（实测 11/11 套 = 1.000），"本色原样"不再是融合的判据；
    /// ② 卡面档距取 <see cref="SurfaceCardLift"/>（6 档），让"卡面对页面底"恒 ≥1.15
    /// （实测 1.165–1.169；5 档只有 1.135–1.138，够不到门槛）。
    /// </para>
    /// <para>
    /// ⚠️ <see cref="SurfaceBaseOf"/> ② 路径里那个"对卡面 ≤ <see cref="SurfaceFusionMaxContrast"/> 就采用"的循环
    /// 在现行档距下**恒不成立**（提亮 5–6 档的对比恒 &gt;1.1）→ 循环必然走到末尾、返回上限档。
    /// 这是**有意的 inert**（`7988403` 起的实测事实）：**别为了"让它活过来"反转判据** ——
    /// 反转等于把卡面压回与页面底融合，正好破坏本档要保住的卡片层级。
    /// </para>
    /// </remarks>
    public const double SurfaceBaseToneMax = 91.0;

    /// <summary>页面底的明度档**下限**：背景色成员比它更深就提到这个档（浅色主题的底线，否则整页偏暗）。</summary>
    public const double SurfaceBaseToneMin = 87.0;

    /// <summary>页面底的**标称档**（= 上限档）：文档与"新出现的页面底取哪一档"的说明都引用它。</summary>
    public const double SurfaceBaseTone = SurfaceBaseToneMax;

    /// <summary>
    /// 卡面相对页面底提亮的档距：**必须让"卡面对页面底"≥1.15**（用户第三轮报障"卡片贴脸看不出层级"
    /// 的机器化判据 = `页面底_夹在明度档内_且卡面与选中底都看得见`）。
    /// </summary>
    /// <remarks>
    /// 实测（`.scratch/themedump`）：页面底落在 87–91 档时，提亮 5 档只有 **1.135–1.138**（够不到 1.15），
    /// 提亮 6 档 = **1.165–1.169** —— 所以档距是 6 而不是 5：这是"深度档 87–91"约束下唯一能带上 1.15 的取值。
    /// </remarks>
    public const double SurfaceCardLift = 6.0;

    /// <summary>卡面明度上限（再亮就与白色胶囊容器分不开了）。</summary>
    public const double SurfaceCardMaxTone = 98.0;

    /// <summary>悬停底相对页面底压深的档距。</summary>
    public const double SurfaceHoverDrop = 6.0;

    /// <summary>悬停底明度下限（再深就不像"浅色主题"了；文字弱档对它的对比度由对比度矩阵卡住）。</summary>
    public const double SurfaceHoverMinTone = 84.0;

    /// <summary>强调容器来源的最低明度档：低于它的成员当容器会"浅色容器上放浅色字"，读不出来。</summary>
    public const double ContainerSourceMinTone = 80.0;

    /// <summary>
    /// "安静"的彩度上限：做选中底/徽标底的浅色成员彩度超过它就太吵（且容易与背景色成员撞成同一个色）。
    /// </summary>
    public const double ContainerNeutralChroma = 12.0;

    /// <summary>正文色（配色最深成员的本色）对页面底的最低对比度：达不到就回退到中性墨生成。</summary>
    public const double TextPrimaryMinContrast = 7.0;

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

        /// <summary>配色原色的"加深版"：保持同一个色相与彩度（= 用户那个颜色的本色），只把明度压到目标档。</summary>
        Argb DarkenTo(Argb source, double tone)
            => At(ColorMath.Measure(source).H, ColorMath.Measure(source).C, tone);
        Argb LightenTo(Argb source, double tone)
            => At(ColorMath.Measure(source).H, ColorMath.Measure(source).C, tone);

        /// <summary>把配色成员压/提到目标明度档（保持它的色相与彩度；用于"本色当某个角色用"）。</summary>
        Argb DarkenOrLighten(Argb source, double tone)
            => At(ColorMath.Measure(source).H, ColorMath.Measure(source).C, tone);

        /// <summary>
        /// 直配取色：**够用就原样返回，不够才按本色补一档**（<see cref="PaletteMode.Exact"/> 的核心）。
        /// </summary>
        /// <param name="source">角色来源（配色成员的**原色**）。</param>
        /// <param name="ok">"这个原色直接当该角色用，够不够用"的判据。</param>
        /// <param name="fallbackTone">不够用时压/提到哪个档（<b>保持原色的色相与彩度</b>）。</param>
        Argb Direct(Argb source, Func<Argb, bool> ok, double fallbackTone)
            => ok(source) ? source : DarkenOrLighten(source, fallbackTone);

        var exact = definition.PaletteMode == PaletteMode.Exact;

        // ── 表面族：**用户给的背景色成员**（本色色相/彩度，明度夹在 87–91）就是页面底 ──
        // 三个层由页面底按固定档距推出：卡面**从页面底提亮**一档；悬停底**从页面底压深**一档。
        // ⚠️ 档距的基准必须是**页面底**而不是"成员原色的明度"：原色比底色档更浅时（晴王青提饮 T97.9、
        //    薄荷气泡水 T98.1），按原色算出来的"卡面"会与页面底同色（实测对比度 1.003 / 1.000 = 看不出卡片）。
        var surfaceSource = families.SurfaceSource ?? At(families.NeutralHue, NeutralChroma, SurfaceBaseToneMax);
        var surfaceSourceTone = ColorMath.Measure(surfaceSource).T;
        var surfaceBase = SurfaceBaseOf(families);
        var surfaceBaseTone = ColorMath.Measure(surfaceBase).T;
        var surfaceCard = LightenTo(surfaceBase, Math.Min(surfaceBaseTone + SurfaceCardLift, SurfaceCardMaxTone));
        var surfaceHover = DarkenTo(surfaceBase, Math.Max(surfaceBaseTone - SurfaceHoverDrop, SurfaceHoverMinTone));

        var textPrimary = At(families.NeutralHue, NeutralChroma, ToneScale.TextPrimary);
        var textSecondary = At(families.NeutralHue, NeutralChroma, ToneScale.TextSecondary);
        var textMuted = At(families.NeutralHue, NeutralChroma, ToneScale.TextMuted);

        // ── 文字主色 = 配色里**最深的那个成员**（它是"墨"，本色压到正文档）──
        // 为什么不让它走 (中性色相, C4) 生成：那样"最深的身份色"会变成**零出口**的颜色
        // （用户令 2026-09-20："我说过优先应用我们选中的这几个颜色"；护栏 = 每个身份色 leave-one-out 都得有影响）。
        // 压到正文档是为了可读性（对比度矩阵逐条卡住），色相与彩度仍然是用户那个颜色的。
        var darkestSource = definition.Palette
            .Select(c => (Color: c, Tone: ColorMath.Measure(c).T))
            .OrderBy(m => m.Tone)
            .First();
        var darkestTone = Math.Min(darkestSource.Tone, ToneScale.TextPrimary);
        if (ColorMath.ContrastRatio(DarkenOrLighten(darkestSource.Color, darkestTone), surfaceBase) >= TextPrimaryMinContrast)
            textPrimary = DarkenOrLighten(darkestSource.Color, darkestTone);

        // 直配模式下另两档文字也取自**同一个最深成员**（只是提亮到各自档位）：
        // 不这么做的话，正文是用户的紫色、次要文字却是我们生成的灰墨（= 又混进了"不是你的颜色"）。
        if (exact)
        {
            textSecondary = LightenTo(textPrimary, ToneScale.TextSecondary);
            textMuted = LightenTo(textPrimary, ToneScale.TextMuted);
        }

        // 悬停底是**唯一一块比页面底更深的表面**（"弱文字对它"必须达标）→ 在"够深"的一侧里
        // **取最深的那个还达标的档**：悬停反馈要看得出来，可读性也不能破（旧写法"从浅往深走到第一个达标"
        // 会在高彩度浅色主题上直接走到与页面底同档 = 悬停反馈消失）。
        // ⚠️ 判据必须**同时**包含两支弱字：中性 T12 更严（它达标时更浅的弱字也必然达标），
        //    而当前的 `textMuted` 才是真正会落在悬停底上的那一支（直配 = 用户墨提亮、自动 = 中性灰墨，
        //    两者的对比度实测骑在阈值两侧 4.47 / 4.51）。两支一起卡，两种模式都不会跌破 4.5。
        var hoverInk = At(families.NeutralHue, NeutralChroma, ToneScale.TextPrimary);
        var hoverFloor = Math.Max(surfaceBaseTone - SurfaceHoverDrop, SurfaceHoverMinTone);
        surfaceHover = DarkenTo(surfaceBase, surfaceBaseTone);
        for (var tone = hoverFloor; tone <= surfaceBaseTone; tone += 1.0)
        {
            var candidate = DarkenTo(surfaceBase, tone);
            if (ColorMath.ContrastRatio(hoverInk, candidate) >= ToneScale.MinMutedOnHover
                && ColorMath.ContrastRatio(textMuted, candidate) >= ToneScale.MinMutedOnHover)
            {
                surfaceHover = candidate;   // 取第一个（= 最深）同时达标的档
                break;
            }
        }
        // 描边：取配色里最接近中间调的成员 —— **够深就直接用**，比档位浅才压到档位（直配）；自动模式保持原行为。
        var outlineSource = families.OutlineSource;
        var outline = outlineSource is { } oSrc
            ? Direct(oSrc, c => ColorMath.Measure(c).T <= ToneScale.LineOutline, ToneScale.LineOutline)
            : At(families.NeutralVariantHue, NeutralVariantChroma, ToneScale.LineOutline);
        var outlineVariant = outlineSource is { } oSrc2
            ? (exact
                ? Direct(oSrc2, c => ColorMath.Measure(c).T >= ToneScale.LineVariant, ToneScale.LineVariant)
                : LightenTo(oSrc2, Math.Max(ColorMath.Measure(oSrc2).T, ToneScale.LineVariant)))
            : At(families.NeutralVariantHue, NeutralVariantChroma, ToneScale.LineVariant);

        // ── 强调族：**优先用配色原色**（深到能撑白字就直接用；太浅才按本色压到填充档）──
        var accentSource = families.AccentSource;
        var accentFill = accentSource is { } aSrc
            ? Direct(aSrc, c => ColorMath.Measure(c).T <= ToneScale.AccentFill, ToneScale.AccentFill)
            : At(families.AccentHue, families.AccentChroma, ToneScale.AccentFill);
        var accentText = DarkenTo(accentFill, ToneScale.AccentText);
        var accentContainer = families.ContainerSource is { } containerSrc
            ? Direct(containerSrc, c => ColorMath.Measure(c).T >= ContainerSourceMinTone, ToneScale.AccentContainer)
            : LightenTo(accentFill, ToneScale.AccentContainer);
        // 选中底 / 落点高亮 / 徽标底 = **必定看得见**（用户报障 2026-09-20："选中行与页面底同色、看不出来"）：
        // 直接取浅成员当容器时，实测 8/11 套与页面底**完全同色**（对比度 1.000 —— 默认主题最浅的
        // `#F2EEF5` 既是页面底又是容器来源）。故这里以"容器来源的色相 + 支撑档彩度"取一个够浅的档，
        // 再按**实测对比度**抬到与页面底、与悬停底都分得开的档位（只动明度、不发明色相）。
        accentContainer = LiftContainerUntilVisible(accentContainer, surfaceBase, surfaceHover);
        // 容器字跟随**容器自己的色相**（否则浅色容器上会浮出一层别的颜色的墨）
        var accentOnContainer = At(ColorMath.Measure(accentContainer).H,
            Math.Max(ColorMath.Measure(accentContainer).C, NeutralChroma), ToneScale.AccentOnContainer);

        var supportIconSource = families.SupportSource ?? accentFill;
        // 支撑容器：够浅就直接用（直配）/ 否则提亮到容器档；再不行才用强调色提亮兜底。
        var supportContainer = families.SupportSource is { } supportSrc
            ? Direct(supportSrc, c => ColorMath.Measure(c).T >= ContainerSourceMinTone, ToneScale.SupportContainer)
            : LightenTo(accentFill, ToneScale.SupportContainer);
        var supportOnContainer = DarkenTo(supportContainer, ToneScale.SupportOnContainer);
        // 支撑图标：够深就直接用，浅了压到填充档（与强调图标同一口径）。
        var supportIcon = ColorMath.Measure(supportIconSource).T <= ToneScale.AccentFill
            ? supportIconSource
            : DarkenTo(supportIconSource, ToneScale.AccentFill);

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

        // 语义令牌：表面族绑到**用户给的背景色推导出来的三层**，其余绑到上面的唯一真值
        var tokens = new Dictionary<string, Argb>(StringComparer.Ordinal)
        {
            [AppTokens.SurfaceBase] = surfaceBase,
            [AppTokens.SurfaceCard] = surfaceCard,
            [AppTokens.SurfaceHover] = surfaceHover,
            [AppTokens.SurfaceTintCard] = surfaceCard,
            [AppTokens.SurfaceTint] = surfaceCard,
            [AppTokens.SurfacePanel] = surfaceHover,
            [AppTokens.SurfaceDialog] = surfaceBase,
            [AppTokens.SurfaceFloating] = surfaceCard,

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
            accentIsDeep ? accentFill : surfaceCard,
            accentIsDeep ? white : textPrimary,
            ToneScale.StateHoverOpacity);
        tokens[AppTokens.StatePressed] = ColorMath.Overlay(
            surfaceCard, textPrimary, ToneScale.StatePressedOpacity);
        tokens[AppTokens.StateDisabledFill] = ColorMath.Overlay(
            surfaceCard, textPrimary, ToneScale.DisabledFillOpacity);
        tokens[AppTokens.StateDisabledContent] = ColorMath.Overlay(
            surfaceCard, textPrimary, ToneScale.DisabledContentOpacity);

        // 标题栏/无底按钮的中性墨状态层（历史值即"中性黑 10% / 20% 叠在任意底上"）
        tokens[AppTokens.StateTitleBarHover] = ColorMath.Overlay(
            surfaceCard, textPrimary, ToneScale.TitleBarHoverOpacity);
        tokens[AppTokens.StateTitleBarPressed] = ColorMath.Overlay(
            surfaceCard, textPrimary, ToneScale.TitleBarPressedOpacity);

        // 遮罩 / 阴影：按"常量 + α"口径（方案 §5.4 例外 ②，Scrim/Shadow 无彩度、不参与旋转）
        var black = Argb.FromInt(unchecked((int)0xFF000000));
        tokens[AppTokens.OverlayScrim] = ColorMath.Overlay(surfaceBase, black, ToneScale.ScrimOpacity);
        tokens[AppTokens.OverlayBusy] = ColorMath.Overlay(
            surfaceBase, surfaceBase, ToneScale.BusyOverlayOpacity);
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

    /// <summary>
    /// 把"选中底 / 落点高亮 / 徽标底"抬到**与页面底、悬浮底都分得开**的明度档（只动明度，不发明色相）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要这一步（用户报障 2026-09-20："选中行与页面底同色、看不出来"）</b>：容器来源是
    /// "浅的低彩度成员"，而**页面底也是那个成员**（明度最高的那个）——实测 8/11 套里
    /// <c>App.Accent.Container</c> 与 <c>App.Surface.Base</c> 的对比度正好是 **1.000**（同一个色值），
    /// 主栏选中行、树选中行、徽标底全部看不见。
    /// </para>
    /// <para>
    /// 判据落在"看得见"这件事实上：沿**浅色方向**逐档找第一个同时满足
    /// 「对页面底 ≥ <see cref="ContainerMinContrastOnBase"/>」与「对悬停底 ≥ <see cref="ContainerMinContrastOnHover"/>」
    /// 的档位（浅色主题的选中底只能往更浅处走，往深处走会撞上悬停底/正文墨）。
    /// 彩度按支撑档钳制（大面积容器必须安静），色相仍是配色成员自己的。
    /// </para>
    /// </remarks>
    private static Argb LiftContainerUntilVisible(Argb container, Argb surfaceBase, Argb surfaceHover)
    {
        // 已经够开就直接用（预设里"浅且安静"的成员本来就够）+ 不许比页面底更深（选中底不能压过页面底）
        if (ColorMath.ContrastRatio(container, surfaceBase) >= ContainerMinContrastOnBase
            && ColorMath.ContrastRatio(container, surfaceHover) >= ContainerMinContrastOnHover
            && ColorMath.Measure(container).T >= ColorMath.Measure(surfaceBase).T)
            return container;

        var m = ColorMath.Measure(container);
        var chroma = Math.Min(m.C, Math.Min(24.0, NeutralVariantChroma * 2));
        for (var tone = Math.Max(m.T, ColorMath.Measure(surfaceBase).T); tone <= 99.0; tone += 1.0)
        {
            var candidate = ColorMath.FromAlphaHct(0xFF, m.H, chroma, tone);
            if (ColorMath.ContrastRatio(candidate, surfaceBase) >= ContainerMinContrastOnBase
                && ColorMath.ContrastRatio(candidate, surfaceHover) >= ContainerMinContrastOnHover)
                return candidate;
        }
        // 兜底：白色（对任何浅色底都是最大对比；只在"浅色成员彩度极高"的极端配色下走到）
        return ColorMath.FromAlphaHct(0xFF, m.H, chroma, 98.0);
    }

    /// <summary>选中底对页面底的最低对比度（低于它 = 选中行看不出来）。</summary>
    public const double ContainerMinContrastOnBase = 1.08;

    /// <summary>选中底对悬停底的最低对比度（悬停中选中也要分得开）。</summary>
    public const double ContainerMinContrastOnHover = 1.06;

    /// <summary>
    /// "背景色成员的色点看起来与页面底融合"的**对比度上限**（`SurfaceBaseOf` ② 路径的循环判据）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据落在渲染事实上：那个色点画在**卡面**上，而卡面由页面底提亮而来 ——
    /// 本色与卡面的对比度超过 1.08 时，用户看到的就是"这个圆没融合"（用户报障 2026-09-20 第二轮）。
    /// </para>
    /// <para>
    /// ⚠️ <b>现行档距下这个判据恒不成立</b>（提亮 5–6 档的对比恒 &gt;1.1）→ ② 路径的循环是有意 inert、
    /// 永远返回上限档；融合改由"主题卡色点显示实际生效页面底"承担（见 <see cref="SurfaceBaseToneMax"/> 的注释）。
    /// **不要反转它来"救活"循环** —— 那是把卡面压回融合，正好破坏卡片层级。
    /// </para>
    /// </remarks>
    public const double SurfaceFusionMaxContrast = 1.08;

    /// <summary>一个锚点在该旋转角下的取值（α 取自锚点；不旋转键保持库基线）。</summary>
    private static Argb Resolve(SurfaceAnchors.Anchor anchor, double rotation)
    {
        if (!anchor.Rotates) return anchor.Today;
        var c = ColorMath.Unpack(anchor.Today);
        var hct = ColorMath.Measure(anchor.Today);
        return ColorMath.FromAlphaHct(c.A, ColorMath.RotateHue(hct.H, rotation), hct.C, hct.T);
    }
}
