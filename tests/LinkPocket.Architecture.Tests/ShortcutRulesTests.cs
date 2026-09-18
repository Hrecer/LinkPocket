using Xunit;

namespace LinkPocket.Architecture.Tests;

/// <summary>
/// 快捷键架构红线（2026-09-19 用户定稿，D9）：
/// **键位只能声明在 LinkPocket.Input 子系统（含浏览页键位表 BrowserShortcuts）**——
/// 全仓其余位置不得出现 <c>&lt;KeyBinding&gt;</c> / <c>KeyDown</c> 处理，杜绝"快捷键散落各页、
/// 键位表与实际行为不一致、同一键在不同页语义打架"的历史问题（本轮已删除 5 处散落实现）。
///
/// <para>**控件级白名单**（例外必须在此登记并写明理由）：输入控件自身的编辑键——
/// 它们的语义属于"文本框编辑"而不是"页面命令"，且只在控件获得焦点时生效，天然不参与页面级仲裁。</para>
/// </summary>
public class ShortcutRulesTests
{
    /// <summary>快捷键子系统落点（唯一允许出现键位声明的地方）。</summary>
    private const string InputSubsystem = "LinkPocket.UIKit/Input";

    /// <summary>
    /// 控件级白名单：`相对路径 => 理由`。
    /// 每条都必须是"控件自持的编辑语义"，不接受任何页面级/业务级键位进入本表。
    /// </summary>
    private static readonly Dictionary<string, string> ControlLevelWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LinkPocket.UIKit/BreadcrumbBar.xaml"] =
            "地址栏编辑框（TextBox）自身的编辑键：Enter 确认路径 / Esc 取消编辑 / Tab 补全候选——" +
            "只在编辑框获得焦点时生效，属控件编辑语义，不参与页面级快捷键仲裁",
        ["LinkPocket.UIKit/BreadcrumbBar.xaml.cs"] =
            "同上：地址栏编辑框的 PreviewKeyDown（候选列表 ↑/↓ 移动）——控件内编辑语义",
    };

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

    /// <summary>遍历 src 下全部源文件（跳过 obj/bin），返回 (相对路径, 绝对路径)。</summary>
    private static IEnumerable<(string Relative, string Full)> SourceFiles()
    {
        var src = Path.Combine(RepoRoot, "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;

            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
                continue;
            yield return (relative, file);
        }
    }

    private static bool IsWhitelisted(string relative)
        => relative.StartsWith(InputSubsystem + "/", StringComparison.OrdinalIgnoreCase)
           || ControlLevelWhitelist.ContainsKey(relative);

    /// <summary>
    /// 违规判定 = 三种真实写法任一命中：XAML 挂点属性、code-behind 事件方法名、代码订阅（+=）。
    /// 抽成纯函数以便自证有效性（见下方用例）。
    /// </summary>
    private static bool HasKeyHandler(string text, string handler)
        => text.Contains($"{handler}=\"", StringComparison.Ordinal)
           || text.Contains($"_{handler}(", StringComparison.Ordinal)
           || text.Contains($"{handler} +=", StringComparison.Ordinal);

    /// <summary>
    /// 检测规则自证：必须能抓到本轮删掉的那几处历史写法（否则红线形同虚设）；
    /// 同时不得误伤"与快捷键无关"的键盘 API 用法（焦点/路由事件常量）。
    /// </summary>
    [Fact]
    public void 检测规则_能抓到历史散落写法且不误伤无关用法()
    {
        // 真实历史写法（本轮删除的 5 处，逐条回放）
        Assert.True(HasKeyHandler("private void TrashPage_PreviewKeyDown(object sender, KeyEventArgs e)", "PreviewKeyDown"));
        Assert.True(HasKeyHandler("Focusable=\"True\"\n             PreviewKeyDown=\"TrashPage_PreviewKeyDown\">", "PreviewKeyDown"));
        Assert.True(HasKeyHandler("PreviewKeyDown += SmartListsPage_PreviewKeyDown;", "PreviewKeyDown"));
        Assert.True(HasKeyHandler("<TextBox x:Name=\"SearchBox\" KeyDown=\"SearchBox_KeyDown\"/>", "KeyDown"));
        Assert.True(HasKeyHandler("private void IdInput_KeyDown(object sender, KeyEventArgs e)", "KeyDown"));

        // 无关用法：焦点管理与路由事件常量不得被判违规（否则红线会逼着人写怪代码）
        Assert.False(HasKeyHandler("Keyboard.Focus(this);", "KeyDown"));
        Assert.False(HasKeyHandler("AddHandler(Keyboard.KeyDownEvent, handler, true);", "KeyDown"));
        Assert.False(HasKeyHandler("private void OnPreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)", "PreviewKeyDown"));
    }

    /// <summary>XAML 键位声明（<c>&lt;KeyBinding&gt;</c>）只允许出现在快捷键子系统与控件级白名单。</summary>
    [Fact]
    public void KeyBinding声明_只允许在Input子系统与控件级白名单()
    {
        var offenders = new List<string>();
        foreach (var (relative, full) in SourceFiles())
        {
            if (!relative.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsWhitelisted(relative)) continue;
            if (File.ReadAllText(full).Contains("<KeyBinding", StringComparison.Ordinal))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "键位声明（<KeyBinding>）散落在快捷键子系统之外，必须迁入 BrowserShortcuts 键位表："
            + string.Join("、", offenders));
    }

    /// <summary>
    /// 键盘事件处理（KeyDown / PreviewKeyDown / KeyUp）只允许出现在快捷键子系统与控件级白名单。
    /// 判定 = 源码里出现事件处理方法名（含 XAML 挂点与 code-behind 订阅），两者任一都算违规。
    /// </summary>
    [Theory]
    [InlineData("KeyDown")]
    [InlineData("PreviewKeyDown")]
    [InlineData("KeyUp")]
    public void 键盘事件处理_只允许在Input子系统与控件级白名单(string handler)
    {
        var offenders = new List<string>();
        foreach (var (relative, full) in SourceFiles())
        {
            if (IsWhitelisted(relative)) continue;
            var text = File.ReadAllText(full);
            if (HasKeyHandler(text, handler)) offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            $"键盘事件处理（{handler}）散落在快捷键子系统之外，必须迁入 BrowserShortcuts 键位表："
            + string.Join("、", offenders));
    }

    /// <summary>白名单必须真实存在（防止文件改名/删除后留下僵尸豁免，悄悄放宽红线）。</summary>
    [Fact]
    public void 控件级白名单_条目真实存在()
    {
        foreach (var relative in ControlLevelWhitelist.Keys)
        {
            var path = Path.Combine(RepoRoot, "src", relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"快捷键白名单指向的文件不存在（僵尸豁免）: {relative}");
        }
    }

    /// <summary>键位唯一事实源必须存在：浏览页键位表（键位声明集中处，防止被整体删除后红线形同虚设）。</summary>
    [Fact]
    public void 浏览页键位表_存在且集中声明键位()
    {
        var path = Path.Combine(RepoRoot, "src", "LinkPocket.UI.Browser", "BrowserShortcuts.cs");
        Assert.True(File.Exists(path), "浏览页键位表 BrowserShortcuts.cs 不存在（键位唯一事实源缺失）");
        var text = File.ReadAllText(path);
        Assert.Contains("ShortcutBinding", text, StringComparison.Ordinal);
    }
}