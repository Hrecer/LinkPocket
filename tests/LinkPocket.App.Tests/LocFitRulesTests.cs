using System;
using System.Windows;
using LinkPocket.I18n;
using LinkPocket.Views;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 自适应本体（<see cref="LocFit.Fit"/>）：**纯算法**用例——注入假度量（宽度 = 字符数 × 字号），
/// 因此不依赖渲染线程，能在普通 xUnit 线程上跑完整条链。
/// </summary>
/// <remarks>
/// <para>
/// "几何冻结 + 字号自适应"的可执行定义就在这里：**能放下的最大档**是唯一解，
/// 路径只有一条 —— **缩字号**（放不下就继续缩，无下限），**永不截断**
/// （2026-09-22 定稿口径；曾经的"先换 `#short` 短式"与"到硬边界就 `CharacterEllipsis`"两档都已删除）。
/// </para>
/// <para>
/// ⚠️ <see cref="LocFit.Metrics"/> 是<b>进程级静态</b>：每个用例跑前装上假度量、跑完必须还原
/// （与 <c>ThemeService.ResetForTests</c> 同一条纪律；不还原会让同一程序集里别的用例量出假宽度）。
/// </para>
/// </remarks>
public sealed class LocFitRulesTests : IDisposable
{
    private readonly Func<LocFit.MetricsRequest, double> _original = LocFit.Metrics;

    public LocFitRulesTests()
    {
        // 假度量：宽度与字号严格成正比。字宽 = 1.0 × 字号 ⇒ 每个字符占的字号数就是"字符数"。
        LocFit.Metrics = request => request.Text.Length * request.Size;
    }

    public void Dispose()
    {
        LocFit.Metrics = _original;
        LocFit.ClearCache();
    }

    private static readonly LocFit.FontDescriptor Desc = new("Test Family", FontWeights.Normal, FontStretches.Normal, 1.0);

    private static LocFit.FitResult Fit(string? full, double baseSize, double available)
        => LocFit.Fit(full, baseSize, available, Desc);

    [Fact]
    public void 放得下时按基准字号画()
    {
        // 5 字 × 12.5 = 62.5 ≤ 100
        var result = Fit("恢复默认外观", 12.5, 100);

        Assert.Equal(12.5, result.Size);
    }

    [Fact]
    public void 放不下时只缩字号_不换文案也不截断()
    {
        // 18 字 × 12.5 = 225 > 140 ⇒ 解 18×s ≤ 140 ⇒ s ≤ 7.78 ⇒ 吸附到 7.5（0.5 网格）
        var result = Fit("Delete permanently", 12.5, 140);

        Assert.Equal(7.5, result.Size);
    }

    [Fact]
    public void 缩字号取能放下的最大档()
    {
        // 20 字，可用 200 ⇒ 解 20×s ≤ 200 ⇒ s ≤ 10 ⇒ 吸附到 10.0
        var result = Fit(new string('m', 20), 12.5, 200);

        Assert.Equal(10.0, result.Size);
    }

    [Fact]
    public void 缩字号只取零点五档_不产生非网格字号()
    {
        // 20 字，可用 190 ⇒ s ≤ 9.5 ⇒ 恰为 0.5 网格点
        var result = Fit(new string('m', 20), 12.5, 190);

        Assert.Equal(9.5, result.Size);
        Assert.Equal(0, (result.Size / LocFit.Step) % 1, 6);
    }

