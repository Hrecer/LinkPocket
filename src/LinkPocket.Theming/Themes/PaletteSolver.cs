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

    /// <summary>
    /// **表面族彩度**（页面底 / 卡面 / 悬停底 / 选中底共用的"浅色面"彩度）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// = 配色里**浅调成员**的彩度（明度 ≥ <see cref="SurfaceLightMemberMinTone"/> 的成员中彩度最高者，
    /// 封顶 <see cref="SurfaceChromaMax"/>），下限 = 背景色成员自己的彩度（**只增不减**）。
    /// </para>
    /// <para>
    /// <b>为什么不再取"背景色成员自己的彩度"</b>：低彩度的背景色成员（如 `#F2EEF5`，C5.4，
    /// 设计档语义 = "米白"）推出的整页大面积全是灰的（实测表头带/悬停底 C2.6、选中底 C5.4；
    /// 参考量级是表头带 `#E0DAEC` C11.8 / 选中底 `#EEDDF7` C16.5）——只调明度档位改变不了"发灰"观感。
    /// 现行 = 把彩度提升到该配色**自己的浅调成员**的量级：
    /// 色相仍只来自配色（背景色成员），彩度也只来自配色（浅调成员），**没有发明任何颜色**。
    /// </para>
    /// </remarks>
    public double SurfaceChroma { get; init; }
}

