using System.IO;
using LinkPocket.Theming;
using LinkPocket.Theming.Color;
using LinkPocket.Theming.Themes;
using LinkPocket.Theming.Tokens;
using LinkPocket.ViewModels;
using Material3.Core;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// **主题对比度护栏矩阵**（方案 §5.9）：11 套主题 × 9 对配对全部达标。
/// </summary>
/// <remarks>
/// <para>
/// 这是"按钮与小标志颜色奇怪"（旧 4 项 WCAG 不达标）的**机器化回归网**：
/// 任何改动令牌档位 / 派生公式 / 主题目录的动作，只要让某一对对比度跌破阈值，这里立刻红。
/// </para>
/// <para>
/// 阈值取自方案 §5.9：正文 ≥7、次要与弱文字 ≥4.5、图标 ≥3、容器字 ≥7、白字 ≥4.5。
/// </para>
/// </remarks>
public class ThemeContrastTests
{
    /// <summary>一条断言：取哪两个令牌、阈值多少、为什么。</summary>
    private sealed record Pair(string Label, Func<TokenTable, (Argb Fg, Argb Bg)> Pick, double Min, string Why);

    private static readonly Pair[] Matrix =
    {
        new("Text.OnAccent / Accent.Fill",
            t => (t.Token(AppTokens.TextOnAccent), t.Token(AppTokens.AccentFill)), 4.5, "主药丸白字（旧 #A18EB0 白字 = 3.00 ✗）"),
        new("Accent.Icon / Surface.Card",
            t => (t.Token(AppTokens.AccentIcon), t.Token(AppTokens.SurfaceCard)), 3.0, "强调图标（旧 AccentBtn 对 TintCard = 2.69 ✗）"),
        new("Accent.Text / Surface.Card",
            t => (t.Token(AppTokens.AccentText), t.Token(AppTokens.SurfaceCard)), 4.5, "强调文字 / 数字 / 计数"),
        new("Text.Primary / Surface.Card",
            t => (t.Token(AppTokens.TextPrimary), t.Token(AppTokens.SurfaceCard)), 7.0, "正文"),
        new("Text.Secondary / Surface.Card",
            t => (t.Token(AppTokens.TextSecondary), t.Token(AppTokens.SurfaceCard)), 4.5, "次要文字"),
        new("Text.Muted / Surface.Hover",
            t => (t.Token(AppTokens.TextMuted), t.Token(AppTokens.SurfaceHover)), 4.5, "弱文字对最暗内容底（旧 OnSurfaceMuted 对悬停底 = 3.29 ✗）"),
        new("Support.Icon / Surface.Card",
            t => (t.Token(AppTokens.SupportIcon), t.Token(AppTokens.SurfaceCard)), 3.0, "文件夹类型色（替代旧琥珀，对卡面 5.79 ✅）"),
        new("Text.OnContainer / Support.Container",
            t => (t.Token(AppTokens.TextOnContainer), t.Token(AppTokens.SupportContainer)), 7.0, "次强调药丸 / **删除类药丸**（与次操作共用）"),
        new("Text.OnContainer / Surface.Control",
            t => (t.Token(AppTokens.TextOnContainer), t.Token(AppTokens.SurfaceControl)), 7.0, "次按钮 / 计数药丸 / 侧栏「打开」的容器字（承载面 = 次按钮底）"),
        new("Text.OnContainer / Accent.Container",
            t => (t.Token(AppTokens.TextOnContainer), t.Token(AppTokens.AccentContainer)), 7.0, "导航 / 分段指示器 / 徽标 / chip 上的字（浅色强调容器）"),
        new("Text.Primary / Surface.Selected",
            t => (t.Token(AppTokens.TextPrimary), t.Token(AppTokens.SurfaceSelected)), 7.0, "选中行正文（列表行 / 树行选中的主文字）"),
        new("Text.Secondary / Surface.Selected",
            t => (t.Token(AppTokens.TextSecondary), t.Token(AppTokens.SurfaceSelected)), 4.5, "选中行次列文字（日期 / 计数这类在选中底上）"),
        // 选中行上的弱列（"从未"这类）确实会落在选中底上。**4.5 这条在这里与"对卡面 ≥1.22"互斥**
        //（越浅字越清楚、与卡面越不开），走档取的是卡面阈值 ⇒ 实测 4.44；棘轮按实测下界卡住，
        // 防"为了可见性把选中底再压深"（旧值 T76 时这一条只有 3.40）。
        // ⚠️ 棘轮保持 4.4：选中底色相改为**表面族色相**后，最紧的一套是青提（走档由「对卡面悬停 ≥1.06」
        // 钉在 T85.5）实测 **4.404** —— 留 0.004 余量，变动即红（这是棘轮该有的行为，故不预先放宽）。
        new("Text.Muted / Surface.Selected",
            t => (t.Token(AppTokens.TextMuted), t.Token(AppTokens.SurfaceSelected)), 4.4, "选中行弱列文字（棘轮：走档由『对卡面 ≥1.22』定，实测最低 4.404 = 青提）"),
        new("Support.Icon / Surface.Selected",
            t => (t.Token(AppTokens.SupportIcon), t.Token(AppTokens.SurfaceSelected)), 3.0, "文件夹图标落在选中行底上（列表里选中行 + 类型图标是常态组合）"),
        new("Accent.Icon / Surface.Selected",
            t => (t.Token(AppTokens.AccentIcon), t.Token(AppTokens.SurfaceSelected)), 3.0, "强调图标落在选中行底上"),
        new("Support.Icon / Accent.Container",
            t => (t.Token(AppTokens.SupportIcon), t.Token(AppTokens.AccentContainer)), 3.0, "图标落在浅色容器上（徽标 / 空态占位）"),
        new("Accent.Icon / Accent.Container",
            t => (t.Token(AppTokens.AccentIcon), t.Token(AppTokens.AccentContainer)), 3.0, "强调图标落在浅色容器上"),
        new("Text.Muted / Surface.Card",
            t => (t.Token(AppTokens.TextMuted), t.Token(AppTokens.SurfaceCard)), 4.5, "弱文字对卡面（提示 / 占位 / 主题卡摘要）"),
        new("Text.Secondary / Surface.Panel",
            t => (t.Token(AppTokens.TextSecondary), t.Token(AppTokens.SurfacePanel)), 4.5, "面板层（表头带 / 侧栏 / 状态栏）上的文字档：弱档 Muted 对面板只有 3.67 ⇒ 面板上只许用 Secondary 及以上"),
        new("Line.Invalid / Surface.Card",
            t => (t.Token(AppTokens.LineInvalid), t.Token(AppTokens.SurfaceCard)), 4.5, "校验错误描边（= 文字主色，2px）"),
    };

    public static IEnumerable<object[]> ThemeIds() =>
        ThemeCatalog.All.Select(t => new object[] { t.Id });

