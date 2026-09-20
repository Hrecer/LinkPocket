using System.Text.RegularExpressions;
using Xunit;

namespace LinkPocket.Architecture.Tests;

/// <summary>
/// **主题系统护栏**：让"系统化"可验证，而不是口号（方案 §9.1）。
/// </summary>
/// <remarks>
/// 四条断言，全部基于源文件文本扫描（编译期事实之外的"形状"约束）：
/// <list type="number">
/// <item><b>颜色计算只允许在 Theming</b>：除 <c>LinkPocket.Theming</c> 外全仓不得出现
/// <c>Hct</c> / <c>TonalPalette</c> / <c>ColorScheme</c> 引用（白名单 = 探针工具）。</item>
/// <item><b>零颜色字面量</b>：<c>UI.*</c> / <c>UIKit</c> / <c>App</c> 的 <c>.xaml</c> 与 <c>.cs</c> 里
/// 不得出现 <c>#RRGGBB</c> / <c>#AARRGGBB</c> / <c>Color.FromRgb</c> / <c>Brushes.*</c>。</item>
/// <item><b>零警告色残留</b>：全仓不得出现 <c>WarnBg</c> / <c>WarnPillButton</c> / <c>PillTone.Warn</c>
/// / <c>#FFB300</c> / <c>#E24B4A</c> / <c>#EDDFA6</c>（方案决策 3/4：警告色退场 + 彻底去红）。</item>
/// <item><b>字体令牌唯一</b>：除令牌发布点外不得出现 <c>FontFamily("…")</c> 字面量。</item>
/// </list>
/// <para>
/// <b>为什么用文本扫描而不是反射</b>：颜色字面量与"用了哪个库类型"都是**源码形状**，
/// 编译成 dll 后字面量会混进 BAML/元数据堆，反而更难判定；且本仓已有同族先例
/// （<c>ShortcutRulesTests</c> 禁 <c>&lt;KeyBinding&gt;</c>）。
/// </para>
/// </remarks>
public class ThemeRulesTests
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

    /// <summary>方案 §9.1 的"颜色计算白名单"：允许出现 Hct/TonalPalette/ColorScheme 的位置。</summary>
    private static readonly string[] ColorMathAllowed =
    {
        Path.Combine("src", "LinkPocket.Theming"),
    };

    /// <summary>零字面量检查范围（界面层）。Theming 是令牌定稿表所在地，天然含色值。</summary>
    private static readonly string[] LiteralCheckedDirs =
    {
        Path.Combine("src", "LinkPocket.UIKit"),
        Path.Combine("src", "LinkPocket.App"),
        Path.Combine("src", "LinkPocket.UI.Browser"),
        Path.Combine("src", "LinkPocket.UI.Search"),
        Path.Combine("src", "LinkPocket.UI.Trash"),
        Path.Combine("src", "LinkPocket.UI.SmartLists"),
        Path.Combine("src", "LinkPocket.UI.Tools"),
        Path.Combine("src", "LinkPocket.UI.Settings"),
    };

    /// <summary>按方案 §9.1 的例外清单豁免（每条都要写清理由，不许静默扩大）。</summary>
    private static bool IsLiteralExempt(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return normalized.EndsWith("FaviconService.cs", StringComparison.Ordinal)   // 占位徽标用 GDI 画 ⬡（非界面颜色语义）
            || normalized.EndsWith("SmartProbe/Program.cs", StringComparison.Ordinal);
    }

    /// <summary>
    /// 去掉注释后再扫（否则大量"文档里提到的色值"会被误判成字面量）。
    /// </summary>
    /// <remarks>
    /// 本仓的注释习惯是**把决策依据连同原值一起写下来**（例如"原先写死 #1F6750A4"），
    /// 这些不是活的色值。注释里的死值恰恰是有价值的历史记录，不该为了过闸而删掉。
    /// </remarks>
    private static string StripComments(string text, bool xaml)
    {
        if (xaml)
            return Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);
        var noBlock = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", " ");
    }

    private static IEnumerable<string> SourceFiles(params string[] dirs)
    {
        foreach (var dir in dirs)
        {
            var full = Path.Combine(RepoRoot, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var f in Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;
                var ext = Path.GetExtension(f);
                if (ext is ".cs" or ".xaml")
                    yield return f;
            }
        }
    }

    private static string Relative(string full) => Path.GetRelativePath(RepoRoot, full);

    [Fact]
    public void 颜色计算只允许出现在Theming()
    {
        // 判据 = 源码里出现库的颜色科学类型名。Theming 之外任何地方算了颜色都是"第二份实现"的起点。
        var banned = new[] { "Hct", "TonalPalette", "ColorScheme" };
        var offenders = new List<string>();

        var srcDir = Path.Combine(RepoRoot, "src");
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var rel = Relative(file);
            if (ColorMathAllowed.Any(a => rel.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
                continue;

            var text = File.ReadAllText(file);
            foreach (var b in banned)
            {
                // 词边界匹配，避免 "Hct" 命中 "Hctx" 这类无关标识符
                if (Regex.IsMatch(text, $@"\b{Regex.Escape(b)}\b"))
                    offenders.Add($"{rel} → {b}");
            }
        }

        Assert.True(offenders.Count == 0,
            "颜色计算（Hct/TonalPalette/ColorScheme）只允许出现在 LinkPocket.Theming：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void 界面层_零颜色字面量()
    {
        var offenders = new List<string>();
        var patterns = new (string Name, Regex Rx)[]
        {
            ("XAML/CS 十六进制色值", new Regex(@"#[0-9A-Fa-f]{6,8}\b", RegexOptions.Compiled)),
            ("Color.FromRgb", new Regex(@"\bColor\.FromRgb\b", RegexOptions.Compiled)),
            ("Color.FromArgb", new Regex(@"\bColor\.FromArgb\b", RegexOptions.Compiled)),
            // Brushes.Transparent / Brushes.White 是"无彩 + 描边色"的绘图原语（命中面、矢量勾），
            // 不承载主题语义，故只禁**有彩色语义**的具名画刷。
            ("Brushes 具名色", new Regex(@"\bBrushes\.(?!Transparent\b|White\b)[A-Za-z]", RegexOptions.Compiled)),
        };

        foreach (var file in SourceFiles(LiteralCheckedDirs))
        {
            var rel = Relative(file);
            if (IsLiteralExempt(rel)) continue;
            var isXaml = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            var text = StripComments(File.ReadAllText(file), isXaml);
            foreach (var (name, rx) in patterns)
                foreach (Match m in rx.Matches(text))
                    offenders.Add($"{rel} → {name}: {m.Value}");
        }

        Assert.True(offenders.Count == 0,
            "界面层不得出现颜色字面量（一律走 App.* 令牌）：\n" + string.Join("\n", offenders));
    }

    [Fact(Skip = "T3 落地前暂缓：警告色体系仍存在于现役代码，本断言是**目标态**护栏（T2/T3 完成后去掉 Skip）。" +
                 "保留在本文件里是为了让「目标态」与「实现」在同一处可见，不让护栏被遗忘。")]
    public void 全仓_零警告色残留()
    {
        // 方案决策 3（破坏性动作不设专门视觉）+ 决策 4（彻底去红）：这些键名与色值必须彻底消失。
        var banned = new[] { "WarnBg", "WarnPillButton", "PillTone.Warn", "#FFB300", "#E24B4A", "#EDDFA6" };
        var offenders = new List<string>();

        foreach (var dir in new[] { "src", "tests" })
        {
            var full = Path.Combine(RepoRoot, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var file in Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;
                var ext = Path.GetExtension(file);
                if (ext is not (".cs" or ".xaml")) continue;

                var text = File.ReadAllText(file);
                foreach (var b in banned)
                    if (text.Contains(b, StringComparison.Ordinal))
                        offenders.Add($"{Relative(file)} → {b}");
            }
        }

        Assert.True(offenders.Count == 0,
            "警告色体系已整体退场（破坏性动作与次操作共用同一套呈现、全站不使用红色）：\n" + string.Join("\n", offenders));
    }

    [Fact(Skip = "T4 落地前暂缓：等宽字面量与 FontFamily 直写仍在现役代码，本断言是**目标态**护栏（T4 完成后去掉 Skip）。")]
    public void 字体令牌唯一_界面层禁止硬编码字体族()
    {
        // 字体必须经 App.Font.Ui / App.Font.Mono 令牌（运行时换字体的物理前提 = 资源可失效）。
        var offenders = new List<string>();
        var rx = new Regex(@"new\s+FontFamily\s*\(\s*""", RegexOptions.Compiled);

        foreach (var file in SourceFiles(LiteralCheckedDirs))
        {
            var rel = Relative(file);
            if (IsLiteralExempt(rel)) continue;
            var text = File.ReadAllText(file);
            if (rx.IsMatch(text))
                offenders.Add($"{rel} → new FontFamily(\"…\")");
        }

        Assert.True(offenders.Count == 0,
            "字体族只能经 App.Font.Ui / App.Font.Mono 令牌发布：\n" + string.Join("\n", offenders));
    }
}