/// <summary>
/// 派生管线的**纯函数核心**：<c>ThemeDefinition → ThemeFamilies</c>（配色成员 → 角色槽）。
/// </summary>
/// <remarks>
/// <para>
/// <b>配色成员优先级最高</b>——旧模型按色相聚族、单族时用 ±60° 旋转**发明**一个支撑色相，
/// 且只取「色相圆均值 + 最大彩度」，于是配色成员的明度被整体丢弃
/// （实测默认主题 5 色里 3 个对界面零影响），造出来的色相又会落进肤色带/冷色带。
/// 
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
    /// <b>调色板优先</b>：交付界面的颜色**必须是配色成员的原色**，不是"取色相 + 彩度、
    /// 按档位表重新生成"的近似色。旧实现只用 (H, C) 重建，于是宇治抹茶的 4 个青绿在界面上变成了
    /// 灰绿 + 粉紫（`#EEDDF7` 那种配色里根本不存在的颜色）。
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

        // 表面族彩度：取配色里"浅调成员"的彩度（明度 ≥ SurfaceLightMemberMinTone 里最鲜艳的那个），
        // 封顶 SurfaceChromaMax、下限 = 背景色成员自己的彩度（只增不减 —— 只把"太灰"的抬上来）。
        // 表面族彩度按上述口径提升（详见 ThemeFamilies.SurfaceChroma）。
        var lightMember = measured
            .Where(m => m.Hct.T >= SurfaceLightMemberMinTone)
            .OrderByDescending(m => m.Hct.C)
            .FirstOrDefault();
        var surfaceChroma = lightMember.Color == default
            ? surface.Hct.C
            : Math.Max(surface.Hct.C, Math.Min(lightMember.Hct.C, SurfaceChromaMax));

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
            SurfaceChroma = surfaceChroma,
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
    /// 卡面显示的必须是"这套主题实际长什么样"，否则那个圆点会与它自己的底不同色 ——
    /// 背景色圆点与页面底同色（融合）正是设计目标。
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
    /// <b>为什么要夹档</b>：原样采用"最浅成员"会让页面底漂到很浅的档位
    /// （如 `#F2EEF5` T94.6，而页面底目标档是 T91.3），层感随之塌掉：卡面只能提亮 1–2 档、
    /// 悬停底压深后**反而比页面底更浅**（实·晴王青提饮 T97.9 / 薄荷气泡水 T98.1 的卡面与页面底
    /// 对比度 = **1.003 / 1.000**，卡片根本看不出是卡片）。夹到 87–91 之后：页面底有深度、
    /// 卡面必然浮起来、选中底也腾得出位置。
    /// </para>
    /// <para>
    /// <b>不发明颜色</b>：只动明度，色相与彩度仍是那个成员本身的 —— 界面上的色相依然全部来自配色。
    /// </para>
    /// </remarks>
    private static Argb SurfaceBaseOf(ThemeFamilies families)
    {
        // 表面色相：预设/默认由"明度最高的成员"给出（或主题自己的钉值）
        var source = families.SurfaceSource;
        var hue = source is { } s ? ColorMath.Measure(s).H : families.NeutralHue;
        // 彩度 = **表面族彩度**（配色里"浅调成员"的量级，见 ThemeFamilies.SurfaceChroma）——
        // 不再是"背景色成员自己的彩度"：低彩度成员（C5.4 这种）推出的整页都是灰的。
        var memberChroma = source is { } s2 ? ColorMath.Measure(s2).C : NeutralChroma;
        var chroma = source.HasValue ? families.SurfaceChroma : NeutralChroma;
        var tone = source is { } s3 ? ColorMath.Measure(s3).T : SurfaceBaseToneMax;

        // ① 本色在档内、**且族彩度就等于该成员自己的彩度**、且表示得出来 → 原样返回：
        //    这就是"融合"（色点与页面底同一色值），也避免无谓的 HCT 往返
        //    （贴色域边界的高彩度浅色往返会漂 4° 色相，实测宇治抹茶 `#E8F2EF`）。
        if (source is { } exactColor
            && tone >= SurfaceBaseToneMin && tone <= SurfaceBaseToneMax
            && Math.Abs(chroma - memberChroma) < 0.05
            && ColorMath.IsRepresentable(hue, chroma, tone))
            return exactColor;

        // ② 本色更浅（超上限）：只动明度压到上限档（色相仍是那个成员的、彩度是表面族彩度）——
        //    融合不靠"本色原样"：主题卡那枚色点显示的是**实际生效页面底**本身（见 BuildSwatches），
        //    所以这里压档不会破坏融合，只把整页压到 87–91 的深度档。
        if (source is { } lighter)
        {
            _ = lighter;
            // 起点 = **本色明度夹在档内**（本色比上限浅才压到 91；已在 87–91 里就沿用它）——
            // 这样"背景色成员"本身的明度不会被无谓地抹平（默认主题本色 T89 就落 89）。
            var start = Math.Clamp(tone, SurfaceBaseToneMin, SurfaceBaseToneMax);
            for (var t = start; t >= SurfaceBaseToneMin; t -= 1.0)
            {
                if (!ColorMath.IsRepresentable(hue, chroma, t)) continue;
                var candidate = ColorMath.FromAlphaHct(0xFF, hue, chroma, t);
                var cardAt = ColorMath.FromAlphaHct(0xFF, hue, chroma,
                    Math.Min(t + SurfaceCardLift, SurfaceCardMaxTone));
                if (ColorMath.ContrastRatio(candidate, cardAt) <= SurfaceFusionMaxContrast)
                    return candidate;
            }
            return AtTone(hue, chroma, start);
        }

        // ③ 本色更深（低于下限，例如暮色玫瑰 T89）→ 提到下限档
        return AtTone(hue, chroma, Math.Clamp(tone, SurfaceBaseToneMin, SurfaceBaseToneMax));
    }

    /// <summary>
    /// 给定色相 / 彩度 / 明度档取色：**彩度放不下（贴色域边界）时才逐步降彩度**——
    /// 色相永远不动（"只动明度与彩度上限、绝不换色相"，见 <see cref="ColorMath.IsRepresentable"/>）。
    /// </summary>
    private static Argb AtTone(double hue, double chroma, double tone)
    {
        for (var c = chroma; c >= 0; c -= 1.0)
            if (ColorMath.IsRepresentable(hue, c, tone))
                return ColorMath.FromAlphaHct(0xFF, hue, c, tone);
        return ColorMath.FromAlphaHct(0xFF, hue, 0, tone);
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

    // ── 表面三层由**配色里的背景色成员**推出（优先应用配色成员的原色）──
    /// <summary>
    /// 页面底的明度档**上限**：背景色成员比它更浅就压到这个档（87–91 深度档的顶）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>档位取舍的由来</b>：① 原样取"最浅成员"、不设上限 → 卡面提亮撞上 T98 上限、与页面底同色
    /// （薄荷气泡水 / 青梨冻冻实测 1.000 = 卡片看不出是卡片）；② 一律夹 87–91 → 深度有了，
    /// 但"背景色成员"与页面底永远差一档，读起来"许多颜色都无法融合"；③ 87–95 → 融合回来了，
    /// 但整页发灰、卡片贴脸看不出层级。
    /// </para>
    /// <para>
    /// **现行 = 87–91 的深度 + 两个保住另两项目标的机制**：① 主题卡上那枚"背景色"色点显示的是
    /// **实际生效的页面底**（`ThemeCardViewModel.BuildSwatches` 用 <see cref="SurfaceBaseColor"/> 替换最浅成员）
    /// → 色点与页面底**逐字节同色 = 融合**（实测 11/11 套 = 1.000），"本色原样"不再是融合的判据；
    /// 页面底**彩度**同样不取"背景色成员本色"，改取配色"浅调成员"的量级，
    /// 见 <see cref="ThemeFamilies.SurfaceChroma"/> —— 色相与彩度都仍来自这份配色。
    /// ② 卡面档距取 <see cref="SurfaceCardLift"/>（6 档），让"卡面对页面底"恒 ≥1.15
    /// （实测 1.165–1.169；5 档只有 1.135–1.138，够不到门槛）。
    /// </para>
    /// <para>
    /// ⚠️ <see cref="SurfaceBaseOf"/> ② 路径里那个"对卡面 ≤ <see cref="SurfaceFusionMaxContrast"/> 就采用"的循环
    /// 在现行档距下**恒不成立**（提亮 5–6 档的对比恒 &gt;1.1）→ 循环必然走到末尾、返回上限档。
    /// 这是**有意的 inert**（实测事实）：**别为了"让它活过来"反转判据** ——
    /// 反转等于把卡面压回与页面底融合，正好破坏本档要保住的卡片层级。
    /// </para>
    /// </remarks>
    public const double SurfaceBaseToneMax = 91.0;

    /// <summary>页面底的明度档**下限**：背景色成员比它更深就提到这个档（浅色主题的底线，否则整页偏暗）。</summary>
    public const double SurfaceBaseToneMin = 87.0;

    /// <summary>页面底的**标称档**（= 上限档）：文档与"新出现的页面底取哪一档"的说明都引用它。</summary>
    public const double SurfaceBaseTone = SurfaceBaseToneMax;

    /// <summary>
    /// 「浅调成员」的明度下限：表面族彩度取**明度 ≥ 它的成员里彩度最高者**的量级
    /// （见 <see cref="ThemeFamilies.SurfaceChroma"/>）。
    /// </summary>
    public const double SurfaceLightMemberMinTone = 80.0;

    /// <summary>
    /// 表面族彩度上限：浅色大面积必须"安静"，不能把配色的高彩度浅色整片铺满
    /// （16 = 既有的容器彩度上限档 <c>NeutralVariantChroma × 2</c>，见 `LiftContainerUntilVisible`）。
    /// </summary>
    public const double SurfaceChromaMax = 16.0;

    /// <summary>
    /// 卡面相对页面底提亮的档距：**必须让"卡面对页面底"≥1.15**（机器化判据 =
    /// `页面底_夹在明度档内_且卡面与选中底都看得见`）。
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

    /// <summary>
    /// 悬停可见性护栏：悬停底对**页面底**的最低可分辨对比度（低于它 = 鼠标移上去看不出变化）。
    /// 只在 <see cref="ThemeDefinition.EnforceHoverVisibility"/> 打开时参与判定。
    /// </summary>
    public const double HoverVisibilityMinRatio = 1.06;

    /// <summary>悬停可见性护栏允许探到的最深档（弱字 ≥4.5 的判据照旧由循环卡住，不在这里放水）。</summary>
    public const double SurfaceHoverGuardMinTone = 84.0;

    /// <summary>
    /// 浅带层（表头带 `App.Surface.HeaderBand` / 面板层 `App.Surface.Panel`）的档距规则**按分层手段分两支**：
    /// 色相贴上强调槽 ⇒ 页面底**本色档**（本常量 = 0，靠两支近邻色相分层）；
    /// 色相贴不上 ⇒ 压深 <see cref="SurfaceBandToneDrop"/> 档（明度分层，另加 <see cref="SurfaceBandChromaComp"/> 彩度补偿）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一层**同时定两处**：表格顶部的表头带 + 侧区面板 / 状态栏 / 徽标底——它们是**同一条带**
    /// （角色不同、层级同一条，两枚键绑同一个值，同 `TintCard`/`Tint`/`Floating` 绑卡面的口径）。
    /// 改档 = 那一片颜色**整体**换，不存在"只改一处、别处还是旧灰"的可能。
    /// </para>
    /// <para>
    /// **贴不上的主题必须明度分层**：与页面底同色相、同彩度、同明度 = 逐字节重合
    /// （实测 8 套带/底 = 1.000，侧区面板 / 状态栏融进页面底，看不出层）。
    /// 压深方向是浅带层的常规语言（悬停底就是"承载面压深 2–3 档"的同一做法）；
    /// 同彩度越深越显灰 ⇒ 压深必须伴彩度补偿。**提亮档不用**：大面积提亮读成"发白"（曾用 +3 实测）。
    /// </para>
    /// </remarks>
    public const double SurfaceBandLift = 0.0;

    /// <summary>色相贴不上时浅带层相对页面底的压深档距（明度分层；11 套实测带/底 = 1.065–1.075，判据窗口 [1.05, 1.15] 两头卡）。</summary>
    public const double SurfaceBandToneDrop = 2.5;

    /// <summary>浅带层压深时的彩度补偿（抵消"同彩度越深越显灰"；色相仍不动，贴色域边界时由 <see cref="AtTone"/> 降彩度兜住）。</summary>
    public const double SurfaceBandChromaComp = 2.0;

    /// <summary>
    /// 浅带层允许**贴到强调槽色相**的条件：强调槽与表面槽的色相差 ≤ 此值（度）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 效果 = 同一套配色里出现**两支近邻色相**：页面底 / 卡面走表面族色相，表头带 / 侧区面板 / 状态栏
    /// 走强调槽那一支（默认主题表面 H297.4 ↔ 强调 H310.7，差 13.2° ⇒ 页面偏蓝紫、条带偏玫紫）
    /// = **同族弱撞色**：看得出区别、但不突兀。
    /// </para>
    /// <para>
    /// ⚠️ **只贴、不插值**：条带色相只能是**配色成员本来的色相**（强调槽或表面槽），不许落在两者之间。
    /// 早先的写法是"向强调槽旋转、上限 14°"——那会在 11 套里的 5 套上停在一支**配色里根本不存在的色相**
    /// （赭石玫瑰 / 莲鼠尾草 / 樱花白脱…实测离最近的配色成员 9.0°–14.7°），违反"界面色必须能在用户给的调色板里找到"。
    /// 强调槽与表面槽不相邻的主题（樱花白脱差 162.9°）因此**不换色相**：条带与页面底同色，宁可不撞也不发明色。
    /// </para>
    /// </remarks>
    public const double SurfaceBandHueNeighbourMax = 16.0;

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

        /// <summary>配色原色的"加深版"：保持同一个色相与彩度（= 该成员的本色），只把明度压到目标档。</summary>
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

        // ── 表面族：**配色里的背景色成员**（本色色相/彩度，明度夹在 87–91）就是页面底 ──
        // 层次由页面底按固定档距推出：卡面**提亮**、悬停底**压深一档**、面板层再**压深一档**。
        // ⚠️ 档距的基准必须是**页面底**而不是"成员原色的明度"：原色比底色档更浅时（晴王青提饮 T97.9、
        //    薄荷气泡水 T98.1），按原色算出来的"卡面"会与页面底同色（实测对比度 1.003 / 1.000 = 看不出卡片）。
        var surfaceSource = families.SurfaceSource ?? At(families.NeutralHue, NeutralChroma, SurfaceBaseToneMax);
        var surfaceSourceTone = ColorMath.Measure(surfaceSource).T;
        // 表面族色相 = "背景色成员"本色的色相（`SurfaceBaseOf` 内部用的是同一个）——
        // 表面三层之外，**选中底与次按钮底也锚在它上面**（见下方两处），这样"选中行 / 次按钮"
        // 与它们所在的页面 / 卡面天然同色调，不会读成"跳了一块别的颜色"。
        var surfaceHue = families.SurfaceSource is { } surfSrc ? ColorMath.Measure(surfSrc).H : families.NeutralHue;
        var surfaceBase = SurfaceBaseOf(families);
        var surfaceBaseTone = ColorMath.Measure(surfaceBase).T;
        var surfaceCard = LightenTo(surfaceBase, Math.Min(surfaceBaseTone + SurfaceCardLift, SurfaceCardMaxTone));
        var surfaceHover = DarkenTo(surfaceBase, Math.Max(surfaceBaseTone - SurfaceHoverDrop, SurfaceHoverMinTone));
        // 浅带层（表头带 + 侧区面板 / 状态栏 / 徽标底，**同一条带、同一个值**）：
        //  色相 = **只在强调槽与表面槽本来就相邻（≤ `SurfaceBandHueNeighbourMax`）时贴到强调槽那一支**，
        //         否则与页面底同色相。⇒ 弱撞色是"用配色里已有的第二支色相"，不是两支之间插出来的新色相。
        //  分层手段随色相结果二选一：贴上 ⇒ 本色档（色相分层：两支近邻色相足以分开）；
        //         贴不上 ⇒ 同色相 + 同彩度 + 同明度会与页面底逐字节重合（实测 8 套带/底 = 1.000），
        //         只能明度分层：压深 `SurfaceBandToneDrop` 档 + 彩度补偿 `SurfaceBandChromaComp`。
        var bandSticks = ColorMath.HueDistance(families.NeutralHue, families.AccentHue) <= SurfaceBandHueNeighbourMax;
        var bandHue = bandSticks
            ? ColorMath.NormalizeHue(families.AccentHue)
            : ColorMath.NormalizeHue(families.NeutralHue);
        double bandTone, bandChroma;
        if (bandSticks)
        {
            // 色相分层 = 页面底本色档。档位夹在 [页面底档下限, 页面底本色档]：页面底自身可能因量化
            // 落在 87 以下（实测有 86.95），因此下限取"两者较小者"，绝不让 Clamp 的 min > max。
            bandTone = Math.Clamp(surfaceBaseTone + SurfaceBandLift,
                Math.Min(SurfaceBaseToneMin, surfaceBaseTone), surfaceBaseTone);
            bandChroma = families.SurfaceChroma;
        }
        else
        {
            // 明度分层 = 压深 + 彩度补偿（同彩度越深越显灰）。更低明度的色域更宽，
            // +彩度反而比本色档更好放；放不下时 AtTone 降彩度兜住，色相永远不动。
            bandTone = surfaceBaseTone - SurfaceBandToneDrop;
            bandChroma = families.SurfaceChroma + SurfaceBandChromaComp;
        }
        var surfaceBand = bandSticks
            ? At(bandHue, families.SurfaceChroma, bandTone)
            : AtTone(bandHue, bandChroma, bandTone);
        var surfaceHeaderBand = surfaceBand;
        var surfacePanel = surfaceBand;

        var textPrimary = At(families.NeutralHue, NeutralChroma, ToneScale.TextPrimary);
        var textSecondary = At(families.NeutralHue, NeutralChroma, ToneScale.TextSecondary);
        var textMuted = At(families.NeutralHue, NeutralChroma, ToneScale.TextMuted);

        // ── 文字主色 = 配色里**最深的那个成员**（它是"墨"，本色压到正文档）──
        // 为什么不让它走 (中性色相, C4) 生成：那样"最深的身份色"会变成**零出口**的颜色
        // （护栏 = 每个身份色 leave-one-out 都得有影响）。压到正文档是为了可读性（对比度矩阵逐条卡住），
        // 色相与彩度仍然取自配色成员的原色。
        var darkestSource = definition.Palette
            .Select(c => (Color: c, Tone: ColorMath.Measure(c).T))
            .OrderBy(m => m.Tone)
            .First();
        var darkestTone = Math.Min(darkestSource.Tone, ToneScale.TextPrimary);
        if (ColorMath.ContrastRatio(DarkenOrLighten(darkestSource.Color, darkestTone), surfaceBase) >= TextPrimaryMinContrast)
            textPrimary = DarkenOrLighten(darkestSource.Color, darkestTone);

        // 直配模式下另两档文字也取自**同一个最深成员**（只是提亮到各自档位）：
        // 不这么做的话，正文取自配色成员、次要文字却由中性色相生成 —— 同一族文字混进两种来源。
        if (exact)
        {
            textSecondary = LightenTo(textPrimary, ToneScale.TextSecondary);
            textMuted = LightenTo(textPrimary, ToneScale.TextMuted);
        }

        // 悬停 = **承载面自己压深一档**（三层各一支令牌：页面底 `Surface.Hover` / 卡面 `Surface.CardHover`
        // / 浅带层 `Surface.BandHover`）。一支通用悬停服务三层 ⇒ 画在卡面 / 浅带上的悬停会换成**另一支色相、
        // 另一个明度档**的颜色：屏幕上不是"这一条加深了"，而是"跳了一块别的颜色"。
        // 档位 = 在"够深"的一侧里**取最深的那个还达标的档**：反馈要看得出来，弱文字对比不许破 4.5
        //（旧写法"从浅往深走到第一个达标"会在高彩度浅色主题上直接走到与承载面同档 = 悬停反馈消失）。
        // ⚠️ 判据必须**同时**包含两支弱字：中性 T12 更严（它达标时更浅的弱字也必然达标），
        //    而当前的 `textMuted` 才是真正会落在悬停底上的那一支（直配 = 配色墨提亮、自动 = 中性灰墨，
        //    两者的对比度实测骑在阈值两侧 4.47 / 4.51）。两支一起卡，两种模式都不会跌破 4.5。
        var hoverInk = At(families.NeutralHue, NeutralChroma, ToneScale.TextPrimary);
        var otherMuted = exact
            ? At(families.NeutralHue, NeutralChroma, ToneScale.TextMuted)
            : LightenTo(textPrimary, ToneScale.TextMuted);
        Argb HoverOn(Argb carrier, Argb muted)
        {
            var carrierTone = ColorMath.Measure(carrier).T;
            var floor = Math.Max(carrierTone - SurfaceHoverDrop, SurfaceHoverMinTone);
            // 悬停可见性护栏（仅对开了它的主题）：「卡面提亮」与「悬停压深」同为 6 档时，
            // 卡面那一支压深后**正好落回页面底色值**——鼠标移上去和背景一个色，反馈等于消失。
            // 这里只把**撞色的那一支**的起点再往下探（别的支取值不动），探到与页面底分得开为止。
            if (definition.EnforceHoverVisibility)
                while (floor > SurfaceHoverGuardMinTone
                       && ColorMath.ContrastRatio(DarkenTo(carrier, floor), surfaceBase) < HoverVisibilityMinRatio)
                    floor -= 1.0;
            for (var tone = floor; tone <= carrierTone; tone += 1.0)
            {
                var candidate = DarkenTo(carrier, tone);
                if (ColorMath.ContrastRatio(hoverInk, candidate) >= ToneScale.MinMutedOnHover
                    && ColorMath.ContrastRatio(muted, candidate) >= ToneScale.MinMutedOnHover)
                    return candidate;   // 第一个（= 最深）同时达标的档
            }
            return carrier;   // 一档都不达标：宁可不给悬停反馈，也不压出读不出字的底
        }
        surfaceHover = HoverOn(surfaceBase, textMuted);
        var cardHover = HoverOn(surfaceCard, textMuted);
        // 浅带层的字幕合同 = "面板上只许用 Secondary 及以上"（对比度矩阵 `Text.Secondary / Surface.Panel`）
        // ⇒ 贴不上的一支按这支字反推悬停档：带已压深 2.5 档，若仍按弱字 ≥4.5 反推，悬停会被逼回承载面
        // 同档（悬停消失），而弱字本来就不许上带。贴上的一支保持弱字口径（该分支的值逐字节不动）。
        var bandHover = HoverOn(surfaceBand, bandSticks ? textMuted : textSecondary);
        // ⚠️ 选中底的邻居约束用**另一种模式也算一遍、取更浅的那个**：悬停档按"弱文字对它 ≥4.5"反推，
        //    而弱文字在两种模式下取值不同 ⇒ 悬停底可能差一档（这个例外由
        //    `配色应用方式_缺省是自动调色_直配只改文字两档` 点名放行）。拿"更浅的那个"当约束，
        //    选中底才会在两种模式下算出**同一个色**（结构色必须逐字节相同）。
        var lightestCardHover = Lightest(HoverOn(surfaceCard, textMuted), HoverOn(surfaceCard, otherMuted));

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
        // 列表行 / 树行的**选中底与拖拽落点高亮** = 画在**卡面**上（卡面比页面底还亮 6 档），
        // 判据锚在卡面上（对卡面 ≥1.22，见 `SelectedSurfaceUntilVisible`）—— 因此它比强调容器深一大截。
        // **两个令牌、两条判据**：一个令牌服务两种承载面时，浅了行看不出选中、深了指示器/徽标发灰（WARNINGS 118）。
        // 邻居 = **卡面悬停底**（同一块卡上"悬停行"与"选中行"必须分得开），不是页面底那一支悬停。
        // ⚠️ **色相 = 表面族色相**，不是强调容器槽成员的色相：选中行铺在页面 / 卡面上，只有与背景同色调
        //    才读作"这一行被选中"（实测青提：容器槽成员是奶黄 H100.9 ⇒ 选中底 `#DFD6B0` 与表面族 H137 差
        //    35.3°，用户："选中态是黄色，而不是与背景同色调"）。彩度 / 明度 / 三条阈值一概不动 ⇒
        //    只有"容器槽与表面槽不同色相"的主题变值（11 套里实测只有晴王青提饮一套）。
        var containerSeed = ColorMath.Measure(accentContainer);
        var selectedSeed = At(surfaceHue, containerSeed.C, containerSeed.T);
        var surfaceSelected = SelectedSurfaceUntilVisible(selectedSeed, surfaceBase, surfaceCard, lightestCardHover,
            families.SurfaceChroma);
        // **次按钮底**（TonalButton / SoftPillButton / 计数药丸）= 表面族色相 + **提高一档的彩度**，
        // 取"对卡面 ≥ `ControlSurfaceMinContrastOnCard` 的**最浅**达标档"。
        // 承载面三种：卡面（命令栏胶囊里的四枚药丸）/ 面板（右侧栏「打开」）/ 选中行底（计数药丸）——
        // 走档锚在**卡面**（三者里最亮的承载面：对面板 / 选中底的对比度自动更高）。
        // ⚠️ 彩度**不再取表面族彩度**（那是"背景色成员的量级"，默认主题只有 C16）：
        // 浅明度 + 低彩度 = 灰紫——"深浅怎么调都还是灰"（用户实测两轮："发灰"是根因、"和背景差不多"是表象）。
        // "+14" = 比表面族鲜艳一大档（默认 C16→30），封顶 34（浅明度上"看得出颜色"需要更高的彩度）。
        // 为什么不用支撑槽本色：本色深浅随配色摆动 ⇒ 实测对卡面 1.02–1.97（重合 ↔ 重色，两头都被用户点过名），
        // 详见 `ControlSurfaceMinContrastOnCard` 的注释。
        var controlChroma = Math.Min(34.0, families.SurfaceChroma + 14.0);
        var surfaceControl = At(surfaceHue, controlChroma, ControlSurfaceToneFloor);
        for (var tone = 99.0; tone >= ControlSurfaceToneFloor; tone -= 0.5)
        {
            var candidate = At(surfaceHue, controlChroma, tone);
            if (ColorMath.ContrastRatio(candidate, surfaceCard) < ControlSurfaceMinContrastOnCard) continue;
            surfaceControl = candidate;
            break;
        }
        // 强调容器（导航 / 分段 / 分段指示器 / 面包屑当前段 / 徽标 / chip 的浅色底）= 承载面是**页面底与悬停底**，
        // 判据 = 对页面底 ≥1.08、对悬停底 ≥1.06 —— 沿浅色方向抬（`LiftContainerUntilVisible`）。
        // ⚠️ 直接取浅成员当容器时实测 8/11 套与页面底**完全同色**（1.000 —— 最浅成员往往既是页面底
        //    又是容器来源），故必须有这一步。
        accentContainer = LiftContainerUntilVisible(accentContainer, surfaceBase, surfaceHover, families.SurfaceChroma);
        // 容器字跟随**容器自己的色相**（否则浅色容器上会浮出一层别的颜色的墨）
        var accentOnContainer = At(ColorMath.Measure(accentContainer).H,
            Math.Max(ColorMath.Measure(accentContainer).C, NeutralChroma), ToneScale.AccentOnContainer);

        var supportIconSource = families.SupportSource ?? accentFill;
        // 支撑容器 = **配色成员本色**（用户选的颜色），不再提亮到"容器档"。
        // 为什么改：把暮色玫瑰的藕紫 `#907884`（T53 C13.7）提亮到容器档 T90 而彩度不下调，
        // 得到的是 `#F8DBE8` —— 一支**配色里根本不存在的甜粉**（实测：用户看到的"凭空冒出来的粉色按钮"）。
        // 浅底上同样的彩度显眼得多，"够浅才能直接用"这条门槛逼着深成员必须被改造；
        // 直接取本色 = 界面上出现的每一支都能在用户给的调色板里找到（悬停/状态这类派生仍走算法）。
        // 墨先按**本色**定档（色相与本色的容器一致，下面提亮容器时对比度只随明度变）
        var inkOnSupport = DarkenTo(supportIconSource, ToneScale.SupportOnContainer);
        var supportContainer = families.SupportSource is { } supportSrc
            ? LightenUntilInkReadable(supportSrc, inkOnSupport, ToneScale.MinInkOnContainer)
            : LightenTo(accentFill, ToneScale.SupportContainer);
        var supportOnContainer = DarkenTo(supportContainer, ToneScale.SupportOnContainer);
        // 支撑图标：够深就直接用，浅了压到填充档（与强调图标同一口径）。
        var supportIcon = ColorMath.Measure(supportIconSource).T <= ToneScale.AccentFill
            ? supportIconSource
            : DarkenTo(supportIconSource, ToneScale.AccentFill);
        // **文件夹图标 = 这一支再提淡**：钉在填充档（T40 上下）时，每一套主题里的文件夹图标都
        // 黑压压一片（实测 11/11 套 —— 用户："偏深、太强烈"）。改成沿**本色的色相与彩度**往浅走，
        // 取"对页面底刚好还看得见"的最浅档 = 浅浅深了一层的那种淡（判据 3:1，WCAG 非文本图形）。
        var typeFolder = LightenUntilVisible(supportIcon, surfaceBase, ToneScale.MinIconOnSurface);

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

        // 语义令牌：表面族绑到**配色背景色成员推导出来的三层**，其余绑到上面的唯一真值
        var tokens = new Dictionary<string, Argb>(StringComparer.Ordinal)
        {
            [AppTokens.SurfaceBase] = surfaceBase,
            [AppTokens.SurfaceCard] = surfaceCard,
            [AppTokens.SurfaceHover] = surfaceHover,
            [AppTokens.SurfaceCardHover] = cardHover,
            [AppTokens.SurfaceBandHover] = bandHover,
            [AppTokens.SurfaceSelected] = surfaceSelected,
            [AppTokens.SurfaceControl] = surfaceControl,
            [AppTokens.SurfaceTintCard] = surfaceCard,
            [AppTokens.SurfaceTint] = surfaceCard,
            [AppTokens.SurfacePanel] = surfacePanel,
            [AppTokens.SurfaceHeaderBand] = surfaceHeaderBand,
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

            [AppTokens.TypeFolder] = typeFolder,
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
        // 同一条护栏（仅对开了它的主题）：按钮悬停底若是"浅承载面叠墨"，结果可能正好落回页面底色值
        //（实测晴王青提饮 #D7EBC9 对页面底 #D9EBC8，ΔRGB ≤ 2）——改叠在**已经压深的卡面悬停底**上，
        // 反馈才看得出来。叠层强度与墨色都不变，只是承载面换成深一档的那一支。
        if (definition.EnforceHoverVisibility
            && ColorMath.ContrastRatio(tokens[AppTokens.StateHover], surfaceBase) < HoverVisibilityMinRatio)
            tokens[AppTokens.StateHover] = ColorMath.Overlay(cardHover, textPrimary, ToneScale.StateHoverOpacity);
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

    /// <summary>两块颜色里更浅的那一块（明度档更大者）。</summary>
    private static Argb Lightest(Argb a, Argb b)
        => ColorMath.Measure(a).T >= ColorMath.Measure(b).T ? a : b;

    /// <summary>
    /// 把**强调容器**（导航 / 分段 / 分段指示器 / 面包屑当前段 / 徽标 / chip 的浅色底）抬到
    /// "对页面底、对悬停底都分得开"的明度档（只动明度与彩度，不发明色相）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要这一步</b>：容器来源是"浅的低彩度成员"，而**页面底也是那个成员**（明度最高的那个）
    /// ——实测 8/11 套里它与 <c>App.Surface.Base</c> 的对比度曾经正好是 **1.000**（同一个色值），
    /// 导航指示器与徽标全部看不见。
    /// </para>
    /// <para>
    /// 判据落在"看得见"这件事实上：沿**浅色方向**逐档找第一个同时满足
    /// 「对页面底 ≥ <see cref="ContainerMinContrastOnBase"/>」与「对悬停底 ≥ <see cref="ContainerMinContrastOnHover"/>」
    /// 的档位（这类面画在页面底 / 悬停底上，往深处走会撞上悬停底与正文墨）。
    /// 彩度 = 表面族彩度（<paramref name="familyChroma"/>，**只增不减**）、上限仍是容器自己的安静档，
    /// 色相仍是配色成员自己的。
    /// </para>
    /// <para>
    /// ⚠️ <b>这一支只服务"画在页面底 / 悬停底上"的面</b>：列表行 / 树行的选中底画在**卡面**上，
    /// 用这个浅档会被读成"没选中"（实测它与卡面的对比只有 1.001–1.074）。两者是**两个令牌两条判据**，
    /// 见 <see cref="SelectedSurfaceUntilVisible"/> 与 开发文档。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 把一支**前景图标色**沿自己的色相/彩度往浅走，取"对承载面刚好看得见"的**最浅**那一档。
    /// </summary>
    /// <remarks>
    /// 钉档位（旧写法）与"按可见性取档"的差别：图标钉在填充档（T40 上下）时，每一套主题里都是
    /// 同一种黑压压的深度；而"取最浅的达标档"让每一套主题自动落在它自己能读的边界上——
    /// 浅色主题给出浅浅深了一层的淡，深色主题也不会糊掉。色相与彩度始终是配色成员本色的。
    /// </remarks>
    private static Argb LightenUntilVisible(Argb color, Argb carrier, double minRatio)
    {
        var start = ColorMath.Measure(color).T;
        var m0 = ColorMath.Measure(color);
        Argb? best = null;
        for (var tone = start; tone <= 100.0; tone += 1.0)
        {
            var candidate = ColorMath.FromAlphaHct(0xFF, ColorMath.NormalizeHue(m0.H), m0.C, tone);
            // 记**最后一个**达标的档 = 最浅的那一支（一达标就返回会停在原档，等于没淡）
            if (ColorMath.ContrastRatio(candidate, carrier) >= minRatio) best = candidate;
        }
        return best ?? color;   // 一档都不达标（承载面极端）：原样返回，不发明颜色
    }

    /// <summary>
    /// 容器**沿自己的本色往浅走**，取"容器上的墨读得清"的**第一个**档（= 最深 = 最接近本色的那一档）。
    /// </summary>
    /// <remarks>
    /// 与旧写法的差别：旧写法把不够浅的成员一律提到容器档（T90 上下），于是暮色玫瑰的藕紫
    /// <c>#907884</c> 被提亮成 <c>#F8DBE8</c> —— 一支配色里根本不存在的甜粉（用户实测："没选粉色，按钮却是粉的"）。
    /// 这里只在**本色确实压得墨读不出来**时才提亮，且停在刚够用的那一档：色相与彩度始终是本色的，
    /// 提亮幅度也最小。墨读得清的本色（如抹茶/青提的浅成员）则逐字节不动。
    /// </remarks>
    private static Argb LightenUntilInkReadable(Argb container, Argb ink, double minRatio)
    {
        var m = ColorMath.Measure(container);
        for (var tone = m.T; tone <= 100.0; tone += 1.0)
        {
            var candidate = ColorMath.FromAlphaHct(0xFF, ColorMath.NormalizeHue(m.H), m.C, tone);
            if (ColorMath.ContrastRatio(ink, candidate) >= minRatio) return candidate;
        }
        return container;
    }

    private static Argb LiftContainerUntilVisible(Argb container, Argb surfaceBase, Argb surfaceHover, double familyChroma)
    {
        var m = ColorMath.Measure(container);
        // 彩度：**只增不减**地抬到表面族彩度（页面底 / 悬停底 / 容器底 = 同一个"浅色面"家族）——
        // 页面底加紫之后，若容器底还停在"背景色成员本色"的彩度上（默认 C7.8），它会**比页面底更灰**
        // （目标量级：选中底 `#EEDDF7` C16.5、表头带 `#E0DAEC` C11.8）。上限仍是容器自己的安静档。
        var chroma = Math.Min(Math.Max(m.C, familyChroma), Math.Min(24.0, NeutralVariantChroma * 2));
        // 算好的彩度**当场落回种子色**：此后 m 与由它构造的候选同源，不存在"m 还是旧彩度、
        // 候选已是新彩度"的失同步分支。
        container = ColorMath.FromAlphaHct(0xFF, m.H, chroma, m.T);
        m = ColorMath.Measure(container);

        // 已经够开就直接用（预设里"浅且安静"的成员本来就够）+ 不许比页面底更深（容器底不能压过页面底）
        if (ColorMath.ContrastRatio(container, surfaceBase) >= ContainerMinContrastOnBase
            && ColorMath.ContrastRatio(container, surfaceHover) >= ContainerMinContrastOnHover
            && m.T >= ColorMath.Measure(surfaceBase).T)
            return container;

        for (var tone = Math.Max(m.T, ColorMath.Measure(surfaceBase).T); tone <= 99.0; tone += 1.0)
        {
            var candidate = ColorMath.FromAlphaHct(0xFF, m.H, chroma, tone);
            if (ColorMath.ContrastRatio(candidate, surfaceBase) >= ContainerMinContrastOnBase
                && ColorMath.ContrastRatio(candidate, surfaceHover) >= ContainerMinContrastOnHover)
                return candidate;
        }
        // 兜底：最浅档（对任何浅色底都是最大对比；只在"浅色成员彩度极高"的极端配色下走到）
        return ColorMath.FromAlphaHct(0xFF, m.H, chroma, 98.0);
    }

    /// <summary>
    /// 把**列表行 / 树行的选中底**（与拖拽落点高亮）定到"在**卡面**上明显看得出来"的明度档
    /// （只动明度与彩度，不发明色相）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么判据与强调容器不同</b>：选中行画在**卡面**上，而卡面比页面底还亮 6 档 ——
    /// 按"对页面底 ≥1.08"往浅处抬的容器与卡面的对比只有 **1.001–1.074**（比承载它的面还亮一点点），
    /// 屏幕上"选中"与"未选中"几乎同色。所以这条判据锚在真实承载面（卡面）上：≥
    /// <see cref="ContainerMinContrastOnCard"/>（最硬），并保持对页面底 / 悬停底的最低对比。
    /// </para>
    /// <para>
    /// 取值方式 = **从最浅处往深处走，第一个三条阈值都满足的档位**（= 够得开阈值的**最浅**档）。
    /// 早先反过来"从允许的最深档起往浅处找第一个达标" ⇒ 默认档 76 当场成立，于是选中底一路下探到
    /// <c>#C0B8D0</c>（对卡面 1.68，阈值只要 1.22）——同一彩度在更低明度上就是**灰**，
    /// 屏幕上选中行读成"压了一块灰紫"。可见性阈值是下限，不是"越狠越好"。
    /// </para>
    /// <para>
    /// ⚠️ **不把"弱文字 ≥4.5"加进这条走档**：两条阈值在这套配色下互斥——对卡面 ≥1.22 要求 ≤T85.5，
    /// 而弱文字 ≥4.5 要求 ≥T86（选中底越浅字越清楚、与卡面越不开）。加进去会让走档一路落回最深档
    /// （实测：加了就退回 <c>#C0B8D0</c>，弱字反而 3.40 更差）。当前值弱字 4.44、次列 6.40、正文 11.21，
    /// 弱字这一条由 <c>ThemeContrastTests.对比度矩阵</c> 按实测棘轮（≥4.4）卡住，不让它继续往深滑
    /// —— 换表面族色相后最紧的一套是青提（走档由"对卡面悬停 ≥1.06"钉在 T85.5）实测 4.404，
    /// 棘轮仍取 4.4（余量 0.004：闸门本来就该在这条线上报警）。</para>
    /// <para>
    /// 种子 = **强调容器本色，但色相换成表面族色相**（彩度与明度仍取容器种子自己的 ——
    /// 只有"容器槽与表面槽不同色相"的主题会因此变值）；彩度 = 表面族彩度（**只增不减**，与页面底 /
    /// 悬停底同族）、上限 = 容器安静档（<c>NeutralVariantChroma × 2</c>）；色相一律取自配色成员。
    /// 悬停底传"**卡面**那一支、且两种模式里更浅的那一个"（调用方给）：选中行与悬停行画在同一块卡上，
    /// 邻居是它；用两种模式里更浅的那个当约束，选中底才在自动 / 直配下算出**同一个色**。
    /// </para>
    /// </remarks>
    private static Argb SelectedSurfaceUntilVisible(Argb seed, Argb surfaceBase, Argb surfaceCard, Argb cardHover,
        double familyChroma)
    {
        var m = ColorMath.Measure(seed);
        var chroma = Math.Min(Math.Max(m.C, familyChroma), Math.Min(24.0, NeutralVariantChroma * 2));
        for (var tone = 99.0; tone >= ContainerToneFloor; tone -= 0.5)
        {
            var candidate = ColorMath.FromAlphaHct(0xFF, m.H, chroma, tone);
            if (ColorMath.ContrastRatio(candidate, surfaceCard) < ContainerMinContrastOnCard) continue;
            if (ColorMath.ContrastRatio(candidate, surfaceBase) < ContainerMinContrastOnBase) continue;
            if (ColorMath.ContrastRatio(candidate, cardHover) < ContainerMinContrastOnHover) continue;
            return candidate;
        }
        return ColorMath.FromAlphaHct(0xFF, m.H, chroma, ContainerToneFloor);
    }

    /// <summary>
    /// 选中底对**卡面**的最低对比度（列表行、树行、下拉选中项都画在卡面上——这条是最硬的一条）。
    /// </summary>
    /// <remarks>
    /// 只用在 <see cref="SelectedSurfaceUntilVisible"/>（<c>App.Surface.Selected</c>）上。
    /// 实测口径：默认主题选出 <c>#C0B8D0</c>（对卡面 1.68、对页面底 1.43、对悬停底 1.33、容器字 7.97）
    /// ——屏幕上"选中"与"未选中"一眼分得开；对卡面低于 1.15 就退化成"看着像没选中"
    /// （改之前实测只有 1.001–1.074：选中底比承载它的卡面还亮）。
    /// </remarks>
    public const double ContainerMinContrastOnCard = 1.22;

    /// <summary>选中底允许下探的最深档（再深就会抢正文墨的对比度）。</summary>
    public const double ContainerToneFloor = 76.0;

    /// <summary>容器底对页面底的最低对比度（低于它 = 指示器 / 徽标看不见）。</summary>
    public const double ContainerMinContrastOnBase = 1.08;

    /// <summary>
    /// 容器底对悬停底的最低对比度（两种令牌共用这一条：指示器画在悬停底上要分得开，
    /// 选中行在悬停中也要分得开）。
    /// </summary>
    /// <remarks>
    /// 实测最紧的一处是"强调容器 vs 悬停底"（默认主题 1.175）；选中底在最深档上与悬停底的对比
    /// 有 1.33–1.38，永远不会撞上这一条。
    /// </remarks>
    public const double ContainerMinContrastOnHover = 1.06;

    /// <summary>
    /// **次按钮底**（<c>App.Surface.Control</c>：TonalButton / SoftPillButton / 计数药丸）对卡面的最低对比度。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么单独立一条</b>：这类面原先用"支撑槽**本色**"（<c>App.Support.Container</c>），
    /// 本色深浅随配色摆动 ⇒ 实测跨 1.02–1.97 两头都不合格 —— 青提本色 `#EDFFDB` 对卡面 **1.026**
    /// （ΔRGB 各 3 = 与命令栏胶囊重合，用户："底色与背景重合"）、宇治抹茶本色对页面底 **1.000**
    /// （逐字节同色）、藕粉灰绿 / 焦糖玫瑰本色对卡面 1.966（用户："四个工具栏按钮全部偏深"）。
    /// </para>
    /// <para>
    /// 1.60（2026-09-26 由 1.50 上调）：1.50 档下对卡面 1.50–1.52、**对页面底/面板仅 1.29–1.31**
    ///（右侧栏「打开」正画在面板上，这处最紧）—— 实测"按钮和背景差不多"（用户反馈，各主题一致）。
    /// 抬一档后：对卡面 1.60、对页面底/面板 1.37–1.38，容器字仍 8.3（≥7），离选中底 1.25（反而更分得开）。
    /// 走档取"**最浅**达标档"⇒ 上界天然锁在阈值附近，不会滑向更深（旧本色那一头）。
    /// </para>
    /// </remarks>
    public const double ControlSurfaceMinContrastOnCard = 1.60;

    /// <summary>
    /// 次按钮底允许下探的最深档（兜底：配色极端、一路够不到阈值时停在这里）。
    /// </summary>
    /// <remarks>实测最深的一支是出厂默认 T79.4 —— 地板 60 只防"永不达标"的死循环，正常取值远在它之上。</remarks>
    public const double ControlSurfaceToneFloor = 60.0;

    /// <summary>
    /// "背景色成员的色点看起来与页面底融合"的**对比度上限**（`SurfaceBaseOf` ② 路径的循环判据）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据落在渲染事实上：那个色点画在**卡面**上，而卡面由页面底提亮而来 ——
    /// 本色与卡面的对比度超过 1.08 时，色点看起来就没有融进卡面。
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
