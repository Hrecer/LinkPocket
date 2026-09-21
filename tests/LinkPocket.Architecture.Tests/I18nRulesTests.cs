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

    /// <summary>非界面层：只许携带键与错误码，不许携带任何中文散文。</summary>
    private static readonly string[] EngineDirs =
    {
        "LinkPocket.Contracts", "LinkPocket.Kernel", "LinkPocket.Engine", "LinkPocket.Data",
        "LinkPocket.Theming", "LinkPocket.Composition", "LinkPocket.Diagnostics",
    };

    private static readonly string[] EngineModules = Directory
        .EnumerateDirectories(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"))
        .Select(Path.GetFileName)
        .Where(n => n!.StartsWith("LinkPocket.Modules.", StringComparison.Ordinal))
        .Cast<string>()
        .ToArray();

    private static readonly string Cjk = @"[\u4E00-\u9FFF]";

    /// <summary>XAML 里承载用户可见文案的属性。</summary>
    private static readonly Regex XamlTextAttr = new(
        @"(?<attr>\b(Text|Content|Header|HeaderText|ToolTip|Tag|PlaceholderText)\s*=\s*"")(?<val>[^""<>]*" + Cjk + @"[^""<>]*)""",
        RegexOptions.Compiled);

    /// <summary>C# 里"赋给界面属性的中文字面量"（含插值串与逐字串前缀）。</summary>
    private static readonly Regex CsUiAssignment = new(
        @"\b(Status|StatusText|ErrorMessage|Error|Label|Tip|Title|Text|Content|Header|ToolTip|Tooltip|DisplayName|DescriptionText|OpenLabel|CloseLabel|DeleteActionLabel|DeleteSelectionLabel|Hint|Message|Caption|Subtitle)\s*=\s*@?""[^""\r\n]*" + Cjk + @"[^""\r\n]*""",
        RegexOptions.Compiled);

    /// <summary>直接进界面控件的中文实参（弹窗/提示/列定义构造）。</summary>
    private static readonly Regex CsUiCallArgument = new(
        @"\b(MessageBox\.Show|ShowError|ShowInfo|ShowWarning|AddColumn|ConfirmAsync)\s*\([^;""{]*""[^""\r\n]*" + Cjk,
        RegexOptions.Compiled);

    private static readonly Regex AnyCjkLiteral = new(
        @"""[^""\r\n]*" + Cjk + @"[^""\r\n]*""", RegexOptions.Compiled);

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
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                yield return f;
            }
        }
    }

    private static string Strip(string text, bool xaml)
        => xaml
            ? Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline)
            : Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline), @"//[^\r\n]*", " ");

    private static string Relative(string full) => Path.GetRelativePath(RepoRoot, full).Replace('\\', '/');

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
        lines.AddRange(Scan("cs-ui", Files(UiDirs),
            b => CsUiAssignment.Matches(b).Count + CsUiCallArgument.Matches(b).Count)
            .Select(h => $"cs-ui|{h.Path}|{h.Count}"));
        lines.AddRange(Scan("engine", Files(EngineDirs.Concat(EngineModules).ToArray()),
            b => AnyCjkLiteral.Matches(StripLogCalls(b)).Count)
            .Select(h => $"engine|{h.Path}|{h.Count}"));

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

    [Fact]
    public void 界面层_C_赋值给界面属性的中文零容忍()
    {
        var baseline = LoadBaseline();
        var hits = Scan("cs-ui", Files(UiDirs),
            body => CsUiAssignment.Matches(body).Count + CsUiCallArgument.Matches(body).Count);
        AssertRatchet(hits, baseline, "cs-ui");
    }

    /// <summary>
    /// 日志门面调用整段先抹掉再计数。
    /// <para>
    /// <b>为什么</b>：日志按定稿<b>不翻译</b>（工程观测面，双语只会让 grep 变难），
    /// 而它与"会到界面上的中文散文"住在同一个文件、同一种字面量形状里。
    /// 不区分就会把 116 条日志连同 146 条异常一起记成待收口，棘轮数字失去意义。
    /// </para>
    /// </summary>
    private static string StripLogCalls(string body)
        => Regex.Replace(body, @"LpLog\.\w+\s*\([^;]*\)\s*;?", " ", RegexOptions.Singleline);

    [Fact]
    public void 引擎与契约层_零中文散文_日志除外()
    {
        var baseline = LoadBaseline();
        var dirs = EngineDirs.Concat(EngineModules).ToArray();
        var hits = Scan("engine", Files(dirs), body => AnyCjkLiteral.Matches(StripLogCalls(body)).Count);
        AssertRatchet(hits, baseline, "engine");
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
            foreach (Match m in Regex.Matches(body, @"(?:\{loc:Loc\s+([a-z0-9.]+(?:#[a-z]+)?)\})|(?:Loc\.T\(\s*""([a-z0-9.]+(?:#[a-z]+)?)"")"))
            {
                var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!keys.Contains(key)) referenced.Add($"{Relative(file)} → {key}");
            }
        }

        Assert.True(referenced.Count == 0,
            "界面引用了表里不存在的键（运行时只会显示 ⟨key⟩，属真缺陷）：\n" + string.Join("\n", referenced.Distinct()));
    }
}
