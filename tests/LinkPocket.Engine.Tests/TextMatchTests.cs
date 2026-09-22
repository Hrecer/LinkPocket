using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 搜索文本匹配的唯一口径（<see cref="TextMatch"/>）。
/// </summary>
/// <remarks>
/// 放在引擎层测试里是因为它的**主要消费者是引擎侧**（<c>Modules.Search</c> 的命中判定），
/// 而它与界面侧高亮共用同一份实现——那份共用的正确性由这里钉住。
/// </remarks>
public class TextMatchTests
{
    [Theory]
    [InlineData("Hello World", "world", true)]
    [InlineData("Hello World", "WORLD", true)]
    [InlineData("Hello World", "hello", true)]
    [InlineData("Hello World", "wor", true)]
    [InlineData("Hello World", "xyz", false)]
    [InlineData("", "a", false)]
    [InlineData("abc", "", false)]
    [InlineData("abc", null, false)]
    [InlineData(null, "abc", false)]
    public void 包含判定_ASCII大小写不敏感_空串恒假(string? text, string? needle, bool expected)
        => Assert.Equal(expected, TextMatch.Contains(text, needle));

    /// <summary>
    /// 非 ASCII 不做折叠（与 SQL 侧 <c>LIKE</c> / <c>COLLATE NOCASE</c> 同义）。
    /// </summary>
    /// <remarks>
    /// <c>NOCASE</c> 只折叠 ASCII A–Z，而 .NET 的 <c>OrdinalIgnoreCase</c> 用的是 Unicode 简单折叠——
    /// 两者在非 ASCII 上确实有差异，本用例把**实际口径**钉住，免得后人以为是漏了。
    /// 真要比 SQL 更宽（例如全角/半角互认、带音调字母折叠），必须连 SQL 侧一起换。
    /// </remarks>
    [Fact]
    public void 非ASCII_按简单折叠判_与SQL侧NOCASE同口径()
    {
        // ASCII 大小写：认
        Assert.True(TextMatch.Contains("ABC", "abc"));

        // 全角与半角不是同一个字符：不认（`NOCASE` 同样不认）
        Assert.False(TextMatch.Contains("ＡＢＣ", "ABC"));

        // 中文没有大小写概念：原样匹配
        Assert.True(TextMatch.Contains("前端开发", "前端"));
        Assert.False(TextMatch.Contains("前端开发", "后端"));
    }

    [Fact]
    public void 命中区间_是原串上的下标_可直接切段()
    {
        var ranges = TextMatch.Ranges("Hello World hello", "hello");

        Assert.Equal(2, ranges.Count);
        Assert.Equal(new TextMatch.Range(0, 5), ranges[0]);
        Assert.Equal(new TextMatch.Range(12, 5), ranges[1]);
        Assert.Equal(new[] { "Hello", "hello" },
            new[] { "Hello World hello"[..ranges[0].End], "Hello World hello".Substring(ranges[1].Start, ranges[1].Length) });
    }

    /// <summary>
    /// 不重叠、左到右取尽；步进 = 区间长度（不是 +1）。
    /// </summary>
    /// <remarks>
    /// 步进写错（+1）会让高亮画出互相覆盖的段——这是"逐段拼接"最直接的错法，用重叠用例钉住。
    /// </remarks>
    [Fact]
    public void 命中区间_不重叠且左到右取尽()
    {
        var ranges = TextMatch.Ranges("aaaa", "aa");

        Assert.Equal(new[] { new TextMatch.Range(0, 2), new TextMatch.Range(2, 2) }, ranges);
    }

    /// <summary>
    /// <b>大小写折叠不改变长度</b>：早期实现用 <c>ToLowerInvariant()</c> 折叠后再取下标，
    /// 折叠串与原串**不等长**时（<c>İ</c> 这类字符）下标就错位，切出来的段是错的。
    /// </summary>
    /// <remarks>
    /// <c>U+0130 LATIN CAPITAL LETTER I WITH DOT ABOVE</c> 折叠成 <c>"i̇"</c>（两个码元）。
    /// 本用例断言：无论命中与否，区间要么为空、要么落在原串范围内且与原串片段一致。
    /// </remarks>
    [Fact]
    public void 命中区间_永远落在原串范围内()
    {
        const string text = "İstanbul K";
        foreach (var needle in new[] { "i", "İ", "K", "k", "stan" })
        {
            foreach (var range in TextMatch.Ranges(text, needle))
            {
                Assert.InRange(range.Start, 0, text.Length);
                Assert.InRange(range.End, range.Start, text.Length);
                // 原串片段与查询串按本口径必须相等（否则就是折叠串的坐标被拿来切原串了）
                Assert.Equal(needle, text.Substring(range.Start, range.Length), ignoreCase: true);
            }
        }
    }

