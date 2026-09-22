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
        new("Text.OnContainer / Accent.Container",
            t => (t.Token(AppTokens.TextOnContainer), t.Token(AppTokens.AccentContainer)), 7.0, "导航 / 分段指示器 / 徽标 / chip 上的字（浅色强调容器）"),
        new("Text.Primary / Surface.Selected",
            t => (t.Token(AppTokens.TextPrimary), t.Token(AppTokens.SurfaceSelected)), 7.0, "选中行正文（列表行 / 树行选中的主文字）"),
        new("Text.Secondary / Surface.Selected",
            t => (t.Token(AppTokens.TextSecondary), t.Token(AppTokens.SurfaceSelected)), 4.5, "选中行次列文字（日期 / 计数这类在选中底上）"),
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
    public void 对比度矩阵_11套主题_两种配色方式全部达标()
    {
        // 配色应用方式（「自动调整颜色」开关）**两种都要可读**：
        // 直配（尽量原样用用户颜色）与自动调色（按档位重排）走不同的取色分支，
        // 任一支跌破阈值都要在这里红 —— 只测一种模式等于把另一半放空。
        var failures = new List<string>();
        foreach (var mode in new[] { PaletteMode.Exact, PaletteMode.Auto })
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
        // 出厂默认**不再钉中性色相**——表面族色相 = 配色里最浅的 #F2EEF5（H287.7），
        // 于是旋转角 = 287.7 − 298.7 = −11°（≡ 349°）。旧判据"表面族逐字节等于改造前"随之作废。
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

            // 色相：按旋转角走。容差 5° 不是"差不多就行"，而是 HCT↔sRGB **8 位往返**的量化下界：
            // 低彩度（C≈4 的表面族）与贴色域边界的色（InversePrimary C≈40）旋转后 RGB 几乎不变，
            // 反解出来的色相会偏 1–3°（实测 Surface 偏 2.5°/OnTertiary 是纯白，色相不可观测）。
            // 真正的回归（旋转没生效 = 差 11°）仍然会被抓住。
            var expectedHue = ColorMath.NormalizeHue(before.H + table.SurfaceRotation);
            Assert.True(ColorMath.HueDistance(expectedHue, after.H) <= 5.0,
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
        // 口径：界面色 = **用户给的颜色本身**优先 —— 强调 / 支撑 / 描边 / 正文直接取配色成员；
        // 页面底取"背景色成员"**本色**落到明度档 **87–91**（更浅的压到 91、更深的提到 87，
        // 见 `页面底_夹在明度档内_且卡面与选中底都看得见`）。
        // 本用例按 **Exact**（开关关闭）口径核对逐键实测值：那是"最贴近原色"的那一支。
        // ⚠️ 按**主题定义**求解，不读 `ThemeService.DerivedTable`（进程级共享状态，别的测试类会并行改它）。
        var t = PaletteSolver.Solve(ThemeCatalog.Default with { PaletteMode = PaletteMode.Exact });
        Assert.Equal(0x251C2Eu, Rgb(t.Token(AppTokens.TextPrimary)));      // ← 色1 #3F3448 本色（最深成员的"墨"）
        Assert.Equal(0x4D4357u, Rgb(t.Token(AppTokens.TextSecondary)));    // ← 同一个墨提亮到次文档（直配：不换成灰）
        Assert.Equal(0x655A6Fu, Rgb(t.Token(AppTokens.TextMuted)));        // ← 同上，弱文档
        Assert.Equal(0x6A567Cu, Rgb(t.Token(AppTokens.AccentFill)));       // ← 色2 #6E5A80（彩度最高，本色压到填充档）
        Assert.Equal(0x6A567Cu, Rgb(t.Token(AppTokens.AccentIcon)));
        Assert.Equal(0x523F63u, Rgb(t.Token(AppTokens.AccentText)));
        // 强调容器 = 导航 / 分段 / 分段指示器 / 面包屑当前段 / 徽标 / chip 的**浅色底**
        //（承载面是页面底与悬停底；判据 = 对页面底 ≥1.08、对悬停底 ≥1.06，实测 1.114 / 1.203）。
        // ⚠️ 它**不**再给列表行 / 树行当选中底 —— 那类面画在卡面上，需要更深的一支（见下一条）。
        Assert.Equal(0xEFE7FFu, Rgb(t.Token(AppTokens.AccentContainer)));
        // 选中底 / 落点高亮 = **列表行 / 树行 / 下拉选中项**的底：判据锚在真实承载面（**卡面**）上，
        // 对卡面 ≥1.22（最硬）、对页面底 ≥1.08、对悬停底 ≥1.06。取值 = 允许的最深档
        //（`ContainerToneFloor` T76，实测对卡面 1.675 / 对页面底 1.431 / 对悬停底 1.325）。
        Assert.Equal(0xC0B8D0u, Rgb(t.Token(AppTokens.SurfaceSelected)));
        // 面板层（表头带 / 侧区面板 / 状态栏）= **独立的一层**：页面底（T88.8）**浅压深 5 档** ⇒ T83.8。
        // 与悬停底同色时画在它上面的悬停反馈看不见（实测 1.000，鼠标悬停表头毫无变化）；
        // 而压得更深（T78 一档）会把整条表头带读成**灰紫**——层次的取舍以观感为准（用户判据 = 不发灰）。
        // 表头的悬停反馈改成"抬亮"（药丸用卡面色），不再依赖本层比悬停底更深。
        Assert.Equal(0xD6CDE7u, Rgb(t.Token(AppTokens.SurfacePanel)));
        // 容器字 = 支撑族 T15（唯一真值：`App.Text.OnContainer` 同时服务强调容器与次强调容器）
        Assert.Equal(0x2D203Bu, Rgb(t.Token(AppTokens.TextOnContainer)));
        Assert.Equal(0xF0DBFFu, Rgb(t.Token(AppTokens.SupportContainer))); // ← 色3 #A18EB0（支撑槽本色提亮）
        Assert.Equal(0x695877u, Rgb(t.Token(AppTokens.SupportIcon)));
        Assert.Equal(0x695877u, Rgb(t.Token(AppTokens.TypeFolder)));
        Assert.Equal(0xA898B4u, Rgb(t.Token(AppTokens.LineOutline)));      // ← 色4 本色（#E0CEEC）压到描边档
        // ← 色5（背景色成员 **#F7EEF8**：原 #F2EEF5 H287.7 加彩度后发蓝，故改值）的**色相**
        //   + 配色"浅调成员"（色4 #D5C7DE, C15.1）的**彩度量级**：明度压到 **87–91 深度档**（T91）；
        //   彩度不再取"背景色成员本色"（只有 C5.4，整页发灰）→ 取浅调成员量级（封顶 16）→ C15.3。
        //   主题卡那枚色点显示的就是**这个值**（与页面底/卡面底逐字节同色 = 融合）
        Assert.Equal(0xE4DBF5u, Rgb(t.Token(AppTokens.SurfaceBase)));
        // 卡面 = 页面底提亮 6 档（T91 → T97；档距 6 是"卡面对页面底 ≥1.15"实测选定的取值）
        Assert.Equal(0xF5EDFFu, Rgb(t.Token(AppTokens.SurfaceCard)));
    }

    [Fact]
    public void 配色应用方式_缺省是自动调色_直配只改文字两档()
    {
        // 缺省 = 自动调色（开关打开）。
        Assert.Equal(PaletteMode.Auto, ThemeService.DefaultPaletteMode);
        Assert.Equal(PaletteMode.Auto, new ThemeDefinition
        {
            Id = "t", Source = ThemeSource.UserDefined, Palette = ThemeCatalog.Default.Palette,
        }.PaletteMode);

        var exact = PaletteSolver.Solve(ThemeCatalog.Default with { PaletteMode = PaletteMode.Exact });
        var auto = PaletteSolver.Solve(ThemeCatalog.Default with { PaletteMode = PaletteMode.Auto });

        // ① 两种模式的**结构色相同**（页面底 / 卡面 / 强调 / 容器 / 描边 / 正文 / 图标…）：
        //    自动调色不该把用户选的颜色换掉，它只调整文字两级的中性度。
        //    ⚠️ 例外 = 悬停底（`Surface.Hover` 与绑到它的 `Surface.Panel`）：它的档位是**按"弱文字对它 ≥4.5"
        //    反推**出来的，而弱文字本身在两种模式下取值不同（实测直配 4.47 / 自动 4.51，恰好骑在阈值两侧）
        //    → 两种模式的悬停底可能差一档。这是**有意的**：可读性不能为了"结构色逐字节相同"让路；
        //    该例外由下方 ③ 的实测断言与 `对比度矩阵`（两种模式逐条）共同守住。
        //    ⚠️ 面板层（`Surface.Panel`）已从"= 悬停底"改为**页面底自己的一个档**（对页面底 / 卡面 / 悬停底
        //    都分得开），因此它不再跟着悬停底在两种模式间漂 —— 它回到下面这条"结构色逐字节相同"的断言里。
        var hoverTokens = new[] { AppTokens.SurfaceHover };
        foreach (var token in AppTokens.AllColorTokens
                     .Except(new[] { AppTokens.TextSecondary, AppTokens.TextMuted })
                     .Except(hoverTokens))
            Assert.True(exact.Token(token).ToInt() == auto.Token(token).ToInt(),
                $"{token} 在两种模式下不同：exact={Rgb(exact.Token(token)):X6} auto={Rgb(auto.Token(token)):X6}");

        // ② 差异落在文字两档：直配 = 同一个墨提亮；自动 = 中性灰墨
        //    （灰墨带一点表面族色相 —— 表面色相改为 #F7EEF8 的 H309.8 之后，这两档跟着挪了 2 个色阶）
        Assert.NotEqual(exact.Token(AppTokens.TextSecondary).ToInt(), auto.Token(AppTokens.TextSecondary).ToInt());
        Assert.Equal(0x48464Au, Rgb(auto.Token(AppTokens.TextSecondary)));
        Assert.Equal(0x605D62u, Rgb(auto.Token(AppTokens.TextMuted)));

        // ③ 融合在**两种模式下都成立**（开关关闭或打开，主题卡色点都必须能融合）
        foreach (var mode in new[] { PaletteMode.Exact, PaletteMode.Auto })
        {
            var table = PaletteSolver.Solve(ThemeCatalog.Default with { PaletteMode = mode });
            var baseColor = table.Token(AppTokens.SurfaceBase);
            var surface = PaletteSolver.SurfaceBaseColor(ThemeCatalog.Default with { PaletteMode = mode });
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
            AppTokens.SurfaceTintCard, AppTokens.AccentFill, AppTokens.AccentText,
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
        // （旧模型实测：默认主题 5 色里 3 个去掉后 0 个令牌变化 —— 见 内部资产/文档/WARNINGS.md 77。）
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
        // 面板层（`Surface.Panel`）的阈值：11 套实测最弱值 vsBase 1.142 / vsCard 1.330 / vsSelected 1.249
        // （现行档距 = `PaletteSolver.SurfacePanelDrop` = 5；对卡面这条最硬 —— 行区是近白卡面，
        // "表头是一条带"主要靠它读出来；表头**悬停**另算：药丸用卡面色，与带的对比 = 同一条 vsCard）。
        const double PanelMinContrastOnBase = 1.12;
        const double PanelMinContrastOnCard = 1.30;
        const double PanelMinContrastOnSelected = 1.20;
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

            var selectedOnHover = ColorMath.ContrastRatio(selected, hover);
            if (selectedOnHover < PaletteSolver.ContainerMinContrastOnHover)
                failures.Add($"{theme.Id} 选中底对悬停底 {selectedOnHover:F3} < {PaletteSolver.ContainerMinContrastOnHover}");

            // 强调容器：画在页面底 / 悬停底上（导航指示器、分段指示器、徽标、chip）——
            // 它必须比页面底与悬停底都看得出来，否则"指示器与底同色"（这类面没有卡面那层提亮）。
            var containerOnBase = ColorMath.ContrastRatio(container, baseColor);
            if (containerOnBase < PaletteSolver.ContainerMinContrastOnBase)
                failures.Add($"{theme.Id} 强调容器对页面底 {containerOnBase:F3} < {PaletteSolver.ContainerMinContrastOnBase}（指示器/徽标看不见）");

            var containerOnHover = ColorMath.ContrastRatio(container, hover);
            if (containerOnHover < PaletteSolver.ContainerMinContrastOnHover)
                failures.Add($"{theme.Id} 强调容器对悬停底 {containerOnHover:F3} < {PaletteSolver.ContainerMinContrastOnHover}");

            // 面板层（表头带 / 侧区面板 / 状态栏）= 页面底**浅压深 5 档**的独立一层：
            //  ① 对卡面 ≥1.30（**表头是一条带**主要靠它读出来；表头悬停 = 药丸抬亮成卡面色，对比同此）；
            //  ② 对页面底 ≥1.12（面板与页面底仍须分得开）；
            //  ③ 对选中底 ≥1.20（面板不许抢选中底的层级）。
            //    ⚠️ 别再要求"比悬停底更深"：压深到那一步（T78）整条带会读成灰紫；悬停的可见性由
            //    "抬亮成卡面"承担（对比度 = 本条的 vsCard，实测 1.33–1.38）。
            var panel = table.Token(AppTokens.SurfacePanel);
            var panelOnCard = ColorMath.ContrastRatio(panel, card);
            if (panelOnCard < PanelMinContrastOnCard)
                failures.Add($"{theme.Id} 面板层对卡面 {panelOnCard:F3} < {PanelMinContrastOnCard}（表头带与行区分不出来）");

            var panelOnBase = ColorMath.ContrastRatio(panel, baseColor);
            if (panelOnBase < PanelMinContrastOnBase)
                failures.Add($"{theme.Id} 面板层对页面底 {panelOnBase:F3} < {PanelMinContrastOnBase}（大片面板与页面底分不出层次）");

            var panelOnSelected = ColorMath.ContrastRatio(panel, selected);
            if (panelOnSelected < PanelMinContrastOnSelected)
                failures.Add($"{theme.Id} 面板层对选中底 {panelOnSelected:F3} < {PanelMinContrastOnSelected}（面板抢了选中底的层级）");
        }
        Assert.True(failures.Count == 0, "表面族层次未达标：\n" + string.Join("\n", failures));
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

            // ④ 色点数 = 设计档色数（1–4 套五色 / 5–10 套四色 / 出厂默认五色）
            if (slots.Count != theme.Palette.Count)
                failures.Add($"{theme.Id} 色点 {slots.Count} ≠ 设计档色数 {theme.Palette.Count}");
        }

        // 先对账"设计档色数"（比 solve 结果更硬的判据：它是目录本身的形状）
        var shape = string.Join(" ", ThemeCatalog.Presets.Select(p => $"{p.Id}={p.Palette.Count}"));
        Assert.True(ThemeCatalog.Default.Palette.Count == 5, $"出厂默认必须是 5 色（当前 {ThemeCatalog.Default.Palette.Count}）");
        Assert.True(ThemeCatalog.Presets.Count(p => p.Palette.Count == 5) == 4,
            "设计档第 1–4 套（赭石玫瑰 / 暮色玫瑰 / 藕粉灰绿 / 焦糖玫瑰）必须是 5 色：" + shape);
        Assert.True(ThemeCatalog.Presets.Count(p => p.Palette.Count == 4) == 6,
            "设计档第 5–10 套必须是 4 色：" + shape);
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
