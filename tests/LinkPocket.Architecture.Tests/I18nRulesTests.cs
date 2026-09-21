using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LinkPocket.Architecture.Tests;

/// <summary>
/// 零硬编码文案护栏（i18n 的棘轮）：界面文案必须是**键**，字面量只能越来越少、不许变多。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么带基线而不是直接全红</b>：存量约 1.2 千行中文，一次改不完；没有棘轮，
/// "改一批"与"又漏一批"在 CI 上长得一模一样。基线记的是**每个文件还剩几处**，
/// 只许减不许增——改多了要显式回去调低数字，这个动作本身就是"又收口了一批"的凭证。
/// </para>
/// <para>
/// 扫描口径与 <see cref="ThemeRulesTests"/> 同族：扫源码文本（不扫 dll/BAML）、先剥注释再判
/// （本仓习惯把决策依据连同原文写进注释，那不是活字面量）。
/// </para>
/// <para>
/// 收口完成的判据不是这里归零——是探针 <c>language</c> 套件的"英文界面零中文"总闸
/// 能在**不带排除面**的情况下跑过全页面。这里的 G1/G2 归零只是同一件事的静态影子。
/// </para>
/// </remarks>
public class I18nRulesTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LinkPocket.sln")))
                dir = dir.Parent!;
            return dir!.FullName;
        }
    }

    /// <summary>界面层（用户看得见的文案都在这里）。</summary>
    private static readonly string[] UiDirs =
    {
        "LinkPocket.UIKit", "LinkPocket.App", "LinkPocket.UI.Browser", "LinkPocket.UI.Search",
        "LinkPocket.UI.Trash", "LinkPocket.UI.SmartLists", "LinkPocket.UI.Tools", "LinkPocket.UI.Settings",
    };

    /// <summary>
    /// 中文文案的唯一归属地。除此之外，<c>src/**</c> 的 C# 里不许出现任何 CJK 字面量：
    /// 引擎层带英文技术文案，UI 层带键与 <c>LocValue</c>，界面语言只在渲染边界生效。
    /// </summary>
    private static readonly string[] CopyHome =
    {
        "src/LinkPocket.I18n/StringTables.cs",
    };

    /// <summary>
    /// 身份数据豁免：<b>中文在这里是名字本身，不是文案</b>——路径首段要能被旧串认出、根级不许用户占用这些名字，
    /// 两条判断都发生在不许引 I18n 的层。加一项必须写清这个理由，且必须有配套的一致性测试
    /// （<c>RootAliasIdentityTests</c> 卡住别名表 == 字符串表的 <c>nav.root.*</c>）；
    /// 想用它给硬编码文案开后门，请先问那条测试为什么没红。
    /// </summary>
    private static readonly string[] IdentityData =
    {
        "src/LinkPocket.Contracts/BookmarkPath.cs",
    };

    private static readonly string Cjk = @"[\u4E00-\u9FFF]";

    /// <summary>XAML 里承载用户可见文案的属性。</summary>
    private static readonly Regex XamlTextAttr = new(
        @"(?<attr>\b(Text|Content|Header|HeaderText|ToolTip|Tag|PlaceholderText)\s*=\s*"")(?<val>[^""<>]*" + Cjk + @"[^""<>]*)""",
        RegexOptions.Compiled);

    private static readonly Regex AnyCjkLiteral = new(
        @"""[^""\r\n]*" + Cjk + @"[^""\r\n]*""", RegexOptions.Compiled);

    /// <summary>
    /// G9：界面层把引擎文本直接当话说（<c>ex.Message</c> / <c>err.Error.Message</c> / <c>HumanSummary</c>）。
    /// 引擎文本按定稿是英文技术文案，上屏就是混语；界面只许说键或"码 + 参数"。
    /// </summary>
    private static readonly Regex RendersEngineText = new(
        @"\b(ex|e|err|error|exception)\.Message\b|\.Error\.Message\b|\bHumanSummary\b",
        RegexOptions.Compiled);

    /// <summary>
    /// G10：把取词结果**存进状态**（<c>Status = Loc.T(...)</c>、<c>string X =&gt; Loc.T(...)</c>）。
    /// 取词时机被钉死在构造期/求值期，换语言就不跟着变——模型成员只许流 <c>Loc.K(...)</c> 的 <c>LocValue</c>。
    /// 作实参用（弹窗显示那一刻取词）不在此列，所以只匹配赋值与表达式体。
    /// </summary>
    private static readonly Regex BakedText = new(
        @"(=|=>)\s*Loc\.T\(|\bLoc\.Plural\(",
        RegexOptions.Compiled);

    private static string BaselinePath => Path.Combine(RepoRoot, "tests", "LinkPocket.Architecture.Tests", "I18nBaseline.txt");

    /// <summary>基线：`规则|相对路径|处数`。缺失的规则按"0 处"处理（新文件一律从严）。</summary>
    private static Dictionary<string, int> LoadBaseline()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(BaselinePath)) return map;
        foreach (var line in File.ReadAllLines(BaselinePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var parts = line.Split('|');
            if (parts.Length != 3) continue;
            map[$"{parts[0]}|{parts[1]}"] = int.Parse(parts[2]);
        }
        return map;
    }

    private static IEnumerable<string> Files(params string[] dirs)
    {
        foreach (var dir in dirs)
        {
            var full = Path.Combine(RepoRoot, "src", dir);
            if (!Directory.Exists(full)) continue;
            foreach (var f in Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories))
            {
                if (!IsGenerated(f)) yield return f;
            }
        }
    }

    /// <summary>src 下所有源文件（排除 bin/obj 与文案归属地）。</summary>
    private static IEnumerable<string> AllSourceFiles()
    {
        foreach (var f in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.*", SearchOption.AllDirectories))
        {
            if (IsGenerated(f)) continue;
            var rel = Relative(f);
            if (CopyHome.Contains(rel, StringComparer.Ordinal) || IdentityData.Contains(rel, StringComparer.Ordinal)) continue;
            yield return f;
        }
    }

    private static bool IsGenerated(string f)
        => f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
           f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    private static string Strip(string text, bool xaml)
        => xaml
            ? Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline)
            : Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline), @"//[^\r\n]*", " ");

    private static string Relative(string full) => Path.GetRelativePath(RepoRoot, full).Replace('\\', '/');

    private static IEnumerable<string> CsFiles(params string[] dirs)
        => Files(dirs).Where(f => f.EndsWith(".cs", StringComparison.Ordinal));

    /// <summary>一条规则的实际命中（按文件计数 + 留几行样本给人看）。</summary>
    private sealed record RuleHit(string Rule, string Path, int Count, List<string> Samples);

    private static List<RuleHit> Scan(string rule, IEnumerable<string> files, Func<string, int> counter)
    {
        var hits = new List<RuleHit>();
        foreach (var path in files.Distinct(StringComparer.Ordinal))
        {
            var body = Strip(File.ReadAllText(path), Path.GetExtension(path) == ".xaml");
            var count = counter(body);
            if (count > 0) hits.Add(new RuleHit(rule, Relative(path), count, new List<string>()));
        }
        return hits;
    }

    private static void AssertRatchet(IEnumerable<RuleHit> hits, Dictionary<string, int> baseline, string rule)
    {
        var over = hits.Where(h => h.Count > baseline.GetValueOrDefault($"{rule}|{h.Path}", 0)).ToList();
        Assert.True(over.Count == 0,
            $"{rule}：以下文件的硬编码文案**比基线多**（只许减不许增；确实要新增界面文案，" +
            $"请走 Loc 键 + 填两张表，而不是写字面量）：\n" +
            string.Join("\n", over.Select(h => $"  {h.Path}: {h.Count} 处（基线 {baseline.GetValueOrDefault($"{rule}|{h.Path}", 0)}）")));

        var stale = baseline.Keys
            .Where(k => k.StartsWith(rule + "|", StringComparison.Ordinal))
            .Select(k => k[(rule.Length + 1)..])
            .Where(p => hits.All(h => h.Path != p))
            .ToList();
        Assert.True(stale.Count == 0,
            $"{rule}：基线里这些文件已经没有命中了，把对应行删掉（棘轮不许留着空额度）：\n" +
            string.Join("\n", stale.Select(p => $"  {p}")));
    }

    /// <summary>
    /// 基线生成器（不是断言）：<c>LP_I18N_BASELINE=1 dotnet test --filter 基线生成</c> 重算并落盘。
    /// 每收口一批就重跑一次——棘轮的"减"要留下凭证，手工改数字容易改错方向。
    /// </summary>
    [Fact]
    public void 基线生成()
    {
        if (Environment.GetEnvironmentVariable("LP_I18N_BASELINE") != "1")
            return;   // 常态下空跑：这条只在人工重算基线时有作用

        var lines = new List<string>
        {
            "# 零硬编码文案基线：`规则|相对路径|处数`。只许减不许增；一批收口完重跑生成器。",
        };
        lines.AddRange(Scan("xaml", Files(UiDirs),
            b => XamlTextAttr.Matches(b).Count(m => m.Groups["val"].Value.Trim().Length > 0))
            .Select(h => $"xaml|{h.Path}|{h.Count}"));
        lines.AddRange(Scan("cjk", AllSourceFiles().Where(f => f.EndsWith(".cs", StringComparison.Ordinal)),
            b => AnyCjkLiteral.Matches(b).Count)
            .Select(h => $"cjk|{h.Path}|{h.Count}"));
        lines.AddRange(Scan("g9", CsFiles(UiDirs), b => RendersEngineText.Matches(StripLogStatements(b)).Count)
            .Select(h => $"g9|{h.Path}|{h.Count}"));
        lines.AddRange(Scan("g10", CsFiles(UiDirs), b => BakedText.Matches(b).Count)
            .Select(h => $"g10|{h.Path}|{h.Count}"));

        File.WriteAllLines(BaselinePath, lines.Order(StringComparer.Ordinal), new System.Text.UTF8Encoding(false));
    }

    [Fact]
    public void 界面层_XAML属性零中文文案()
    {
        var baseline = LoadBaseline();
        var hits = Scan("xaml", Files(UiDirs),
            body => XamlTextAttr.Matches(body).Count(m => m.Groups["val"].Value.Trim().Length > 0));
        AssertRatchet(hits, baseline, "xaml");
    }

    /// <summary>
    /// 全仓源文件零中文字面量（注释除外）。<b>只有一条规则、没有形状白名单</b>：
    /// 早先按属性名白名单判（<c>Status=</c>/<c>Label=</c>…）会被构造函数的位置实参绕过——
    /// <c>ShortcutCatalog</c> 的 139 条中文说明就是这么躲过"界面层已清零"的。
    /// </summary>
    [Fact]
    public void 全仓源文件_零中文字面量_注释除外()
    {
        var baseline = LoadBaseline();
        var hits = Scan("cjk", AllSourceFiles().Where(f => f.EndsWith(".cs", StringComparison.Ordinal)),
            body => AnyCjkLiteral.Matches(body).Count);
        AssertRatchet(hits, baseline, "cjk");
    }

    /// <summary>
    /// G9：界面层不得把引擎文本（<c>ex.Message</c> / <c>err.Error.Message</c> / <c>HumanSummary</c>）当话说。
    /// <para>
    /// 日志门面调用整段先抹掉再计数：把异常原文写进日志是观测面的正当用法，
    /// 这条闸只管"上屏"。与旧的 <c>StripLogCalls</c> 不同——那条是为了让中文日志过关（已随定稿 v2 作废），
    /// 这条是因为日志本来就不在 G9 的射程里。
    /// </para>
    /// </summary>
    private static string StripLogStatements(string body)
        => Regex.Replace(body, @"LpLog\.\w+\s*\([^;]*\)\s*;?", " ", RegexOptions.Singleline);

    /// <summary>G9：界面层不得把引擎文本当话说（日志除外，见 <see cref="StripLogStatements"/>）。</summary>
    [Fact]
    public void 界面层_不渲染引擎文本()
    {
        var baseline = LoadBaseline();
        var hits = Scan("g9", CsFiles(UiDirs), body => RendersEngineText.Matches(StripLogStatements(body)).Count);
        AssertRatchet(hits, baseline, "g9");
    }

    /// <summary>G10：取词结果不许存进状态；模型成员只许流 <c>LocValue</c>（<c>Loc.K</c>）。</summary>
    [Fact]
    public void 界面层_不把取词结果存进状态()
    {
        var baseline = LoadBaseline();
        var hits = Scan("g10", CsFiles(UiDirs), body => BakedText.Matches(body).Count);
        AssertRatchet(hits, baseline, "g10");
    }

    [Fact]
    public void 字符串表_两表键对称且无空文本()
    {
        var path = Path.Combine(RepoRoot, "src", "LinkPocket.I18n", "StringTables.cs");
        Assert.True(File.Exists(path), "字符串表文件不存在");
        var rows = Regex.Matches(File.ReadAllText(path), @"new StringRow\(\s*""([^""]+)""\s*,\s*""((?:\\.|[^""\\])*)""\s*,\s*""((?:\\.|[^""\\])*)""");
        Assert.True(rows.Count > 0, "一行都没解析到——扫描口径与表形状漂移了");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in rows.Cast<Match>())
        {
            var key = m.Groups[1].Value;
            Assert.True(seen.Add(key), $"键重复：{key}");
            // 段首小写、其后允许 camelCase（restoreToRoot 比 restore_to_root 读得动）；变体只认三个
            Assert.Matches(@"^[a-z][a-z0-9]*(\.[a-z][a-zA-Z0-9]*)+(#(one|other|short))?$", key);
            Assert.NotEqual("", m.Groups[2].Value.Trim());
            Assert.NotEqual("", m.Groups[3].Value.Trim());
        }
    }

    [Fact]
    public void 界面引用的键_必须在表里存在()
    {
        var tablePath = Path.Combine(RepoRoot, "src", "LinkPocket.I18n", "StringTables.cs");
        var keys = Regex.Matches(File.ReadAllText(tablePath), @"new StringRow\(\s*""([^""]+)""")
            .Cast<Match>().Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var referenced = new List<string>();
        foreach (var file in Files(UiDirs).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var body = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(body, @"(?:\{loc:Loc\s+([a-z0-9.]+(?:#[a-z]+)?)\})|(?:Loc\.(?:T|K|PluralK)\(\s*""([a-z0-9.]+(?:#[a-z]+)?)"")"))
            {
                var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!keys.Contains(key)) referenced.Add($"{Relative(file)} → {key}");
            }
        }

        Assert.True(referenced.Count == 0,
            "界面引用了表里不存在的键（运行时只会显示 ⟨key⟩，属真缺陷）：\n" + string.Join("\n", referenced.Distinct()));
    }
}
