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
    /// G10：把取词结果**存进状态**——<c>字段/属性 = Loc.T(...)</c> 与表达式体成员 <c>=&gt; Loc.T(...)</c>。
    /// 这两类的取词时机被钉死在赋值那一刻（绑定不会因为"语言变了"而重读），换语言后停在旧语言。
    /// </summary>
    /// <remarks>
    /// 不在此列的三种形状都**在取词那一刻就被消费**，属 §3.4 允许的瞬时用法：
    /// 局部变量（<c>var t = Loc.T(...)</c>）、别的对象的属性（<c>dialog.Title = Loc.T(...)</c>；控件上的赋值一律走 <c>LocText</c>）、
    /// 以及方法体里的 <c>return Loc.T(...)</c>。判据是**存不存得住**，不是"写没写 <c>Loc.T</c>"。
    /// </remarks>
    private static readonly Regex BakedText = new(
        @"^\s*(?:this\.)?[A-Za-z_]\w*\s*=\s*Loc\.(?:T|Plural)\(|=>\s*Loc\.(?:T|Plural)\(",
        RegexOptions.Compiled | RegexOptions.Multiline);

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

    /// <summary>
    /// 日期/时间的**裸格式串**（G7）：界面层不许自己写 <c>ToString("yyyy-MM-dd HH:mm")</c> 这类格式，
    /// 一律走 <c>UiClock</c> 唯一出口。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>口径 = 格式串里出现日期/时间字段</b>（<c>yyyy</c> / <c>MM</c> / <c>dd</c> / <c>HH</c> /
    /// <c>mm</c> / <c>ss</c> / <c>tt</c> 之一），不是"所有 <c>ToString</c>"：
    /// 纯数值格式（<c>"F2"</c> / <c>"F0"</c>）与机器面格式（<c>"O"</c> 往返、<c>"X6"</c> 十六进制）
    /// 不在本闸射程内——它们由"数字一律 Invariant"那条口径管。
    /// </para>
    /// <para>
    /// <b>判据只认 <c>ToString(...)</c> 的字面量实参</b>（<c>.ToString("…")</c>），
    /// 不扫"任何含日期字母的字符串"：后者会把 <c>"System"</c>、<c>"MM/dd"</c> 之类的普通文案
    /// 一并抓进来（假红），而那类文案本来就走 <c>Loc</c> 键。
    /// </para>
    /// <para>
    /// <b>唯一豁免 = <c>I18n/UiClock.cs</c> 自身</b>（它就是要写格式串的那一处）。
    /// 引擎侧与 Theming 的日期格式（日志文件名 / 备份时间戳 / SQL 时间戳）不在此列：
    /// 它们是机器面，必须与界面语言无关。
    /// </para>
    /// </remarks>
    private static readonly Regex BareDateFormat = new(
        @"\.ToString\(\s*""(?=[^""\r\n]*\b(?:yyyy|MM|dd|HH|mm|ss|tt)\b)[^""\r\n]*""",
        RegexOptions.Compiled);

    private static readonly string[] ClockHome = { "src/LinkPocket.I18n/UiClock.cs" };

    /// <summary>G7：界面层零裸日期格式串（一律走 <c>UiClock</c>）。</summary>
    [Fact]
    public void 界面层_零裸日期格式串()
    {
        var offenders = new List<string>();
        foreach (var file in CsFiles(UiDirs))
        {
            var rel = Relative(file);
            if (ClockHome.Contains(rel, StringComparer.Ordinal)) continue;
            var body = Strip(File.ReadAllText(file), xaml: false);
            var line = 0;
            foreach (var raw in File.ReadAllLines(file))
            {
                line++;
                if (raw.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match m in BareDateFormat.Matches(Strip(raw, xaml: false)))
                    offenders.Add($"{rel}:{line} → {m.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "界面层出现裸日期格式串（换语言后这里会停在旧语言的格式上）：\n" +
            string.Join("\n", offenders) +
            "\n请改走 `UiClock.Format` / `UiClock.Text`（I18n 的日期唯一出口）。");
    }

    /// <summary>
    /// 判据自查：G7 的正则必须**量得到东西**（否则它可能一直在空跑）。
    /// </summary>
    /// <remarks>
    /// 本仓纪律：放宽/新增判据时必须留"它能红"的证据（同族见探针 P9 的负向对照）。
    /// 这里用合成样本，不依赖仓库里恰好有没有违规。
    /// </remarks>
    [Fact]
    public void 界面层_零裸日期格式串_判据能红()
    {
        foreach (var bad in new[]
                 {
                     """x.ToString("yyyy-MM-dd HH:mm")""",
                     """y.ToString("MM/dd/yyyy h:mm tt")""",
                     """z.ToString("HH:mm:ss")""",
                 })
        {
            Assert.True(BareDateFormat.IsMatch(bad), $"G7 抓不到这条裸格式串：{bad}");
        }

        foreach (var good in new[]
                 {
                     """x.ToString("F2", CultureInfo.InvariantCulture)""",
                     """y.ToString("O")""",
                     """z.ToString()""",
                     """w.ToString("X6")""",
                     """var label = "System fonts";""",
                 })
        {
            Assert.False(BareDateFormat.IsMatch(good), $"G7 误报（本不该在射程内）：{good}");
        }
    }

    /// <summary>承载用户可见文案的属性（<see cref="XamlTextAttr"/> 的取值面）。</summary>
    private static readonly Regex XamlBindingToText = new(
        @"\b(Text|Content|Header|HeaderText|ToolTip|Tag)\s*=\s*""\{Binding\s+(?<path>[^""{}]+?)\s*\}""",
        RegexOptions.Compiled);

    /// <summary>
    /// <c>LocValue</c> 型的公开属性名（模型里流的那一类）。这些成员**必须**经 <c>{loc:Value}</c> 取词。
    /// </summary>
    /// <remarks>
    /// 形状扫描：属性声明 <c>LocValue Foo</c> / <c>LocValue? Foo</c>，或表达式体 <c>LocValue Foo =&gt; …</c>。
    /// </remarks>
    private static readonly Regex LocValueMember = new(
        @"\bLocValue\??\s+(?<name>[A-Za-z_]\w*)\s*(?:\{|=>|$)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// <c>{Binding X}</c> 里**只许直接绑定（单段路径）**的成员名：同名但类型不是 <see cref="LocValue"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本规则按"成员名"判（不解析 DataContext 类型：那要跑 BAML，脆弱且与其它源码扫描规则不同族），
    /// 于是"别处有个同名的 <c>LocValue</c> 成员"会被误报。实测只有 <c>Name</c> 属于这一类：
    /// <c>BrowserNode.Name</c> / <c>TrashNode.Name</c> 是**用户数据**（文件夹名 / 书签名），
    /// 两个页面的 XAML 里各有一处 <c>{Binding Name}</c>。
    /// </para>
    /// <para>
    /// ⚠️ <b>豁免只对"直接绑定"（路径里没有点）生效</b>——这一条是被真实事故逼出来的：
    /// <c>{Binding Title}</c>（入口卡片上的用户数据侧）与 <c>{Binding ResultViewModel.Title}</c>
    /// （结果页标题，值是 <c>LocValue</c>）在**成员名**上完全一样，早先按名字一刀切豁免，
    /// 于是结果页那三处裸绑（标题 / 副标题 / 计数句）在闸下静默通过，界面上画出了
    /// <c>LocValue { Key = smartlists.preset.mostVisited, … }</c>。
    /// 判据收紧为：**带前缀的路径（有点）= 明确指向某个对象的成员，一律按 <c>LocValue</c> 判**，
    /// 不加豁免；只有无前缀的直接绑定才可能落在"用户数据那一侧"。
    /// </para>
    /// </remarks>
    private static readonly string[] DirectBindingExemptNames = { "Name" };

    /// <summary>
    /// 禁止把 <c>LocValue</c> 直接绑到文案属性上 —— 那会画出 C# 记录字符串
    /// （<c>LocValue { Key = appearance.slot.n, Args = System.ReadOnlyMemory&lt;Object&gt;… }</c>），
    /// <b>两种语言都坏</b>：它既是 ASCII（"英文界面零 CJK"抓不到），又从来不等于上一种语言的文本
    /// （"旧语言文本为零"也抓不到）——两个动态闸都放它过去，所以必须有这条静态闸。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 实测事故（收口阶段四时发现，PNG 基线里一直看得见）：智能列表入口卡片（标题 + 副标题）、
    /// 工具页左栏工具名、外观面板的色槽序号与色值、「派生摘要」、以及两处右键菜单的「删除」项，
    /// 共 <b>8 处</b>写成 <c>{Binding Title}</c> 这类裸绑定，界面上直接画出记录字符串。
    /// </para>
    /// <para>
    /// 判据 = "绑定的路径段里有任何一个名字，是界面层某个 <c>LocValue</c> 型成员的名字"；
    /// 唯一的豁免是 <see cref="DirectBindingExemptNames"/>，且**只对直接绑定生效**（见那里的说明）。
    /// <b>漏报的代价</b>（用户界面画出记录字符串）远大于误报。
    /// </para>
    /// <para>
    /// 闸只管<b>形状</b>，管不到"值是不是真的会随语言重算"——所以配套还有两条：
    /// 探针逐页扫可视树里的 <c>LocValue {</c> 文本（动态总闸），以及"文案值不许是 <c>string</c>"这条口径
    /// （<c>TotalCountText</c> 曾返回 <c>string</c>：形状上不是裸绑，但同样是"冻结在取词那一刻"）。
    /// </para>
    /// </remarks>
    [Fact]
    public void 界面层_不许把LocValue裸绑到文案属性上()
    {
        var locValueMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in CsFiles(UiDirs))
            foreach (Match m in LocValueMember.Matches(Strip(File.ReadAllText(file), xaml: false)))
                locValueMembers.Add(m.Groups["name"].Value);

        Assert.True(locValueMembers.Count > 0,
            "一个 LocValue 型成员都没扫到——扫描口径与代码形状漂移了（本规则会静默空跑）");
        Assert.True(DirectBindingExemptNames.Length == 1,
            "豁免清单的规模变了：请确认新增的豁免真的是'同名但类型不是 LocValue'且只用于直接绑定，再改这个数字");

        var offenders = new List<string>();
        foreach (var file in Files(UiDirs).Where(f => f.EndsWith(".xaml", StringComparison.Ordinal)))
        {
            var body = Strip(File.ReadAllText(file), xaml: true);
            foreach (Match m in XamlBindingToText.Matches(body))
            {
                var path = m.Groups["path"].Value.Trim();
                var direct = !path.Contains('.', StringComparison.Ordinal);
                var hit = path.Split('.', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(seg => locValueMembers.Contains(seg)
                                           && !(direct && DirectBindingExemptNames.Contains(seg, StringComparer.Ordinal)));
                if (hit is not null) offenders.Add($"{Relative(file)} → {{{hit}}}（路径 {path}）");
            }
        }

        Assert.True(offenders.Count == 0,
            "以下位置把 LocValue 直接绑到了 Text/Content/ToolTip 上——界面上会画出 `LocValue { Key = … }` 记录字符串。" +
            "请改成 `{loc:Value 成员名}`（与 `{loc:Loc}` 同一套版本失效机制）：\n" + string.Join("\n", offenders.Distinct()));
    }

    /// <summary>
    /// 判据自查：这条闸**抓得到**"带前缀路径的裸绑"、且**不误报**"直接绑定的用户数据同名成员"。
    /// </summary>
    /// <remarks>
    /// 存在的理由是一次真实漏报：豁免按**成员名**一刀切（<c>Name</c>/<c>Title</c>/<c>Subtitle</c>），
    /// 于是结果页的 <c>{Binding ResultViewModel.Title}</c>（值是 <c>LocValue</c>）被当成
    /// "明细栏里那个同名的用户数据成员"放过去了——界面上画出记录字符串，而闸全绿。
    /// 收紧判据（豁免只对直接绑定生效）之后，用这条用例把"能红"钉住：本仓纪律，
    /// 改判据必须同时给出"它量得到东西"的证据（与探针 P9 的负向对照同一条）。
    /// </remarks>
    [Fact]
    public void 界面层_不许把LocValue裸绑到文案属性上_判据能红()
    {
        var locValueMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "Title",      // 结果页标题（LocValue）
            "Subtitle",   // 结果页副标题（LocValue）
            "Name",       // 与用户数据同名（豁免只对直接绑定生效）
        };
        const string exemptFromDirect = "Name";

        bool IsOffender(string bindingPath)
        {
            var path = bindingPath.Trim();
            var direct = !path.Contains('.', StringComparison.Ordinal);
            return path.Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Any(seg => locValueMembers.Contains(seg)
                            && !(direct && seg == exemptFromDirect));
        }

        // 必须红：带前缀 = 明确指向某个对象的成员，一律按 LocValue 判
        Assert.True(IsOffender("ResultViewModel.Title"));
        Assert.True(IsOffender("ResultViewModel.Name"));      // 带前缀的 Name 也不豁免
        Assert.True(IsOffender("ResultViewModel.Subtitle"));
        Assert.True(IsOffender("ResultViewModel.Details.Name"));   // 多段路径同样按段判

        // 必须绿：直接绑定的用户数据同名成员（文件树节点名 / 回收站单元名）
        Assert.False(IsOffender("Name"));
        // 必须绿：名字压根不在 LocValue 成员集合里（真正的用户数据成员）
        Assert.False(IsOffender("TitleCopy"));
    }
}