    [Fact]
    public void 对比度矩阵_11套主题_自动调色全部达标()
    {
        // 只守**自动调色**（缺省模式）：它按档位表重排，任何一套配色都必须可读。
        // 直配（Exact）**不参与可读性门槛**——用户口径："直配就是用户选什么颜色就什么颜色，
        // 必须忠实于用户；颜色实在出不来 / 实在不够，再说"——原色直接上屏，可读性由用户的配色自己负责。
        var failures = new List<string>();
        foreach (var mode in new[] { PaletteMode.Auto })
        {
            foreach (var theme in ThemeCatalog.All)
            {
                var table = PaletteSolver.Solve(theme with { PaletteMode = mode });
                foreach (var pair in Matrix)
                {
                    var (fg, bg) = pair.Pick(table);
                    var ratio = ColorMath.ContrastRatio(fg, bg);
                    if (ratio < pair.Min)
                        failures.Add($"[{mode}] {theme.Id} · {pair.Label} = {ratio:F2} < {pair.Min:F1}（{pair.Why}）");
                }
            }
        }
        Assert.True(failures.Count == 0, "对比度未达标：\n" + string.Join("\n", failures));
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void 每个主题_令牌完整性_颜色令牌与库键都齐备(string themeId)
    {
        var table = PaletteSolver.Solve(ThemeCatalog.Find(themeId)!);
        foreach (var token in AppTokens.AllColorTokens)
            Assert.True(table.Tokens.ContainsKey(token), $"{themeId} 缺应用令牌 {token}");
        foreach (var anchor in SurfaceAnchors.All)
            Assert.True(table.Anchored.ContainsKey(anchor.Key), $"{themeId} 缺库键 {anchor.Key}");
    }

    [Fact]
    public void 出厂默认主题_表面族随配色最浅色旋转_层感逐键恒定()
    {
        // 出厂默认**不再钉中性色相**——表面族色相 = 配色里最浅的成员；薄荷气泡水的最浅成员 #EFFFE0（H138.4）
        // ⇒ 旋转角 = 138.4 − 298.7 = −160.3°（≡ 199.7°）。旧判据"表面族逐字节等于改造前"随之作废。
        var table = PaletteSolver.Solve(ThemeCatalog.Default);
        var lightest = ThemeCatalog.Default.Palette.OrderByDescending(c => ColorMath.Measure(c).T).First();
        Assert.Equal(
            ColorMath.NormalizeHue(ColorMath.Measure(lightest).H - ThemeDefinition.ReferenceNeutralHue),
            table.SurfaceRotation, 3);

        // 层感恒定 = 逐键只转色相（明度保持、彩度只可能因色域被收窄，绝不被放大）：
        // 贴色域边界的色（如 InversePrimary，C≈40）旋转后彩度被钳，色相会跟着偏 1–3° —— 这是色彩空间的性质，
        // 不是派生公式的自由度（"只钳上限、绝不放大"）；色域内的色严格按旋转角走。
        foreach (var anchor in SurfaceAnchors.All.Where(a => a.Rotates && !IsSemanticOverride(a.Key)))
        {
            var before = ColorMath.Measure(anchor.Today);
            var after = ColorMath.Measure(table.Key(anchor.Key));
            Assert.Equal((anchor.Today.ToInt() >> 24) & 0xFF, (table.Key(anchor.Key).ToInt() >> 24) & 0xFF);

            // 近无彩（C→0）或纯白 / 近白（T→100）：HCT 的色相**不可观测**（怎么转都还是那个色，
            // 例如 OnTertiary = #FFFFFF，实测 C 2.9 / T 100）→ 只断言"没被改坏"
            if (before.C < 1.0 || before.T >= 99.5)
            {
                Assert.True(after.T >= 99.0, $"{anchor.Key} 不该被压暗：T {before.T:F1} → {after.T:F1}");
                continue;
            }

            Assert.True(Math.Abs(before.T - after.T) <= 1.5, $"{anchor.Key} 明度漂了：{before.T:F1} → {after.T:F1}");
            // 容差 1.0：色相旋转 + sRGB 色域钳制会让**近中性色**的实测彩度上下浮动 ~1
            //（默认主题色相改为 H309.8 后实测 TintCard 7.2 → 7.7）；真正的"加厚颜色"是成倍增长，照样抓得住。
            Assert.True(after.C <= before.C + 1.0, $"{anchor.Key} 彩度被放大了：{before.C:F1} → {after.C:F1}");

            // 色相：按旋转角走。容差不是"差不多就行"，而是 HCT↔sRGB **8 位往返**的量化下界：
            // 低彩度（C≈4 的表面族）与贴色域边界的色（InversePrimary C≈40）旋转后 RGB 几乎不变，
            // 反解出来的色相会偏 1–3°；旋转角越大漂得越明显（薄荷气泡水旋转 199.7°：InverseSurface C≈3 实测偏 5.4°）。
            // 真正的回归（旋转没生效）偏离 = 整个旋转角（≈200°），照样抓得住。
            var expectedHue = ColorMath.NormalizeHue(before.H + table.SurfaceRotation);
            var hueTolerance = before.C < 5.0 ? 8.0 : 5.0;
            Assert.True(ColorMath.HueDistance(expectedHue, after.H) <= hueTolerance,
                $"{anchor.Key} 没按旋转角走：期望 H{expectedHue:F1}，实际 H{after.H:F1}（彩度 {before.C:F1} → {after.C:F1}）");
        }

        // 不旋转键（零消费语义族 / 无彩常量）保持库基线
        foreach (var anchor in SurfaceAnchors.All.Where(a => !a.Rotates))
            Assert.Equal(anchor.Today.ToInt(), table.Key(anchor.Key).ToInt());
    }

    /// <summary>
    /// 方案**有意**覆写的库键（不再是"今天的值"）：强调族 / 次强调族 / 文字三档 / 描边 / SurfaceTint。
    /// 出厂默认主题有意覆写这些语义，其余键保持逐字节不变。
    /// </summary>
    private static bool IsSemanticOverride(string key) =>
        key is "Primary" or "OnPrimary" or "PrimaryContainer" or "OnPrimaryContainer" or "SurfaceTint"
            or "Secondary" or "OnSecondary" or "SecondaryContainer" or "OnSecondaryContainer"
            or "OnSurface" or "OnSurfaceVariant" or "OnSurfaceMuted"
            or "Outline" or "OutlineVariant";

    [Fact]
    public void 出厂默认主题_令牌等于配色直配的实测值_且已发布到界面()
    {
        // 口径：直配（开关关闭）= **成员色原样上屏**（用户口径："必须忠实于用户"）—— 强调填充 / 强调容器 /
        // 支撑 / 图标 / 描边弱档直取配色成员原色；**页面底例外**：本色明度夹进 **87–91 深度档**
        //（用户实测退回：纯原色当背景整页过亮——这档属"颜色实在出不来"的校准面）。
        // 没有成员的派生面（卡面 / 悬停 / 选中 / 次按钮底 / 浅带层）按本色推档；唯一自适应的是"墨"
        //（白字 / 本色深墨取更优者 —— 填充、开关滑块都吃它）。本用例按 **Exact** 口径核对逐键实测值。
        // ⚠️ 按**主题定义**求解，不读 `ThemeService.DerivedTable`（进程级共享状态，别的测试类会并行改它）。
        // 出厂默认 = 薄荷气泡水（2026-10-14 起）：深青 / 气泡青 / 薄荷 / 米白 四枚梯度。
        var t = PaletteSolver.Solve(ThemeCatalog.Default with { PaletteMode = PaletteMode.Exact });
        Assert.Equal(0x002429u, Rgb(t.Token(AppTokens.TextPrimary)));      // ← 色1 #17434A 深青（最深成员的墨，压到正文档）
        Assert.Equal(0x2C4C51u, Rgb(t.Token(AppTokens.TextSecondary)));    // ← 同一个墨提亮到次文档（直配：不换成灰）
        Assert.Equal(0x446369u, Rgb(t.Token(AppTokens.TextMuted)));        // ← 同上，弱文档
        // 强调填充 = **色2 #8ED6DE（彩度最高）原样上屏**（不再加深到填充档 —— 直配的核心行为）；
        // 填充上的墨自适应：原色撑不住白字 → 换**本色深墨**（#8ED6DE 上白字只有 1.5，深墨 9+）。
        Assert.Equal(0x8ED6DEu, Rgb(t.Token(AppTokens.AccentFill)));
        Assert.Equal(0x8ED6DEu, Rgb(t.Token(AppTokens.AccentIcon)));       // ← 图标 = 填充原色（一字不改）
        Assert.Equal(0x8ED6DEu, Rgb(t.Token(AppTokens.TypeLink)));
        Assert.Equal(0x002B2Eu, Rgb(t.Token(AppTokens.TextOnAccent)));     // ← 本色深墨（T15；开关滑块也用它）
        Assert.Equal(0x004F55u, Rgb(t.Token(AppTokens.AccentText)));       // ← 强调文字 = 原色色相压到文字档（对卡面 ≥8）
        // 强调容器 = 色4 #EFFFE0 **原样**（直配连"与页面底分得开"的可见性提亮也不做 —— 忠实优先）。
        Assert.Equal(0xEFFFE0u, Rgb(t.Token(AppTokens.AccentContainer)));
        // 选中底 / 落点高亮 = 派生面（没有成员）：锚在真实承载面（**卡面**）上取"够得开阈值的最浅档"。
        Assert.Equal(0xD1E0C4u, Rgb(t.Token(AppTokens.SurfaceSelected)));
        // 次按钮底（TonalButton / SoftPillButton / 计数药丸）= 表面族色相 + 提高一大档的彩度（去灰），
        // 取"对卡面 ≥1.60 的最浅达标档"（派生面：颜色不够 → 按本色算）。
        Assert.Equal(0xACCE97u, Rgb(t.Token(AppTokens.SurfaceControl)));
        // 悬停三层 = **承载面自己压深一档**（派生面）。直配的弱文字取值与自动不同（直配 = 本色墨提亮），
        // 悬停档按"弱文字对它 ≥4.5"反推 ⇒ 与自动可差一档（两层逐支实测钉住）。
        Assert.Equal(0xCDDCBEu, Rgb(t.Token(AppTokens.SurfaceHover)));
        Assert.Equal(0xDBEACCu, Rgb(t.Token(AppTokens.SurfaceCardHover)));
        Assert.Equal(0xC5D8B6u, Rgb(t.Token(AppTokens.SurfaceBandHover)));
        // 浅带层（表头带 + 侧区面板 / 状态栏 / 徽标底）：派生面（无成员）→ 明度分层（压深 + 彩度补偿）。
        Assert.Equal(0xD1E4C2u, Rgb(t.Token(AppTokens.SurfaceHeaderBand)));
        Assert.Equal(0xD1E4C2u, Rgb(t.Token(AppTokens.SurfacePanel)));
        // 容器字 = 容器底上读得清的深档（唯一真值：`App.Text.OnContainer` 同时服务强调容器与次强调容器）。
        Assert.Equal(0x002C23u, Rgb(t.Token(AppTokens.TextOnContainer)));
        // ← 支撑槽 = 色1 #BCF1E0 **原样**（容器 / 图标 / 文件夹色一字不改 —— 直配不再"够浅才能用"）。
        Assert.Equal(0xBCF1E0u, Rgb(t.Token(AppTokens.SupportContainer)));
        Assert.Equal(0xBCF1E0u, Rgb(t.Token(AppTokens.SupportIcon)));
        Assert.Equal(0xBCF1E0u, Rgb(t.Token(AppTokens.TypeFolder)));
        // 描边档（取自"最接近中间调"的成员）；描边的"弱"档直配 = 该成员本色（T≥80 不再提亮）。
        Assert.Equal(0x61A9B1u, Rgb(t.Token(AppTokens.LineOutline)));
        Assert.Equal(0x8ED6DEu, Rgb(t.Token(AppTokens.LineVariant)));
        // 页面底 = 本色明度**夹进 87–91 深度档**（两种模式一致——纯原色当背景整页过亮，用户实测退回）；
        // 主题卡那枚"背景色"色点显示的就是**这个值**（`SurfaceBaseColor` 同源 → 逐字节同色 = 融合）。
        Assert.Equal(0xDBEACCu, Rgb(t.Token(AppTokens.SurfaceBase)));
        // 卡面 = 页面底提亮 6 档（档距 6 是"卡面对页面底 ≥1.15"实测选定的取值）。
        Assert.Equal(0xECFBDDu, Rgb(t.Token(AppTokens.SurfaceCard)));

        // 悬停三层在**自动调色**下的实测值（缺省模式，口径与改动前逐字节一致）：逐模式各钉一支才有意义。
        var ta = PaletteSolver.Solve(ThemeCatalog.Default);
        Assert.Equal(0xD0DFC1u, Rgb(ta.Token(AppTokens.SurfaceHover)));
        Assert.Equal(0xDBEACCu, Rgb(ta.Token(AppTokens.SurfaceCardHover)));
        Assert.Equal(0xC5D8B6u, Rgb(ta.Token(AppTokens.SurfaceBandHover)));
    }

    [Fact]
    public void 配色应用方式_直配原样用成员色_自动按档位重排()
    {
        // 缺省 = 自动调色（开关打开）。
        Assert.Equal(PaletteMode.Auto, ThemeService.DefaultPaletteMode);
        Assert.Equal(PaletteMode.Auto, new ThemeDefinition
        {
            Id = "t", Source = ThemeSource.UserDefined, Palette = ThemeCatalog.Default.Palette,
        }.PaletteMode);

        var theme = ThemeCatalog.Default;
        var exact = PaletteSolver.Solve(theme with { PaletteMode = PaletteMode.Exact });
        var auto = PaletteSolver.Solve(theme with { PaletteMode = PaletteMode.Auto });

        // ① 直配 = **成员色原样上屏**（用户口径："原来的颜色硬拼到每一块上"）：强调填充 = 最彩成员原色，
        //    一字不改；页面底例外——**两种模式都夹 87–91 明度档**（用户实测退回：纯原色当背景整页过亮，
        //    这档属"颜色实在出不来"的校准面；只动明度，色相与彩度仍来自配色）。
        var lightest = theme.Palette.OrderByDescending(c => ColorMath.Measure(c).T).First();
        var mostChromatic = theme.Palette.OrderByDescending(c => ColorMath.Measure(c).C)
            .ThenBy(c => ColorMath.Measure(c).T).First();
        Assert.Equal(mostChromatic.ToInt(), exact.Token(AppTokens.AccentFill).ToInt());
        Assert.NotEqual(mostChromatic.ToInt(), auto.Token(AppTokens.AccentFill).ToInt());
        Assert.Equal(exact.Token(AppTokens.SurfaceBase).ToInt(), auto.Token(AppTokens.SurfaceBase).ToInt());
        Assert.NotEqual(lightest.ToInt(), exact.Token(AppTokens.SurfaceBase).ToInt());   // 背景不是裸原色（明度夹档）

        // ② 直配的墨跟着填充**自适应**（原色撑不住白字就换本色深墨 —— 颜色不动、墨来适配）；
        //    自动调色的填充固定撑白字。
        Assert.True(ColorMath.ContrastRatio(exact.Token(AppTokens.TextOnAccent), exact.Token(AppTokens.AccentFill)) >= 4.5,
            "直配：填充上的墨必须读得清（白字或本色深墨，二者取其一）");
        Assert.Equal(0xFFFFFFu, Rgb(auto.Token(AppTokens.TextOnAccent)));

        // ③ 两种模式的文字两档取值不同（直配 = 同一个墨提亮；自动 = 中性灰墨），各自钉住：
        //    自动侧带一点表面族色相（表面色相 = 薄荷气泡水的 H138.4）；直配侧钉在"直配实测值"用例里。
        Assert.NotEqual(exact.Token(AppTokens.TextSecondary).ToInt(), auto.Token(AppTokens.TextSecondary).ToInt());
        Assert.Equal(0x464742u, Rgb(auto.Token(AppTokens.TextSecondary)));
        Assert.Equal(0x5D5F5Au, Rgb(auto.Token(AppTokens.TextMuted)));

        // ④ 融合在**两种模式下都成立**（开关关闭或打开，主题卡色点都必须能融合）
        foreach (var mode in new[] { PaletteMode.Exact, PaletteMode.Auto })
        {
            var table = PaletteSolver.Solve(theme with { PaletteMode = mode });
            var baseColor = table.Token(AppTokens.SurfaceBase);
            var surface = PaletteSolver.SurfaceBaseColor(theme with { PaletteMode = mode });
            Assert.Equal(baseColor.ToInt(), surface.ToInt());   // 卡面取材 = 实际页面底
            Assert.True(ColorMath.ContrastRatio(surface, baseColor) <= 1.01,
                $"[{mode}] 背景色成员与页面底必须同色（融合）");
        }
    }

    [Fact]
    public void 界面用的颜色_必须能在用户给的调色板里找到()
    {
        // 口径：优先应用选中的这几个颜色本身，而不是"深一点浅一点"。
        // 旧模型只取 (H, C) 按档位表重建，于是宇治抹茶的 4 个青绿在界面上变成灰绿 + 粉紫
        // （`#EEDDF7` 那种配色里根本不存在的颜色，观感偏粉）。
        //
        // 判据：**每个界面色都必须与某个配色成员同色相**（±8°，HCT↔sRGB 8 位往返 + 低彩度下色相反解的量化波动）。
        // 允许压暗/提亮（保可读性的必要手段），但不许换成另一个颜色。
        const double HueTolerance = 8.0;
        var failures = new List<string>();
        var appTokens = new[]
        {
            AppTokens.SurfaceBase, AppTokens.SurfaceCard, AppTokens.SurfaceHover, AppTokens.SurfaceSelected,
            AppTokens.SurfaceControl,
            AppTokens.SurfaceTintCard, AppTokens.SurfaceHeaderBand, AppTokens.SurfacePanel,
            AppTokens.AccentFill, AppTokens.AccentText,
            AppTokens.AccentContainer, AppTokens.SupportContainer, AppTokens.SupportIcon,
            AppTokens.TypeFolder, AppTokens.TypeLink, AppTokens.LineOutline, AppTokens.LineVariant,
        };
        foreach (var theme in ThemeCatalog.All)
        {
            var t = PaletteSolver.Solve(theme);
            var palette = theme.Palette.Select(c => ColorMath.Measure(c)).ToList();
            foreach (var token in appTokens)
            {
                var m = ColorMath.Measure(t.Token(token));
                if (m.C < 3.0) continue;   // 近无彩色的色相不可观测（容器字这类深墨）
                if (!palette.Any(p => ColorMath.HueDistance(p.H, m.H) <= HueTolerance))
                    failures.Add($"{theme.Id} · {token} = #{t.Token(token).ToInt() & 0x00FFFFFF:X6}（H{m.H:F0}）"
                                 + $" 在配色里找不到同色相成员（配色色相：{string.Join("/", palette.Select(p => p.H.ToString("F0")))}）");
            }
        }
        Assert.True(failures.Count == 0, "界面色与用户配色不同源（= 又「发明」了颜色）：\n" + string.Join("\n", failures));
    }

    [Fact]
    public void 配色角色分配_彩度降序占槽_明度最高者管表面()
    {
        // 不再分族、不再发明色相——配色成员按彩度降序占槽，
        // 明度最高的成员决定表面族与文字墨。这条把"哪个颜色管哪一块"钉死。
        var def = ThemeCatalog.Default;
        var families = PaletteSolver.SolveFamilies(def);
        var measured = def.Palette
            .Select(c => ColorMath.Measure(c))
            .ToList();
        var byChroma = measured.OrderByDescending(m => m.C).ThenBy(m => m.T).ToList();
        var lightest = measured.OrderByDescending(m => m.T).First();

        Assert.Equal(byChroma[0].H, families.AccentHue, 1);
        Assert.Equal(Math.Min(byChroma[0].C, 36.0), families.AccentChroma, 1);
        Assert.Equal(byChroma[1].H, families.SupportHue, 1);
        Assert.Equal(byChroma[2].H, families.ContainerHue, 1);
        Assert.Equal(byChroma[3].H, families.NeutralVariantHue, 1);
        Assert.Equal(lightest.H, families.NeutralHue, 1);
        Assert.False(families.SupportIsDerived, "5 色配色不该走派生的支撑槽");
    }

    [Fact]
    public void 配色成员_每一个都有出口_去掉任一个都会改变界面()
    {
        // "给出的 4/5 个颜色要全部用上"的机器化判据 = **逐槽 leave-one-out**：
        // 去掉任一个身份色，至少有一个语义令牌变值。
        // （旧模型实测：默认主题 5 色里 3 个去掉后 0 个令牌变化 —— ）
        var baseline = PaletteSolver.Solve(ThemeCatalog.Default);
        for (var slot = 0; slot < ThemeCatalog.Default.Palette.Count; slot++)
        {
            var palette = ThemeCatalog.Default.Palette.Where((_, i) => i != slot).ToArray();
            var mutated = PaletteSolver.Solve(new ThemeDefinition
            {
                Id = "probe",
                Source = ThemeSource.UserDefined,
                Palette = palette,
            });
            var changed = AppTokens.AllColorTokens.Count(t => baseline.Token(t) != mutated.Token(t));
            Assert.True(changed > 0, $"去掉第 {slot + 1} 个身份色后界面毫无变化 —— 这个颜色没有出口");
        }
    }

    [Fact]
    public void 界面色相_全部来自配色本身()
    {
        // "不再延续发明色相的思路"：4/5 色配色的强调 / 支撑 / 容器 / 描边 / 表面色相必须是配色成员的色相之一（±1°）。
        foreach (var def in new[] { ThemeCatalog.Default }.Concat(ThemeCatalog.Presets.Where(p => p.Palette.Count >= 4)))
        {
            var hues = def.Palette.Select(c => ColorMath.Measure(c).H).ToList();
            var f = PaletteSolver.SolveFamilies(def);
            foreach (var (label, hue) in new[]
                     {
                         ("强调", f.AccentHue), ("支撑", f.SupportHue), ("强调容器", f.ContainerHue),
                         ("描边", f.NeutralVariantHue), ("表面", f.NeutralHue),
                     })
            {
                Assert.True(hues.Any(h => ColorMath.HueDistance(h, hue) <= 1.0),
                    $"{def.Id} 的{label}色相 H{hue:F1} 不在配色里（配色色相：{string.Join(" / ", hues.Select(h => h.ToString("F1")))}）");
            }
        }
    }

    [Fact]
    public void 页面底_夹在明度档内_且卡面与选中底都看得见()
    {
        // 回归现象与对应口径：
        //  ① 界面底色被整体改动后发灰（虽然融合）→ 页面底不能一味压深（层感会塌）；
        //  ② 大量颜色发灰、许多颜色无法融合 → 融合不能只靠"本色原样"；
        //  ③ 灰度过重、卡片贴脸看不出层级 → 页面底要有深度（87–91），卡面必须明显浮起来（≥1.15）。
        //
        // 现行 = 深度档 87–91（`SurfaceBaseOf`：本色落在档内原样用、更浅的压到 91、更深的提到 87）
        //        + 融合由"主题卡色点显示实际生效页面底"承担（见 `主题卡色点_…` 用例）
        //        + 卡面档距 = 6（`SurfaceCardLift`：5 档实测只有 1.135–1.138，够不到本用例的门槛）。
        // 四条判据（全部按实测对比度，不靠感觉）：
        //  ① 页面底明度落在 87–91；
        //  ② 卡面浮得起来（对页面底 **≥1.15**，实测 1.165–1.169）；
        //  ③ 选中底 / 落点高亮看得见 —— **对卡面 ≥1.22**（列表行画在卡面上，这条最硬；实测 1.767–1.788）
        //     + 对页面底 ≥1.08 + 对悬停底 ≥1.06；
        //     强调容器（导航 / 分段 / 徽标 / chip 的浅色底）另算：承载面是**页面底与悬停底**，
        //     判据 = 对页面底 ≥1.08、对悬停底 ≥1.06（实测 1.085–1.215 / 1.175–1.388）。
        //  ④ 背景色成员与页面底**同色相**（近融，容差见下）。
        const double MinCardOnBase = 1.15;
        var failures = new List<string>();
        foreach (var theme in ThemeCatalog.All)
        {
            var table = PaletteSolver.Solve(theme);
            var baseColor = table.Token(AppTokens.SurfaceBase);
            var card = table.Token(AppTokens.SurfaceCard);
            var hover = table.Token(AppTokens.SurfaceHover);
            var container = table.Token(AppTokens.AccentContainer);
            var selected = table.Token(AppTokens.SurfaceSelected);
            var baseTone = ColorMath.Measure(baseColor).T;

            if (baseTone < PaletteSolver.SurfaceBaseToneMin - 0.6 || baseTone > PaletteSolver.SurfaceBaseToneMax + 0.6)
                failures.Add($"{theme.Id} 页面底明度 T{baseTone:F1} 越出 {PaletteSolver.SurfaceBaseToneMin}–{PaletteSolver.SurfaceBaseToneMax}");

            var cardRatio = ColorMath.ContrastRatio(card, baseColor);
            if (cardRatio < MinCardOnBase)
                failures.Add($"{theme.Id} 卡面对页面底 {cardRatio:F3} < {MinCardOnBase}（卡片看不出是卡片）");

            var selectedOnCard = ColorMath.ContrastRatio(selected, card);
            if (selectedOnCard < PaletteSolver.ContainerMinContrastOnCard)
                failures.Add($"{theme.Id} 选中底对**卡面** {selectedOnCard:F3} < {PaletteSolver.ContainerMinContrastOnCard}"
                             + "（选中行与未选中行几乎同色——选中行画在卡面上，这条才是承载面）");

            var selectedOnBase = ColorMath.ContrastRatio(selected, baseColor);
            if (selectedOnBase < PaletteSolver.ContainerMinContrastOnBase)
                failures.Add($"{theme.Id} 选中底对页面底 {selectedOnBase:F3} < {PaletteSolver.ContainerMinContrastOnBase}（选中行看不出来）");

            // 悬停 = **承载面自己压深一档**（三层各一支令牌）。两条一起量：
            //  ① 压深要看得见（对承载面 ≥1.03，太小 = "鼠标动没动看不出来"）；
            //  ② 色相与彩度必须**留在承载面那一族**（Δ色相 ≤3°、Δ彩度 ≤3）——
            //     否则画出来的不是"这一条加深"，而是"跳了一块别的颜色"（实测缺陷：表头带换成弱撞色档后
            //     表头悬停仍取页面底那支 `Hover`（H298 / 比带深 3 档），屏幕上是一颗灰紫药丸）。
            void CheckHoverLayer(string token, Argb carrier, Argb hover, double minOnCarrier)
            {
                var onCarrier = ColorMath.ContrastRatio(hover, carrier);
                if (onCarrier < minOnCarrier)
                    failures.Add($"{theme.Id} {token} 对承载面 {onCarrier:F3} < {minOnCarrier}（悬停反馈看不出来）");
                if (ColorMath.Measure(hover).T >= ColorMath.Measure(carrier).T)
                    failures.Add($"{theme.Id} {token} 没有比承载面更浅（悬停必须是**压深**一档）");
                var cm = ColorMath.Measure(carrier);
                var hm = ColorMath.Measure(hover);
                if (ColorMath.HueDistance(cm.H, hm.H) > 3.0)
                    failures.Add($"{theme.Id} {token} 色相 H{hm.H:F1} 离开承载面 H{cm.H:F1}（差 {ColorMath.HueDistance(cm.H, hm.H):F1}°）"
                                 + "= 悬停画成了另一支颜色，不是本层加深");
                if (Math.Abs(cm.C - hm.C) > 3.0)
                    failures.Add($"{theme.Id} {token} 彩度 C{hm.C:F1} 偏离承载面 C{cm.C:F1} 超过 3（同层加深不许顺手改彩度）");
            }
            var cardHover = table.Token(AppTokens.SurfaceCardHover);
            var bandHover = table.Token(AppTokens.SurfaceBandHover);
            CheckHoverLayer("卡面悬停底", card, cardHover, 1.03);
            CheckHoverLayer("浅带悬停底", table.Token(AppTokens.SurfaceHeaderBand), bandHover, 1.05);

            // 选中行与悬停行画在**同一块卡**上 ⇒ 邻居是卡面悬停底（不是页面底那一支悬停）
            var selectedOnCardHover = ColorMath.ContrastRatio(selected, cardHover);
            if (selectedOnCardHover < PaletteSolver.ContainerMinContrastOnHover)
                failures.Add($"{theme.Id} 选中底对**卡面悬停底** {selectedOnCardHover:F3} < {PaletteSolver.ContainerMinContrastOnHover}"
                             + "（鼠标划过选中行时看不出「哪一行是选中的」）");

            // 强调容器：画在页面底 / 悬停底上（导航指示器、分段指示器、徽标、chip）——
            // 它必须比页面底与悬停底都看得出来，否则"指示器与底同色"（这类面没有卡面那层提亮）。
            var containerOnBase = ColorMath.ContrastRatio(container, baseColor);
            if (containerOnBase < PaletteSolver.ContainerMinContrastOnBase)
                failures.Add($"{theme.Id} 强调容器对页面底 {containerOnBase:F3} < {PaletteSolver.ContainerMinContrastOnBase}（指示器/徽标看不见）");

            var containerOnHover = ColorMath.ContrastRatio(container, hover);
            if (containerOnHover < PaletteSolver.ContainerMinContrastOnHover)
                failures.Add($"{theme.Id} 强调容器对悬停底 {containerOnHover:F3} < {PaletteSolver.ContainerMinContrastOnHover}");

            // 浅带层（表头带 + 侧区面板 / 状态栏 / 徽标底）分层手段**按贴不贴得上强调槽色相二选一**：
            // 两枚键同值这件事本身要被机器卡住（改档时不会只改一边）。
            //  ① 贴上（邻窗内）⇒ **色相分层**：页面底本色档（两支近邻色相分开，带/底 ≤1.03）；
            //     贴不上 ⇒ 与页面底同色相，只能**明度分层**：压深 + 彩度补偿，带/底 ∈ [1.05, 1.15]
            //     （下限 = 看得出"这是一条带"；上限 = 不压成灰块——阈值是下限不是越狠越好）；
            //  ② 带对卡面 ≥1.06（"这是一条带"读得出来；实测贴上 1.165–1.170 / 压深 1.24–1.28）；
            //  ③ 色相**只贴不插**：强调槽与表面槽相邻（≤ 邻窗）时带贴到强调槽那一支、色相差 = 槽位差；
            //     不相邻时带与页面底**同色相**（宁可不撞，也不发明配色里不存在的色相）；
            //     实测贴上的三套 = 紫罗兰 13.2 / 宇治抹茶 14.8 / 蓝莓酸奶 13.4，其余八套 = 0；
            //  ④ 浅带**自己的**悬停档压在带上 ≥1.05（表头悬停 = 本层压深一档；由 CheckHoverLayer 量）。
            const double BandMinContrastOnCard = 1.06;
            // 邻窗的产品上限（写死在这里 = 棘轮）：把 `SurfaceBandHueNeighbourMax` 抬大会让"弱撞色"
            // 变成真撞色（樱花白脱的表面槽与强调槽差 162.9°），越界即判红。
            const double BandHueNeighbourWindowCap = 16.0;
            var band = table.Token(AppTokens.SurfaceHeaderBand);
            var panel = table.Token(AppTokens.SurfacePanel);
            if (band.ToInt() != panel.ToInt())
                failures.Add($"{theme.Id} 表头带 {band.ToInt() & 0xFFFFFF:X6} 与面板层 {panel.ToInt() & 0xFFFFFF:X6} 不同值"
                             + "（同一条浅带必须是同一个值：改档只改一处）");

            var bandOnCard = ColorMath.ContrastRatio(band, card);
            if (bandOnCard < BandMinContrastOnCard)
                failures.Add($"{theme.Id} 表头带对卡面 {bandOnCard:F3} < {BandMinContrastOnCard}（表头带看不出来）");

            // 邻窗上限（棘轮）：`SurfaceBandHueNeighbourMax` 抬大会让"弱撞色"变成真撞色
            //（樱花白脱的表面槽与强调槽差 162.9°），越界即判红。经本地变量比较，避免常量折叠成不可达分支。
            var bandHueWindow = PaletteSolver.SurfaceBandHueNeighbourMax;
            if (bandHueWindow > BandHueNeighbourWindowCap)
                failures.Add($"{theme.Id} 浅带层邻窗 {bandHueWindow:F1}° > 上限 {BandHueNeighbourWindowCap:F1}°（弱撞色会变成两个色）");

            const double HueRoundTripTolerance = 2.0;   // HCT↔sRGB 8 位往返会让实测色相偏 1–2°
            var fam = PaletteSolver.SolveFamilies(theme);
            var bandHue = ColorMath.Measure(band).H;
            var bandTone = ColorMath.Measure(band).T;
            var bandOnBase = ColorMath.ContrastRatio(band, baseColor);
            var slotGap = ColorMath.HueDistance(fam.NeutralHue, fam.AccentHue);
            if (slotGap <= PaletteSolver.SurfaceBandHueNeighbourMax)
            {
                // 相邻 ⇒ 带的色相**就是强调槽那一支成员**，不是两支色相中间插出来的新色相
                var offAccent = ColorMath.HueDistance(bandHue, fam.AccentHue);
                if (offAccent > HueRoundTripTolerance)
                    failures.Add($"{theme.Id} 强调槽与表面槽差 {slotGap:F1}°（相邻）⇒ 表头带应贴到强调槽 H{fam.AccentHue:F1}，"
                                 + $"实得 H{bandHue:F1}（差 {offAccent:F1}°）");
                // 分层 = 色相：这一支停在页面底本色档（同档同彩、只换色相），动明度会改变既定配色观感
                if (bandOnBase > 1.03)
                    failures.Add($"{theme.Id} 贴色主题表头带对页面底 {bandOnBase:F3} > 1.03（色相分层的一支 = 页面底本色档）");
                if (bandTone > baseTone + 0.6 || bandTone < PaletteSolver.SurfaceBaseToneMin - 0.6)
                    failures.Add($"{theme.Id} 表头带 T{bandTone:F1} 不在 [87, 页面底 {baseTone:F1}] 内（贴色主题 = 页面底本色档）");
            }
            else
            {
                // 不相邻 ⇒ 带不换色相，与页面底同支
                var offNeutral = ColorMath.HueDistance(bandHue, fam.NeutralHue);
                if (offNeutral > HueRoundTripTolerance)
                    failures.Add($"{theme.Id} 强调槽与表面槽差 {slotGap:F1}°（不相邻）⇒ 表头带必须与页面底同色相 H{fam.NeutralHue:F1}，"
                                 + $"实得 H{bandHue:F1}（差 {offNeutral:F1}° = 发明了配色里没有的色相）");
                // 分层 = 明度（同色相同彩度同明度 = 与页面底逐字节重合 = 融成一块）：压深档，窗口两头卡
                if (bandOnBase is < 1.05 or > 1.15)
                    failures.Add($"{theme.Id} 表头带对页面底 {bandOnBase:F3} 不在 [1.05, 1.15]"
                                 + "（同色相主题只能明度分层：<1.05 = 融进页面底，>1.15 = 压成灰块）");
                if (bandTone >= baseTone)
                    failures.Add($"{theme.Id} 表头带 T{bandTone:F1} 未压在页面底 T{baseTone:F1} 之下（明度分层 = 压深档，不走提亮）");
            }
            var bandHueGap = ColorMath.HueDistance(bandHue, ColorMath.Measure(baseColor).H);
            if (bandHueGap > PaletteSolver.SurfaceBandHueNeighbourMax + HueRoundTripTolerance)
                failures.Add($"{theme.Id} 表头带与页面底色相差 {bandHueGap:F1}° > "
                             + $"{PaletteSolver.SurfaceBandHueNeighbourMax + HueRoundTripTolerance:F1}°（弱撞色撞成了两个色）");

            // ④（浅带悬停 = 本层压深一档且看得见、色相不逃）由上方 `CheckHoverLayer("浅带悬停底", …)` 一次量完。
        }
        Assert.True(failures.Count == 0, "表面族层次未达标：\n" + string.Join("\n", failures));
    }

    [Fact]
    public void 次按钮底_每套主题都稳定可见_且与表面族同色调()
    {
        // 底 = 表面族色相 + 表面族彩度，锚 = "对**卡面** ≥ `ControlSurfaceMinContrastOnCard` 的最浅达标档"。
        // 为什么单独立一条：这类面（TonalButton / SoftPillButton / 计数药丸）原先用支撑槽**本色**，
        // 实测跨 1.02–1.97 两头都不合格 —— 青提本色对卡面 1.026（ΔRGB 各 3 = 与命令栏胶囊重合）、
        // 宇治抹茶本色对页面底 1.000（逐字节同色）、藕粉灰绿 / 焦糖玫瑰本色对卡面 1.966（一整块重色）。
        // 三条判据分别对应三种**真实承载面**：
        //  ① 对卡面 ≥ 1.50（命令栏浮动胶囊里的四枚药丸；这条同时是走档的锚）；
        //  ② 对面板 ≥ 1.15（右侧栏「打开」画在 `Surface.Panel` 上，实测最紧 1.201）；
        //  ③ 对选中行底 ≥ 1.06（计数药丸画在选中行上，实测最紧 1.116 —— 低于它药丸形状就没了）；
        //  ④ 容器字 ≥7 由上面的矩阵管；色相必须留在表面族（≤3°，否则"控件面"会跳成另一个颜色）。
        //     ⚠️ 判据锚在**表面族的请求色相**（`SolveFamilies`），不是卡面的**实测**色相：
        //     卡面画在 T97 / 近无彩的那一头，8 位量化会把它的实测色相带偏（实测藕粉灰绿：请求 H49.4
        //     → 卡面实测 H57.5，差 8°；而次按钮底实测只差 1.7°）—— 拿卡面当基准会把量化噪声当成回归。
        //     近无彩（C<3）时色相不可观测，按本仓惯例跳过。
        const double MinOnPanel = 1.15;
        const double MinOnSelected = 1.06;
        const double HueTolerance = 3.0;
        const double UnobservableChroma = 3.0;
        var failures = new List<string>();
        foreach (var theme in ThemeCatalog.All)
        {
            var t = PaletteSolver.Solve(theme);
            var control = t.Token(AppTokens.SurfaceControl);
            var card = t.Token(AppTokens.SurfaceCard);
            var panel = t.Token(AppTokens.SurfacePanel);
            var selected = t.Token(AppTokens.SurfaceSelected);

            var onCard = ColorMath.ContrastRatio(control, card);
            if (onCard < PaletteSolver.ControlSurfaceMinContrastOnCard)
                failures.Add($"{theme.Id} 次按钮底对卡面 {onCard:F3} < {PaletteSolver.ControlSurfaceMinContrastOnCard:F2}"
                             + "（与命令栏胶囊重合 —— 本色那一支实测只有 1.026）");

            var onPanel = ColorMath.ContrastRatio(control, panel);
            if (onPanel < MinOnPanel)
                failures.Add($"{theme.Id} 次按钮底对面板 {onPanel:F3} < {MinOnPanel}（右侧栏「打开」看不见）");

            var onSelected = ColorMath.ContrastRatio(control, selected);
            if (onSelected < MinOnSelected)
                failures.Add($"{theme.Id} 次按钮底对选中行底 {onSelected:F3} < {MinOnSelected}"
                             + "（计数药丸在选中行上丢掉形状）");

            var measured = ColorMath.Measure(control);
            if (measured.C < UnobservableChroma) continue;
            var fam = PaletteSolver.SolveFamilies(theme);
            var familyHue = fam.SurfaceSource is { } ss ? ColorMath.Measure(ss).H : fam.NeutralHue;
            var offHue = ColorMath.HueDistance(measured.H, familyHue);
            if (offHue > HueTolerance)
                failures.Add($"{theme.Id} 次按钮底色相 H{measured.H:F1} 离开表面族 H{familyHue:F1}（差 {offHue:F1}°"
                             + " = 控件面跳成了另一个颜色，不再是同色调的浅色控件面）");
        }
        Assert.True(failures.Count == 0, "次按钮底未达标：\n" + string.Join("\n", failures));
    }

    [Fact]
    public void 主题卡色点_背景色成员与页面底融合_且色点数等于设计档色数()
    {
        // 融合口径（设计档「配色方案.txt」）：
        //  ① 背景色那个圆必须与背景融合 —— 融合 = 配色里的**背景色成员**（明度最高者）与表面族是同一个颜色；
        //     旧实现给每个预设钉了 `NeutralHueOverride`，把表面族带离了那个成员
        //     （默认主题实测：最浅色点 `#F2EEF5` 对页面底 `#E8E4ED` = **1.09**，看得出两块）。
        //  ② 设计档第 1–4 套是 **5 色**、第 5–10 套是 **4 色**，而旧实现每套只收了 1–2 个身份色、
        //     再补位凑到 4（五色被压成四色）。
        //
        // 判据（页面底压到 87–91 深度档之后，"本色原样"不再是融合的判据 ——
        // 融合落在**显示口径**上：主题卡那枚"背景色"色点画的就是**实际生效的页面底**本身
        // （`ThemeCardViewModel.BuildSwatches` 用 `SurfaceBaseColor` 替换最浅成员），逐字节同色 = 1.000）：
        //  ① `ThemeDefinition.NeutralHueOverride` 必须为空（表面族只由最浅成员决定 → 与背景同色相）；
        //  ② 渲染出来的色点里**含页面底本身**（卡片实际画什么 = 用户看到的融合）；
        //  ③ 页面底只许动明度与"彩度量级"：色相与背景色成员 ≤5°（8 位往返量化下界）、
        //     彩度不得超过配色"浅调成员"的量级（不发明配色里没有的更浓颜色）。
        var failures = new List<string>();
        var counts = new List<string>();
        foreach (var theme in ThemeCatalog.All)
        {
            var table = PaletteSolver.Solve(theme);
            var pageBase = table.Token(AppTokens.SurfaceBase);
            var slots = PaletteSolver.EditableSlots(theme);
            var lightest = slots.OrderByDescending(c => ColorMath.Measure(c).T).First();
            counts.Add($"{theme.Id}={slots.Count}");

            // ① 结构：一个色相都不许钉
            if (theme.NeutralHueOverride is not null)
                failures.Add($"{theme.Id} 钉了中性色相 H{theme.NeutralHueOverride:F1} → 表面族会与背景色成员分开");

            // ② 显示口径的融合（真正给用户看的那条通道）：卡面渲染出来的色点里必须含页面底本身
            var card = new ThemeCardViewModel(theme);
            if (!card.Swatches.Any(c => c == ColorMath.ToMedia(pageBase)))
                failures.Add($"{theme.Id} 主题卡色点里没有它的页面底"
                             + $" #{pageBase.ToInt() & 0x00FFFFFF:X6}（圆点与背景不融合）");
            // ②b **卡面底色 = 当前生效主题的页面底**：未选中的卡画在当前主题的背景上；
            //     只有**这张卡的主题正在生效**时，它色点里的"背景色成员"才与卡面同色（看不见 = 融合），
            //     未选中的卡所有色点都看得见。
            var appBase = ThemeService.Table.Token(AppTokens.SurfaceBase);
            var expectedBg = ColorMath.ToMedia(appBase);
            if (card.CardBackground != expectedBg)
                failures.Add($"{theme.Id} 卡面底色 #{card.CardBackground.R:X2}{card.CardBackground.G:X2}{card.CardBackground.B:X2}"
                             + $" ≠ 当前生效主题的页面底 #{expectedBg.R:X2}{expectedBg.G:X2}{expectedBg.B:X2}"
                             + "（卡面必须画在'当前主题的背景'上）");
            if (theme.Id == ThemeService.Current.Id && !card.Swatches.Contains(expectedBg))
                failures.Add($"{theme.Id} 正在生效，但它色点里没有一枚与卡面同色（融合断掉）");

            // ③ 页面底只许动明度与"彩度量级"。
            //    容差 5° = 本仓既有的"HCT↔sRGB 8 位往返量化下界"（同 `出厂默认主题_表面族随配色最浅色旋转`）：
            //    压档时贴色域边界的浅色会被钳制（实测宇治抹茶 `#E8F2EF` H186.8 压到 T91 后 H191.2，差 4.4°，
            //    派生侧已按"降彩度到能表示为止"把漂移压到最小）；
            //    真正的回归（表面族被钉到别的色相）差的是几十度，照样抓得住。
            //
            //    ⚠️ 回归现象"紫罗兰发灰"：页面底彩度**不再以"背景色成员本色"为上限**
            //    （那个成员可能只有 C5.4 = 整页发灰），而是提到配色**浅调成员**的量级
            //    （封顶 `SurfaceChromaMax`）—— 旧断言"不许超过成员本色 +0.5"随之作废。
            //    判据改为**量级上限**：不许超过"配色自己的浅调成员"（超过 = 又发明了配色里没有的更浓颜色）。
            var m0 = ColorMath.Measure(lightest);
            var m1 = ColorMath.Measure(pageBase);
            if (ColorMath.HueDistance(m0.H, m1.H) > 5.0)
                failures.Add($"{theme.Id} 背景色成员 H{m0.H:F1} 与页面底 H{m1.H:F1} 不同色相（表面族被带离了那个成员）");
            var lightChroma = theme.Palette.Select(ColorMath.Measure)
                .Where(x => x.T >= PaletteSolver.SurfaceLightMemberMinTone)
                .Select(x => x.C)
                .DefaultIfEmpty(0)
                .Max();
            var chromaCeiling = Math.Max(m0.C, Math.Min(lightChroma, PaletteSolver.SurfaceChromaMax)) + 0.5;
            if (m1.C > chromaCeiling)
                failures.Add($"{theme.Id} 页面底彩度超过配色浅调成员的量级（{m1.C:F1} > {chromaCeiling - 0.5:F1}）"
                             + "——只许压明度与提到配色自己的量级，不许发明更浓的颜色");

            // ④ 色点数 = 设计档色数（1–4 套五色 / 5–10 套四色 / 出厂默认四色）
            if (slots.Count != theme.Palette.Count)
                failures.Add($"{theme.Id} 色点 {slots.Count} ≠ 设计档色数 {theme.Palette.Count}");
        }

        // 先对账"设计档色数"（比 solve 结果更硬的判据：它是目录本身的形状）
        // 2026-10-14：出厂默认改薄荷气泡水（深青/气泡青/薄荷/米白 四色梯度）；旧出厂默认紫罗兰以 5 色预设「violet」保留。
        var shape = string.Join(" ", ThemeCatalog.Presets.Select(p => $"{p.Id}={p.Palette.Count}"));
        Assert.True(ThemeCatalog.Default.Palette.Count == 4, $"出厂默认必须是 4 色（当前 {ThemeCatalog.Default.Palette.Count}）");
        Assert.True(ThemeCatalog.Presets.Count(p => p.Palette.Count == 5) == 5,
            "5 色预设 = 紫罗兰 + 设计档第 1–4 套：" + shape);
        Assert.True(ThemeCatalog.Presets.Count(p => p.Palette.Count == 4) == 5,
            "4 色预设 = 设计档剩余 5 套（薄荷气泡水已升为出厂默认，从预设列表移出）：" + shape);
        Assert.True(failures.Count == 0, "背景色融合 / 色数对账未通过：\n" + string.Join("\n", failures));
    }

    [Fact]
    public void 过渡期兼容层_已删除()
    {
        // T2 的兼容层（Tokens/ThemeCompatibility.cs）是临时脚手架：T3 必须整文件删除。
        // 它存在就意味着"发布的不是最终语义"——本断言防止它被遗忘或被重新引入。
        var path = Path.Combine(RepoRootOfTests(), "src", "LinkPocket.Theming", "Tokens", "ThemeCompatibility.cs");
        Assert.False(File.Exists(path), "T2 过渡期兼容层必须已删除（T3 起发布值 = 最终语义值）");

        // 令牌键名里也不得再出现 Legacy 一族
        foreach (var token in AppTokens.AllColorTokens)
            Assert.DoesNotContain(".Legacy.", token, StringComparison.Ordinal);
    }

    /// <summary>从测试二进制位置回溯到含解决方案的仓库根。</summary>
    private static string RepoRootOfTests()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LinkPocket.sln")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    [Fact]
    public void 全站_不使用红色_校验错误描边取文字主色()
    {
        // 决策 4「彻底去红」：校验错误的描边 = 文字主色（不是红）。
        foreach (var theme in ThemeCatalog.All)
        {
            var t = PaletteSolver.Solve(theme);
            Assert.Equal(t.Token(AppTokens.TextPrimary).ToInt(), t.Token(AppTokens.LineInvalid).ToInt());
        }
    }

    [Fact]
    public void 颜色型令牌_与同名画刷令牌同源()
    {
        // 阴影/渐变的 Color 令牌必须与 Brush 令牌同源（否则两处会各自漂移）。
        var t = PaletteSolver.Solve(ThemeCatalog.Default);
        Assert.Equal(t.Token(AppTokens.OverlayShadow).ToInt(), t.Token(AppTokens.ShadowColor).ToInt());
    }

    [Fact]
    public void 派生确定性_同一主题重复求值逐键同值()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var a = PaletteSolver.Solve(theme);
            var b = PaletteSolver.Solve(theme);
            foreach (var key in a.Anchored.Keys)
                Assert.Equal(a.Anchored[key].ToInt(), b.Anchored[key].ToInt());
            foreach (var key in a.Tokens.Keys)
                Assert.Equal(a.Tokens[key].ToInt(), b.Tokens[key].ToInt());
        }
    }

    private static uint Rgb(Argb c) => (uint)(c.ToInt() & 0x00FFFFFF);
}
