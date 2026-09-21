using System;
using System.Collections.Generic;
using System.Linq;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.Views;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 语言层（I18n）：字符串表对称性 / 缺键显形 / 切换生效 / 日期口径 / 复数。
/// </summary>
/// <remarks>
/// <see cref="LocTable"/> 是进程级单例，用例结束必须复位到出厂语言——
/// 否则语言会漏进下一个用例（与 <c>ThemeService.ResetForTests</c> 同一条纪律）。
/// </remarks>
public sealed class I18nTests : IDisposable
{
    public void Dispose() => LocaleService.Apply(AppLocales.Default);

    private static readonly DateTime Sample = new(2026, 9, 21, 13, 40, 0);

    [Fact]
    public void 两种语言的键集合完全相等_且无空文本()
    {
        var zh = StringTables.For(AppLocale.ZhCn);
        var en = StringTables.For(AppLocale.En);

        Assert.Equal(zh.Count, en.Count);
        Assert.Subset(new HashSet<string>(zh.Keys, StringComparer.Ordinal), new HashSet<string>(en.Keys, StringComparer.Ordinal));
        Assert.All(en.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    [Fact]
    public void 缺键在界面上显形_不回退成空串()
    {
        var text = Loc.T("nav.root.没有这条键");

        Assert.Equal("⟨nav.root.没有这条键⟩", text);
    }

    [Fact]
    public void 切语言立即生效_版本递增_且同语言重复应用不涨版本()
    {
        var before = Loc.Table.Version;

        LocaleService.Apply(AppLocale.En);
        Assert.Equal(AppLocale.En, LocaleService.Current);
        Assert.Equal("Bookmarks", Loc.T("nav.root.bookmarks"));
        Assert.Equal(before + 1, Loc.Table.Version);

        LocaleService.Apply(AppLocale.En);
        Assert.Equal(before + 1, Loc.Table.Version);
    }

    [Fact]
    public void 切换事件带出新语言_且只在真变了时触发()
    {
        var fired = new List<AppLocale>();
        void Handler(AppLocale l) => fired.Add(l);

        LocaleService.LanguageChanged += Handler;
        try
        {
            LocaleService.Apply(AppLocale.En);
            LocaleService.Apply(AppLocale.En);
        }
        finally
        {
            LocaleService.LanguageChanged -= Handler;
        }

        Assert.Equal(new[] { AppLocale.En }, fired);
    }

    [Fact]
    public void 中文日期沿用基线格式_英文走十二小时制且不出中文时段()
    {
        LocaleService.Apply(AppLocale.ZhCn);
        Assert.Equal("2026-09-21 13:40", UiClock.Format(Sample));

        LocaleService.Apply(AppLocale.En);
        var en = UiClock.Format(Sample);
        Assert.Equal("09/21/2026 1:40 PM", en);
        // 进程 culture 钉在 zh-CN：不显式带 en-US 文化，tt 会渲染成「下午」
        Assert.DoesNotContain("下午", en, StringComparison.Ordinal);
        Assert.DoesNotContain("上午", en, StringComparison.Ordinal);
    }

    [Fact]
    public void 英文短式去年份_中文短式回基线()
    {
        LocaleService.Apply(AppLocale.En);
        Assert.Equal("09/21 1:40 PM", UiClock.FormatShort(Sample));

        LocaleService.Apply(AppLocale.ZhCn);
        Assert.Equal(UiClock.Format(Sample), UiClock.FormatShort(Sample));
    }

    [Fact]
    public void 复数取形规则_英文按n_中文恒取单数形()
    {
        // 用两条真实键当 one/other 替身，验的是"选哪条"的规则本身
        LocaleService.Apply(AppLocale.En);
        Assert.Equal(Loc.T("nav.root.bookmarks"), Loc.Plural(1, "nav.root.bookmarks", "nav.root.trash"));
        Assert.Equal(Loc.T("nav.root.trash"), Loc.Plural(3, "nav.root.bookmarks", "nav.root.trash"));

        LocaleService.Apply(AppLocale.ZhCn);
        Assert.Equal(Loc.T("nav.root.bookmarks"), Loc.Plural(1, "nav.root.bookmarks", "nav.root.trash"));
        Assert.Equal(Loc.T("nav.root.bookmarks"), Loc.Plural(3, "nav.root.bookmarks", "nav.root.trash"));
    }

    // ── 路径：canonical 与显示投影 ─────────────────────────────────────

    [Fact]
    public void 路径显示只换根段_其余段是用户数据原样不动()
    {
        LocaleService.Apply(AppLocale.ZhCn);
        Assert.Equal("全部书签 / 工作 / 前端", BookmarkDisplay.Path("@root/工作/前端"));

        LocaleService.Apply(AppLocale.En);
        Assert.Equal("Bookmarks / 工作 / 前端", BookmarkDisplay.Path("@root/工作/前端"));
        Assert.Equal("Trash", BookmarkDisplay.Path("@trash"));
        Assert.Equal("Unknown location", BookmarkDisplay.Path("@unknown"));   // 断链不伪装成根，也不留 token 给用户看
    }

    [Fact]
    public void 中文下复制的路径_切英文后地址栏照样解析到同一层()
    {
        IReadOnlyList<PathNode> ChildrenOf(string? id) => id switch
        {
            null => new[] { new PathNode("id-work", "工作") },
            "id-work" => new[] { new PathNode("id-fe", "前端") },
            _ => Array.Empty<PathNode>(),
        };
        var resolver = new PathResolver(BookmarkPath.RootToken, ChildrenOf);

        foreach (var typed in new[] { "@root/工作/前端", "全部书签/工作/前端", "Bookmarks/工作/前端" })
        {
            Assert.True(resolver.TryResolve(typed, out var id, out var bad), $"{typed} 解析失败于段「{bad}」");
            Assert.Equal("id-fe", id);
        }

        // 回收站那个根不许认领浏览器的路径首段（否则 `回收站/A` 会被解析成 @root/A）
        Assert.False(resolver.TryResolve("回收站/工作", out _, out _));
    }

    [Fact]
    public void 段名里的斜杠无损往返()
    {
        var canonical = BookmarkPath.Build(BookmarkPath.RootToken, new[] { "A/B", "C\\D" });
        Assert.Equal(@"@root/A\/B/C\\D", canonical);
        Assert.Equal(new[] { "@root", "A/B", @"C\D" }, BookmarkPath.Split(canonical).ToArray());
    }
}

public class RootAliasIdentityTests
{
    [Theory]
    [InlineData(AppLocale.ZhCn)]
    [InlineData(AppLocale.En)]
    public void 根别名与界面根名一致(AppLocale locale)
    {
        var table = StringTables.For(locale);
        var expected = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [BookmarkPath.RootToken] = new() { table["nav.root.bookmarks"] },
            [BookmarkPath.TrashToken] = new() { table["nav.root.trash"] },
        };
        foreach (var (token, names) in expected)
            foreach (var name in names)
                Assert.True(BookmarkPath.RootAliasSet(token).Contains(name, StringComparer.OrdinalIgnoreCase),
                    $"{token} 缺少 {locale.CodeOf()} 的根显示名「{name}」（契约层别名表与字符串表分叉了）");
    }

    [Fact]
    public void 根级保留名_含两种语言的根名与token()
    {
        var reserved = BookmarkPath.ReservedRootNames();
        Assert.All(new[] { "@root", "@trash", "全部书签", "Bookmarks", "回收站", "Trash" },
            name => Assert.Contains(name, reserved, StringComparer.OrdinalIgnoreCase));
    }
}