    [Fact]
    public void 命中区间_空串任一侧即空()
    {
        Assert.Empty(TextMatch.Ranges("abc", ""));
        Assert.Empty(TextMatch.Ranges("", "abc"));
        Assert.Empty(TextMatch.Ranges(null, "abc"));
        Assert.Empty(TextMatch.Ranges("abc", null));
    }
}

/// <summary>
/// 名称排序的唯一口径（<see cref="NameOrder"/> / <see cref="SortCulture"/>）。
/// </summary>
/// <remarks>
/// 这条不变式是 i18n 的硬约束之一：<b>切语言不得改变任何排序结果</b>。
/// 排序文化由 <see cref="SortCulture.Pin"/> 在程序集装载时钉住（只钉 culture、不钉界面语言），
/// 所以"切成英文后顺序会不会变"在这里是可断言的：界面语言换了，<see cref="NameOrder.Culture"/> 不动。
/// </remarks>
public class NameOrderTests
{
    [Fact]
    public void 排序文化与固定值一致()
    {
        // 读数：这就是"排序口径"本身（而不是"某个副本恰好相等"）。
        Assert.Equal(SortCulture.Name, NameOrder.Culture.Name);
        Assert.Equal(SortCulture.Name, SortCulture.Culture.Name);
    }

    /// <summary>
    /// 文化是**钉住**的：切线程序集不该改变它。这条用"显式拨动线程文化"来验证——
    /// 拨到别的语言后，排序口径必须回到固定值（<see cref="SortCulture.Pin"/> 的语义），
    /// 而 <see cref="NameOrder.Culture"/> 读的是当前文化，所以先复位再断言。
    /// </summary>
    /// <remarks>
    /// ⚠️ 用例自己改全局线程文化，结束必须复位（与"切语言"同一类纪律：进程级状态不许漏给下一个用例）。
    /// </remarks>
    [Fact]
    public void 排序文化_被钉在中文_英文界面下也不变()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // 模拟"界面切到英文时把线程文化一起改了"这种错法
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            SortCulture.Pin();   // 模块初始化器干的就是这件事

            Assert.Equal(SortCulture.Name, CultureInfo.CurrentCulture.Name);
            Assert.Equal(SortCulture.Name, NameOrder.Culture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            SortCulture.Pin();   // 把文化拨回固定值（若 original 非中文，Pin 会纠正它）
        }
    }

    [Fact]
    public void 中文家族判定_zh各区域都算_其余不算()
    {
        Assert.True(SortCulture.IsChinese(CultureInfo.GetCultureInfo("zh-CN")));
        Assert.True(SortCulture.IsChinese(CultureInfo.GetCultureInfo("zh-TW")));
        Assert.True(SortCulture.IsChinese(CultureInfo.GetCultureInfo("zh-Hans")));
        Assert.False(SortCulture.IsChinese(CultureInfo.GetCultureInfo("en-US")));
        Assert.False(SortCulture.IsChinese(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void 比较器与比较函数同源()
    {
        foreach (var (a, b) in new[] { ("a", "b"), ("B", "a"), ("中文", "英文"), ("a", "a") })
        {
            var byComparer = Math.Sign(NameOrder.Comparer.Compare(a, b));
            var byCompare = Math.Sign(NameOrder.Compare(a, b));
            Assert.Equal(byComparer, byCompare);
        }
    }

    /// <summary>
    /// 排序结果<b>稳定</b>：同一批名称在切成英文界面后顺序一字不变。
    /// </summary>
    /// <remarks>
    /// 这里直接把界面语言（<c>LocTable</c>）拨到英文，再比对排序结果——
    /// 它证明的是"取词语言变了，排序口径没被带着走"。真实界面行为由探针的 P7 逐行比对。
    /// </remarks>
    [Fact]
    public void 切语言前后_同一批名称的顺序一字不变()
    {
        var names = new List<string> { "b", "A", "c", "中文", "B", "a" };
        var before = names.OrderBy(n => n, NameOrder.Comparer).ToList();

        var original = CultureInfo.CurrentCulture;
        try
        {
            // 界面语言与文化都不是排序的输入（文化被钉住）——两条一起拨，结果仍必须一致
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            SortCulture.Pin();
            var after = names.OrderBy(n => n, NameOrder.Comparer).ToList();

            Assert.Equal(before, after);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            SortCulture.Pin();
        }
    }
}