    [Fact]
    public void 字号没有下限_放不下就一直缩到放得下为止()
    {
        // 命令栏「重命名」的真实几何（实测自 BrowserView.xaml + TonalButton 样式）：
        // 按钮 80、内距 12×2、图标 16 + 间距 7 ⇒ 文本可用宽 = 80 − 24 − 23 = 33。
        // 真实宽度（FormattedText 实测）：中文「重命名」13pt 时 39.0（中文自己就溢出！
        // 需要 11.0pt）；英文 `Rename` 13pt 时 46.2，需要 9.0pt。
        // 旧的 `max(base×0.75, 9.5pt)` 下限会停在 9.5pt（33.7 > 33）而放不下——
        // 这正是决策 6 被撤销的那条实测。这里用**实测比值**给替身度量，等价于真实排版。
        LocFit.Metrics = request =>
            request.Text == "Rename" ? request.Size * (46.2 / 13.0) : request.Size * (39.0 / 13.0);
        LocFit.ClearCache();

        var en = Fit("Rename", 13.0, 33.0);
        Assert.True(en.Size < 9.5, $"必须允许缩到 9.5pt 以下才放得下，实际 {en.Size}pt");

        var zh = Fit("重命名", 13.0, 33.0);
        Assert.True(zh.Size < 13.0, $"中文在 base 13pt 就已溢出，必须缩小，实际 {zh.Size}pt");
    }

    [Fact]
    public void 缩到退化边界仍放不下_停在边界档永不截断()
    {
        // 度量与字号无关（恒 400）：任何档都放不下 ⇒ 一路缩到退化边界。
        // **永不截断**：停在边界档，把"内容完整"交给容器去裁（宁可字小，也不许把内容切掉）。
        LocFit.Metrics = _ => 400;
        LocFit.ClearCache();

        var result = Fit(new string('m', 40), 12.5, 200);

        Assert.Equal(LocFit.DegenerateFloor, result.Size);
    }

    [Fact]
    public void 空文案原样返回基准字号_零可用宽也保持基准字号()
    {
        Assert.Equal(12.5, Fit("", 12.5, 100).Size);
        Assert.Equal(12.5, Fit(null, 12.5, 100).Size);
        // 可用宽 0/-5 = 一点位置都没有：不缩字号——宽度不因字号而变，缩了也放不下。
        // （调用方另有"宽度为 0 先不判定"的守卫，等真拿到宽度再投。）
        Assert.Equal(12.5, Fit("恢复默认外观", 12.5, 0).Size);
        Assert.Equal(12.5, Fit(new string('m', 40), 12.5, -5).Size);
    }

    [Fact]
    public void 退化边界是搜索的护栏_不是设计下限()
    {
        // 只有"可用宽被压到几乎为零"这类退化情形才碰得到它；真实需求离它很远
        // （实测最深是命令栏「重命名」13pt → 9.0pt）。
        Assert.Equal(4.0, LocFit.DegenerateFloor);

        // 逐档缩：base 13pt、每字号占 3px、可用 12 ⇒ 需要 4pt 才放得下
        LocFit.Metrics = request => request.Size * 3;
        LocFit.ClearCache();
        Assert.Equal(4.0, Fit("任意", 13.0, 12.0).Size);
    }

    [Fact]
    public void 同一输入重复求值得到逐字段相同的结果_这是防布局回环的算法前提()
    {
        foreach (var available in new[] { 60.0, 78.0, 140.0, 200.0, 190.0 })
        {
            var first = Fit("Delete permanently", 12.5, available);
            var second = Fit("Delete permanently", 12.5, available);
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void 结论对可用宽单调_越窄的字号只会更小或相等()
    {
        double? previous = null;
        for (var available = 400.0; available >= 40; available -= 10)
        {
            var size = Fit("Delete permanently", 12.5, available).Size;
            if (previous is { } last) Assert.True(size <= last + 1e-9, $"可用宽 {available} 时字号反而变大了");
            previous = size;
        }
    }

    [Fact]
    public void 零宽或负宽不产生异常且保持基准字号()
    {
        Assert.Equal(12.5, Fit(new string('m', 40), 12.5, -5).Size);
    }

    [Fact]
    public void 步长吸附只会向下_不越过基准字号()
    {
        Assert.Equal(12.5, LocFit.SnapDown(12.5));
        Assert.Equal(12.0, LocFit.SnapDown(12.4));
        Assert.Equal(11.5, LocFit.SnapDown(11.9));
    }
}
