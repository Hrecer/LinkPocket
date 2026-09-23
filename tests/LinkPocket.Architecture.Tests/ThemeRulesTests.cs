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
/// <c>Hct</c> / <c>TonalPalette</c> / <c>ColorScheme</c> 引用（白名单 = 渲染检查工具）。</item>
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

    /// <summary>零字面量检查范围（界面层）。Theming 是令牌锚定表所在地，天然含色值。</summary>
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

    /// <summary>
    /// 按方案 §9.1 的例外清单豁免（每条都要写清理由，不许静默扩大）。
    /// </summary>
    /// <remarks>
    /// 三条豁免的共同特征：**它们的颜色不是"界面配色"，而是那个控件自身的语义内容**。
    /// </remarks>
    private static bool IsLiteralExempt(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return normalized.EndsWith("FaviconService.cs", StringComparison.Ordinal)
            // 取色盘的**色相光谱条**：彩虹谱就是该控件的内容（类似标尺上的刻度），
            // 它不是"界面用哪支紫"这类可主题化的决策 —— 主题换了，色相环仍然是 0°→360° 的那道彩虹。
            // 这是全仓唯一允许出现"颜色谱"的位置；除它之外的取色盘颜色（叠加层端点、预览块、描边）
            // 一律走令牌（App.Text.OnAccent / App.Color.Shadow / App.Color.SvTransparent）。
            || normalized.EndsWith("ColorPickerPopup.xaml", StringComparison.Ordinal);
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
        //
        // ⚠️ 与「零颜色字面量」同源的口径：**只扫代码、剥掉注释**。
        // 本仓的注释习惯是把原理连同术语写下来（例如解释"取色盘为什么用 HSV 而不是 HCT"），
        // 那些是**设计依据**，不是活引用；用裸词扫注释会把人逼去删掉最有价值的说明。
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

            var text = StripComments(File.ReadAllText(file), xaml: false);
            foreach (var b in banned)
            {
                // 词边界 + **后随 `.`**：只认"当作类型用"（Hct.FromColor / ColorScheme.Light）这类真实引用，
                // 避免变量名/标识符里恰好含这几个字母就误判。
                if (Regex.IsMatch(text, $@"\b{Regex.Escape(b)}\s*\."))
                    offenders.Add($"{rel} → {b}.*");
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

    [Fact]
    public void 全仓_零警告色残留()
    {
        // 方案决策 3（破坏性动作不设专门视觉）+ 决策 4（彻底去红）：这些**代码符号与色值**必须彻底消失。
        // 破坏性动作改走 <c>TonalButton</c> / <c>App.Support.*</c>（与次操作共用同一套呈现）。
        //
        // 范围口径（两条，都写清理由，不许静默放宽）：
        // ① **只扫代码，不扫注释**：本仓的注释习惯是把决策依据连同原值一起写下来
        //    （例如"原先写死奶油黄 #F5E9B8 / WarnBg"），这些是**有价值的历史记录**，不是活的引用；
        //    为过闸而删掉它们等于销毁决策依据。
        // ② **不列 `PillTone.Warn`**：它是**语义色调名**（"这是破坏性动作"），T3 后仍然存在且被需要
        //    （映射到 TonalButton）；被淘汰的是它的**外观**（WarnPillButton），不是这个名字。
        var banned = new[] { "WarnBg", "WarnPillButton", "#FFB300", "#E24B4A", "#EDDFA6" };
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

                // 护栏文件自身必须写出禁词才能禁止它们，跳过（否则必然命中自己的清单）。
                var name = Path.GetFileName(file);
                if (name is "ThemeRulesTests.cs" or "LegacyBrushKeyTests.cs") continue;

                var text = StripComments(File.ReadAllText(file), ext == ".xaml");
                foreach (var b in banned)
                    if (text.Contains(b, StringComparison.Ordinal))
                        offenders.Add($"{Relative(file)} → {b}");
            }
        }

        Assert.True(offenders.Count == 0,
            "警告色体系已整体退场（破坏性动作与次操作共用同一套呈现、全站不使用红色）：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void 字体令牌唯一_界面层禁止硬编码字体族()
    {
        // 字体必须经 App.Font.Ui / App.Font.Mono 令牌（运行时换字体的物理前提 = 资源可失效）。
        // 界面层里出现 new FontFamily("…") 或 FontFamily="Consolas" 都意味着"这个控件不跟主题字体走"。
        var offenders = new List<string>();
        var rx = new Regex(@"new\s+FontFamily\s*\(\s*""|FontFamily\s*=\s*""[A-Za-z]", RegexOptions.Compiled);

        foreach (var file in SourceFiles(LiteralCheckedDirs))
        {
            var rel = Relative(file);
            if (IsLiteralExempt(rel)) continue;
            var isXaml = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            var text = StripComments(File.ReadAllText(file), isXaml);
            if (rx.IsMatch(text))
                offenders.Add($"{rel} → 硬编码字体族");
        }

        Assert.True(offenders.Count == 0,
            "字体族只能经 App.Font.Ui / App.Font.Mono 令牌发布：\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// **颜色键的权威清单**：从 Theming 的锚定表源码里抽键名（不引产品程序集 ——
    /// 架构测试的目标框架是 net8.0，引 <c>LinkPocket.Theming</c>（WPF）会把它拖成 windows 专属，
    /// 而本层其余断言全是 csproj/源码文本扫描，靠的就是"零产品引用"）。
    /// </summary>
    /// <remarks>
    /// 用"是不是颜色角色"作判据，而不是用"像不像一个大驼峰词"猜 —— 后者会把
    /// <c>{StaticResource TonalButton}</c>（自有样式）误判成颜色键。
    /// 清单来源 = <c>SurfaceAnchors.Build()</c> 的行表：唯一事实来源仍在 Theming，这里只是读它。
    /// </remarks>
    private static HashSet<string> LibraryColorKeys()
    {
        var path = Path.Combine(RepoRoot, "src", "LinkPocket.Theming", "Color", "SurfaceAnchors.cs");
        Assert.True(File.Exists(path), $"锚定表源码不存在：{path}（颜色键清单的唯一来源）");

        var text = File.ReadAllText(path);
        var start = text.IndexOf("var rows = new (string Key, uint Today)[]", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到锚定表的 rows 声明（SurfaceAnchors 结构变了？请同步本测试）");
        var end = text.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(end > start, "找不到锚定表 rows 的结束位置");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text[start..end], @"\(""([A-Za-z0-9_]+)"",\s*0x"))
            keys.Add(m.Groups[1].Value);
        Assert.True(keys.Count >= 40, $"锚定表解析出的键太少（{keys.Count}）——解析器需要跟着源码结构更新");
        return keys;
    }

    /// <summary>
    /// 界面层引用的令牌键**不得是库角色键**（文字色/图标色/底色/描边一律走 <c>App.*</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么这条必须机器化</b>：库角色键（<c>OnSurface</c> / <c>Primary</c> / <c>SurfaceContainerHigh</c>…）
    /// 与 <c>App.*</c> 令牌**当前同值**，引用错了在界面上**看不出来** —— 于是"哪一处该跟着哪个语义走"
    /// 就散落在各页 XAML 里，换主题/调档位时必然漏改，而且没有任何征兆。
    /// 实测：界面里仍有 <b>190 处</b> 库角色键引用，其中 155 处是文字色。
    /// </para>
    /// <para>
    /// <b>为什么豁免 UIKit 的样式键名</b>：<c>{StaticResource TonalButton}</c> / <c>{StaticResource LpMenuItem}</c>
    /// 这类是**自有样式资源**（不是颜色角色）—— 靠 <see cref="LibraryColorKeys"/> 的清单区分，
    /// 不靠命名形状猜。
    /// </para>
    /// </remarks>
    [Fact]
    public void 界面层_颜色令牌只能引App语义族_不得引库角色键()
    {
        var colorKeys = LibraryColorKeys();
        var offenders = new List<string>();
        var rx = new Regex(@"\{(?:Dynamic|Static)Resource\s+([A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);

        foreach (var file in SourceFiles(LiteralCheckedDirs))
        {
            var rel = Relative(file);
            if (IsLiteralExempt(rel)) continue;
            var isXaml = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            var text = StripComments(File.ReadAllText(file), isXaml);
            foreach (Match m in rx.Matches(text))
            {
                var key = m.Groups[1].Value;
                if (!colorKeys.Contains(key)) continue;   // 样式/转换器键名，不是颜色角色
                offenders.Add($"{rel} → {{Resource {key}}}");
            }
        }

        Assert.True(offenders.Count == 0,
            "界面层只能引 App.* 语义令牌，不得直接引库颜色角色键（App.Text.* / App.Type.* / App.Accent.* / App.Surface.* / App.Line.* …）：\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// **代码侧**（code-behind 用字符串取资源）同样只许引 <c>App.*</c> —— 字符串不会跟着 XAML 一起改名。
    /// </summary>
    /// <remarks>
    /// 与上一条同源：XAML 有编译期可见性，代码里的 <c>FindResource("OnSurface")</c> 没有 ——
    /// 这类漏改历史上真的发生过（<c>PillToneToBrushConverter</c> 的旧画刷键名，
    /// 见 <c>LegacyBrushKeyTests</c>）。故颜色角色的字符串取值单独卡一条。
    /// </remarks>
    [Fact]
    public void 界面层_代码取资源只能引App语义族()
    {
        var colorKeys = LibraryColorKeys();
        var offenders = new List<string>();
        var rx = new Regex(@"\b(?:Try)?FindResource\s*\(\s*""([A-Za-z0-9_.]+)""", RegexOptions.Compiled);

        foreach (var file in SourceFiles(LiteralCheckedDirs))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Relative(file);
            if (IsLiteralExempt(rel)) continue;
            var text = StripComments(File.ReadAllText(file), xaml: false);
            foreach (Match m in rx.Matches(text))
            {
                var key = m.Groups[1].Value;
                if (!colorKeys.Contains(key)) continue;
                offenders.Add($"{rel} → FindResource(\"{key}\")");
            }
        }

        Assert.True(offenders.Count == 0,
            "代码里取颜色资源也只能引 App.* 语义令牌（库角色键与令牌同值，引用错了看不出征兆）：\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// **画刷不得"取出即固化"**：code-behind 里 <c>(Brush)FindResource("…")</c> 取画刷再赋给元素属性，
    /// 值在那一刻被写死成本地值 —— 换主题（= 资源字典替换画刷实例）之后再也**不跟随**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么这条必须机器化</b>：这一族错法没有任何编译期征兆，视觉上只在"换主题之后"才显形。
    /// 回归现象：切换其他主题后，这一栏的颜色始终不跟随 —— 像素取样证明表头
    /// 停在出厂默认紫（<c>App.Surface.Panel</c> 的旧主题值），而顶栏 / 卡面已经跟随。
    /// 正确形态 = <c>element.SetResourceReference(dp, key)</c>（XAML 侧 = <c>{DynamicResource}</c>）。
    /// </para>
    /// <para>
    /// <b>判据</b>：扫 <c>src/</c>（排除令牌与颜色计算的唯一归属地 <c>LinkPocket.Theming</c>）的
    /// <c>*.cs</c>，按 <c>;</c> 切成语句，**同一条语句**里同时出现 <c>(Brush)</c> 强制转换与
    /// <c>FindResource(</c> / <c>TryFindResource(</c> 即判违规。按语句而不是按行切，跨行写法也拦得住。
    /// </para>
    /// <para>
    /// <b>已知覆盖边界</b>（如实写明，不假装全覆盖）：只认 <c>(Brush)</c> 显式转换这一种形状；
    /// 把取画刷藏进私有帮手（<c>BrushOf(key)</c> / <c>TryResource(key)</c>）再在别处赋值的**间接形态**
    /// 不在判据内 —— 这类站点已按同一根因改掉，真要再收口得靠"帮手必须返回资源键"这类更强的约定。
    /// <c>DragVisualAdorner</c> 的 <c>owner.TryFindResource(key) as Brush ?? throw</c> 是**失败暴露**形态
    /// （取不到即抛，不静默兜底色），不是固化赋值，故不被本判据命中。
    /// </para>
    /// </remarks>
    [Fact]
    public void 界面层_代码取画刷必须走资源引用_禁一次性赋值()
    {
        var castRx = new Regex(@"\(\s*(?:System\.Windows\.Media\.)?Brush\s*\)", RegexOptions.Compiled);
        var lookupRx = new Regex(@"\b(?:Try)?FindResource\s*\(", RegexOptions.Compiled);
        var offenders = new List<string>();

        var srcDir = Path.Combine(RepoRoot, "src");
        var themingDir = Path.Combine("src", "LinkPocket.Theming");
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            var rel = Relative(file);
            if (rel.StartsWith(themingDir, StringComparison.OrdinalIgnoreCase)) continue;

            var text = StripCommentsKeepingLines(File.ReadAllText(file));
            var start = 0;
            while (start < text.Length)
            {
                var end = text.IndexOf(';', start);
                if (end < 0) end = text.Length;
                var statement = text[start..end];
                var cast = castRx.Match(statement);
                if (cast.Success && lookupRx.IsMatch(statement))
                {
                    // 行号取**强制转换那一处**（语句可能跨行：按语句起点报会指到无关的行）
                    var from = Math.Max(0, cast.Index - 40);
                    var excerpt = Regex.Replace(statement.Substring(from).Trim(), @"\s+", " ");
                    if (excerpt.Length > 120) excerpt = excerpt[..120] + "…";
                    if (from > 0) excerpt = "…" + excerpt;
                    offenders.Add($"{rel}:{LineOf(text, start + cast.Index)} → {excerpt}"
                                  + "（一次性取画刷赋值会在换主题后固化旧主题色，改用 element.SetResourceReference(dp, key)）");
                }
                start = end + 1;
            }
        }

        Assert.True(offenders.Count == 0,
            "界面层 code-behind 不得用 (Brush)FindResource(...) 取画刷后直接赋值"
            + "（换主题 = 资源字典替换画刷实例，固化值不会跟随；改用 element.SetResourceReference(dp, key)）：\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// 同 <see cref="StripComments"/>，但**保留换行**（本测试要报"文件:行"，块注释吃掉换行会让行号漂移）。
    /// </summary>
    private static string StripCommentsKeepingLines(string text)
    {
        var noBlock = Regex.Replace(text, @"/\*.*?\*/",
            m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()),
            RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", " ");
    }

    /// <summary>偏移量 → 1 基行号。</summary>
    private static int LineOf(string text, int offset)
    {
        var line = 1;
        var limit = Math.Min(offset, text.Length);
        for (var i = 0; i < limit; i++)
            if (text[i] == '\n') line++;
        return line;
    }
}
