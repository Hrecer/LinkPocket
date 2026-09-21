using System;
using System.Windows;
using LinkPocket.I18n;
using LinkPocket.Views;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 自适应降级链（<see cref="LocFit.Fit"/>）：**纯算法**用例——注入假度量（宽度 = 字符数 × 字号），
/// 因此不依赖渲染线程，能在普通 xUnit 线程上跑完整条链。
/// </summary>
/// <remarks>
/// <para>
/// "几何冻结 + 字号自适应"的可执行定义就在这里：<b>能放下的最大档</b>是唯一解，
/// 顺序固定（全长 → 短式 → 缩字号 → 截断），不许跳步。
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

    private static LocFit.FitResult Fit(string? full, string? shortText, double baseSize, double available)
        => LocFit.Fit(full, shortText, baseSize, available, allowTruncate: true, Desc);

    [Fact]
    public void 放得下时按基准字号画_不换形态也不截断()
    {
        // 5 字 × 12.5 = 62.5 ≤ 100
        var result = Fit("恢复默认外观", null, 12.5, 100);

        Assert.Equal(12.5, result.Size);
        Assert.False(result.UseShort);
        Assert.False(result.Truncate);
    }

    [Fact]
    public void 全长放不下而短式放得下时_换短式而不是缩字号()
    {
        // 全长 27 字 × 12.5 = 337.5 > 140；短式 6 字 × 12.5 = 75 ≤ 140
        var result = Fit("Delete permanently", "Delete", 12.5, 140);

        Assert.Equal(12.5, result.Size);   // 字号一寸不让 —— 短式优先于缩放
        Assert.True(result.UseShort);
        Assert.False(result.Truncate);
    }

    [Fact]
    public void 两种形态都放不下时_缩字号到能放下的最大档()
    {
        // 20 字，可用 200 ⇒ 解 20×s ≤ 200 ⇒ s ≤ 10 ⇒ 吸附到 10.0（正好落在 0.5 网格上）
        var result = Fit(new string('m', 20), null, 12.5, 200);

        Assert.Equal(10.0, result.Size);
        Assert.False(result.UseShort);
        Assert.False(result.Truncate);
    }

    [Fact]
    public void 缩字号只取零点五档_不产生非网格字号()
    {
        // 20 字，可用 190 ⇒ s ≤ 9.5 ⇒ 恰为下限（同为 0.5 网格点）
        var result = Fit(new string('m', 20), null, 12.5, 190);

        Assert.Equal(9.5, result.Size);
        Assert.Equal(0, (result.Size / LocFit.Step) % 1, 6);
    }

    [Fact]
    public void 缩到下限仍放不下且无短式_截断()
    {
        // 40 字 × 9.5 = 380 > 200
        var result = Fit(new string('m', 40), null, 12.5, 200);

        Assert.Equal(LocFit.MinFloor(12.5), result.Size);
        Assert.True(result.Truncate);
    }

    [Fact]
    public void 缩到下限仍放不下但有短式_先试短式再决定截断()
    {
        // 每字号占 40px（与字数无关）。可用宽 79：任何档、任何形态都放不下 ⇒ 到顶只能截断。
        LocFit.Metrics = request => request.Size * 40;
        LocFit.ClearCache();

        var truncated = Fit("长句", "短", 12.5, 79);

        Assert.Equal(LocFit.MinFloor(12.5), truncated.Size);   // 12.5×0.75 = 9.375 → 抬到 9.5
        Assert.True(truncated.UseShort);                        // 有短式就先换短式，再谈截断
        Assert.True(truncated.Truncate);
    }

    [Fact]
    public void 短式在下限放得下时_用短式而不截断()
    {
        // 短式 10px/字号、全长 100px/字号。可用宽 100：全长任何档都放不下；短式 @10 = 100 ✓
        LocFit.Metrics = request => request.Text == "短" ? request.Size * 10 : request.Size * 100;
        LocFit.ClearCache();

        var result = Fit("长句", "短", 12.5, 100);

        Assert.Equal(10.0, result.Size);       // 取"能放下的最大档"，不是一路压到下限
        Assert.True(result.UseShort);
        Assert.False(result.Truncate);
    }

    [Fact]
    public void 字号下限是基准的七成五与九点五之中较大者()
    {
        Assert.Equal(9.5, LocFit.MinFloor(11));      // 11×0.75 = 8.25 → 抬到 9.5
        Assert.Equal(9.5, LocFit.MinFloor(12.5));    // 12.5×0.75 = 9.375 → 抬到 9.5
        Assert.Equal(12.75, LocFit.MinFloor(17));    // 17×0.75 = 12.75 → 比值生效
    }

    [Fact]
    public void 只缩不截的模式在触底后不截断()
    {
        var result = LocFit.Fit(new string('m', 40), null, 12.5, 200, allowTruncate: false, Desc);

        Assert.Equal(LocFit.MinFloor(12.5), result.Size);
        Assert.False(result.Truncate);
    }

    [Fact]
    public void 空文案原样返回基准字号_零可用宽收敛到下限()
    {
        Assert.Equal(12.5, Fit("", null, 12.5, 100).Size);
        Assert.Equal(12.5, Fit(null, null, 12.5, 100).Size);
        // 可用宽 0/-5 = 一点位置都没有：收敛到下限（TryFit 另有"宽度为 0 先不判定"的守卫）
        Assert.Equal(LocFit.MinFloor(12.5), Fit("恢复默认外观", null, 12.5, 0).Size);
        Assert.Equal(LocFit.MinFloor(12.5), Fit(new string('m', 40), null, 12.5, -5).Size);
    }

    [Fact]
    public void 同一输入重复求值得到逐字段相同的结果_这是防布局回环的算法前提()
    {
        foreach (var available in new[] { 60.0, 78.0, 140.0, 200.0, 190.0 })
        {
            var first = Fit("Delete permanently", "Delete", 12.5, available);
            var second = Fit("Delete permanently", "Delete", 12.5, available);
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void 结论对可用宽单调_越窄的字号只会更小或相等()
    {
        double? previous = null;
        for (var available = 400.0; available >= 40; available -= 10)
        {
            var size = Fit("Delete permanently", "Delete", 12.5, available).Size;
            if (previous is { } last) Assert.True(size <= last + 1e-9, $"可用宽 {available} 时字号反而变大了");
            previous = size;
        }
    }

    [Fact]
    public void 零宽或负宽不产生异常且收敛到下限()
    {
        Assert.Equal(LocFit.MinFloor(12.5), Fit(new string('m', 40), null, 12.5, -5).Size);
    }

    [Fact]
    public void 步长吸附只会向下_不越过基准字号()
    {
        Assert.Equal(12.5, LocFit.SnapDown(12.5));
        Assert.Equal(12.0, LocFit.SnapDown(12.4));
        Assert.Equal(11.5, LocFit.SnapDown(11.9));
    }
}
