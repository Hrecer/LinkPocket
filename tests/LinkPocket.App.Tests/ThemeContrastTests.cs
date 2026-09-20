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
    public void 出厂默认主题_表面族与全部锚定键逐字节等于今天()
    {
        var table = PaletteSolver.Solve(ThemeCatalog.Default);

        // 表面族（层感的来源）必须逐字节不变 —— 这是"默认主题保留目前的背景色"的硬判据
        foreach (var key in SurfaceAnchors.SurfaceStackKeys)
        {
            var anchor = SurfaceAnchors.Find(key)!.Value;
            Assert.Equal(anchor.Today.ToInt(), table.Key(key).ToInt());
        }

        // 旋转角必须恰为 0（默认主题钉住中性色相 → 其余旋转键也逐字节不变）
        Assert.Equal(0.0, table.SurfaceRotation, 6);

        // 全部**旋转**键逐字节等于锚点（语义覆写键不在其中，见 ThemeSemanticOverrideTests）
        foreach (var anchor in SurfaceAnchors.All.Where(a => a.Rotates))
        {
            if (IsSemanticOverride(anchor.Key)) continue;
            Assert.Equal(anchor.Today.ToInt(), table.Key(anchor.Key).ToInt());
        }
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
    public void 出厂默认主题_文字三档与强调族等于方案定稿值_且已发布到界面()
    {
        // T3：定稿值**就是发布值**（过渡期兼容层已删除），故校验 ThemeService 实际发布的表。
        var t = ThemeService.DerivedTable;
        Assert.Equal(0x201F22u, Rgb(t.Token(AppTokens.TextPrimary)));
        Assert.Equal(0x48464Au, Rgb(t.Token(AppTokens.TextSecondary)));
        Assert.Equal(0x605D62u, Rgb(t.Token(AppTokens.TextMuted)));
        Assert.Equal(0x6A567Cu, Rgb(t.Token(AppTokens.AccentFill)));
        Assert.Equal(0x6A567Cu, Rgb(t.Token(AppTokens.AccentIcon)));
        Assert.Equal(0x523F63u, Rgb(t.Token(AppTokens.AccentText)));
        Assert.Equal(0xF0DBFFu, Rgb(t.Token(AppTokens.AccentContainer)));
        // 容器字 = 支撑族 T15（唯一真值：`App.Text.OnContainer` 同时服务强调容器与次强调容器）
        Assert.Equal(0x352023u, Rgb(t.Token(AppTokens.TextOnContainer)));
        Assert.Equal(0xFDDADDu, Rgb(t.Token(AppTokens.SupportContainer)));
        Assert.Equal(0x72585Au, Rgb(t.Token(AppTokens.SupportIcon)));
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
