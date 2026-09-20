using System.IO;
using LinkPocket.Theming;
using LinkPocket.Theming.Color;
using LinkPocket.Theming.Themes;
using LinkPocket.Theming.Tokens;
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
            t => (t.Token(AppTokens.TextOnContainer), t.Token(AppTokens.AccentContainer)), 7.0, "选中指示器 / 落点高亮上的字（同一个容器字令牌服务两种容器）"),
        new("Support.Icon / Accent.Container",
            t => (t.Token(AppTokens.SupportIcon), t.Token(AppTokens.AccentContainer)), 3.0, "文件夹图标落在选中底上（列表里选中行 + 类型图标是常态组合）"),
        new("Accent.Icon / Accent.Container",
            t => (t.Token(AppTokens.AccentIcon), t.Token(AppTokens.AccentContainer)), 3.0, "强调图标落在选中底上"),
        new("Text.Muted / Surface.Card",
            t => (t.Token(AppTokens.TextMuted), t.Token(AppTokens.SurfaceCard)), 4.5, "弱文字对卡面（提示 / 占位 / 主题卡摘要）"),
        new("Line.Invalid / Surface.Card",
            t => (t.Token(AppTokens.LineInvalid), t.Token(AppTokens.SurfaceCard)), 4.5, "校验错误描边（= 文字主色，2px）"),
    };

    public static IEnumerable<object[]> ThemeIds() =>
        ThemeCatalog.All.Select(t => new object[] { t.Id });

    [Fact]
    public void 对比度矩阵_11套主题全部达标()
    {
        var failures = new List<string>();
        foreach (var theme in ThemeCatalog.All)
        {
            var table = PaletteSolver.Solve(theme);
            foreach (var pair in Matrix)
            {
                var (fg, bg) = pair.Pick(table);
                var ratio = ColorMath.ContrastRatio(fg, bg);
                if (ratio < pair.Min)
                    failures.Add($"{theme.Name} · {pair.Label} = {ratio:F2} < {pair.Min:F1}（{pair.Why}）");
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
        // 方案 A（用户令 2026-09-20）：出厂默认**不再钉中性色相**——表面族色相 = 配色里最浅的 #F2EEF5（H287.7），
        // 于是旋转角 = 287.7 − 298.7 = −11°（≡ 349°）。旧判据"表面族逐字节等于改造前"随之作废（代价已确认接受）。
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
            Assert.True(after.C <= before.C + 0.5, $"{anchor.Key} 彩度被放大了：{before.C:F1} → {after.C:F1}");

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
    /// 出厂默认主题按定稿重建这些语义，其余键保持逐字节不变。
    /// </summary>
    private static bool IsSemanticOverride(string key) =>
        key is "Primary" or "OnPrimary" or "PrimaryContainer" or "OnPrimaryContainer" or "SurfaceTint"
            or "Secondary" or "OnSecondary" or "SecondaryContainer" or "OnSecondaryContainer"
            or "OnSurface" or "OnSurfaceVariant" or "OnSurfaceMuted"
            or "Outline" or "OutlineVariant";

    [Fact]
    public void 出厂默认主题_令牌等于配色直配的实测值_且已发布到界面()
    {
        // 定稿口径不变：发布值 = 最终语义值；本轮（方案 A）值随派生模型更新——
        // 强调 / 支撑 / 强调容器 / 描边 / 表面**全部来自那 5 个身份色**，不再有 ±60° 发明出来的色相。
        // ⚠️ 这里按**主题定义**求解，不读 `ThemeService.DerivedTable`：后者读的是"当前生效主题"，
        // 而 ThemeService 是进程级共享状态、别的测试类会并行改它（WARNINGS 68 同源）。
        // "发布值 = 求解值"是结构性保证（发布只此一处 `ThemePublisher`），另有探针在真实窗口里断言发布结果。
        var t = PaletteSolver.Solve(ThemeCatalog.Default);
        Assert.Equal(0x201F23u, Rgb(t.Token(AppTokens.TextPrimary)));
        Assert.Equal(0x47464Au, Rgb(t.Token(AppTokens.TextSecondary)));
        Assert.Equal(0x5F5E62u, Rgb(t.Token(AppTokens.TextMuted)));
        Assert.Equal(0x6A567Cu, Rgb(t.Token(AppTokens.AccentFill)));       // ← 色2 #6E5A80（彩度最高）
        Assert.Equal(0x6A567Cu, Rgb(t.Token(AppTokens.AccentIcon)));
        Assert.Equal(0x523F63u, Rgb(t.Token(AppTokens.AccentText)));
        Assert.Equal(0xEEDDF7u, Rgb(t.Token(AppTokens.AccentContainer)));  // ← 色1 #3F3448（彩度第三）
        // 容器字 = 支撑族 T15（唯一真值：`App.Text.OnContainer` 同时服务强调容器与次强调容器）
        Assert.Equal(0x2E203Bu, Rgb(t.Token(AppTokens.TextOnContainer)));
        Assert.Equal(0xF0DBFFu, Rgb(t.Token(AppTokens.SupportContainer))); // ← 色3 #A18EB0（彩度次高）
        Assert.Equal(0x695877u, Rgb(t.Token(AppTokens.SupportIcon)));
        Assert.Equal(0x695877u, Rgb(t.Token(AppTokens.TypeFolder)));
        Assert.Equal(0xA39BA6u, Rgb(t.Token(AppTokens.LineOutline)));      // ← 色4 #D5C7DE（彩度第四）
        Assert.Equal(0xE8E4EDu, Rgb(t.Token(AppTokens.SurfaceBase)));      // ← 色5 #F2EEF5（明度最高 → 表面族）
    }

    [Fact]
    public void 配色角色分配_彩度降序占槽_明度最高者管表面()
    {
        // 方案 A（用户令 2026-09-20）：不再分族、不再发明色相——配色成员按彩度降序占槽，
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
        // 用户令（2026-09-20）"我们给出的 4/5 个颜色要全部用上"的机器化判据 = **逐槽 leave-one-out**：
        // 去掉任一个身份色，至少有一个语义令牌变值。
        // （旧模型实测：默认主题 5 色里 3 个去掉后 0 个令牌变化 —— 见 文档/WARNINGS.md 77。）
        var baseline = PaletteSolver.Solve(ThemeCatalog.Default);
        for (var slot = 0; slot < ThemeCatalog.Default.Palette.Count; slot++)
        {
            var palette = ThemeCatalog.Default.Palette.Where((_, i) => i != slot).ToArray();
            var mutated = PaletteSolver.Solve(new ThemeDefinition
            {
                Id = "probe",
                Name = "probe",
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
                    $"{def.Name} 的{label}色相 H{hue:F1} 不在配色里（配色色相：{string.Join(" / ", hues.Select(h => h.ToString("F1")))}）");
            }
        }
    }

    [Fact]
    public void 主题卡色点_背景色成员与页面底融合_且色点数等于设计档色数()
    {
        // 用户令 2026-09-20（两张截图 + 设计档「配色方案.txt」）：
        //  ① "背景色那个圆与背景融合，这正是我们想要的效果……为什么默认紫罗兰根本就没有进行融合？"
        //     —— 融合 = 配色里的**背景色成员**（明度最高者）与表面族是同一个颜色；
        //        旧实现给每个预设钉了 `NeutralHueOverride`，把表面族带离了那个成员
        //        （默认主题实测：最浅色点 `#F2EEF5` 对页面底 `#E8E4ED` = **1.09**，看得出两块）。
        //  ② "我说的五色主题显示成四色，这是我们之前的方案" —— 设计档第 1–4 套是 **5 色**、
        //     第 5–10 套是 **4 色**，而旧实现每套只收了 1–2 个身份色、再补位凑到 4（五色被压成四色）。
        //
        // 判据两条：
        //  ① `ThemeDefinition.NeutralHueOverride` 必须为空（表面族只由最浅成员决定 → 结构与背景同色）；
        //  ② 最浅身份色对**页面底**的对比度必须很小（实测 1.00–1.20：第 1–4 套 1.01/1.06/1.04/1.01，
        //     第 5–10 套因设计档里是**高彩度浅色**（C16–19）略松，故阈值取 1.25）。
        const double MaxFusionContrast = 1.25;
        var failures = new List<string>();
        var counts = new List<string>();
        foreach (var theme in ThemeCatalog.All)
        {
            var table = PaletteSolver.Solve(theme);
            var pageBase = table.Token(AppTokens.SurfaceBase);
            var slots = PaletteSolver.EditableSlots(theme);
            var lightest = slots.OrderByDescending(c => ColorMath.Measure(c).T).First();
            var ratio = ColorMath.ContrastRatio(lightest, pageBase);
            counts.Add($"{theme.Name}={slots.Count}");

            // ① 结构：一个色相都不许钉
            if (theme.NeutralHueOverride is not null)
                failures.Add($"{theme.Name} 钉了中性色相 H{theme.NeutralHueOverride:F1} → 表面族会与背景色成员分开");

            // ② 渲染：背景色成员与页面底必须同色
            if (ratio > MaxFusionContrast)
                failures.Add($"{theme.Name} 背景色成员 {lightest.ToInt() & 0x00FFFFFF:X6} 对页面底"
                             + $" {pageBase.ToInt() & 0x00FFFFFF:X6} = {ratio:F2} > {MaxFusionContrast:F2}（没融合）");

            // ③ 色点数 = 设计档色数（1–4 套五色 / 5–10 套四色 / 出厂默认五色）
            if (slots.Count != theme.Palette.Count)
                failures.Add($"{theme.Name} 色点 {slots.Count} ≠ 设计档色数 {theme.Palette.Count}");
        }

        // 先对账"设计档色数"（比 solve 结果更硬的判据：它是目录本身的形状）
        var shape = string.Join(" ", ThemeCatalog.Presets.Select(p => $"{p.Name}={p.Palette.Count}"));
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
