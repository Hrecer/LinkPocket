using System.Text.RegularExpressions;
using Xunit;

namespace LinkPocket.Architecture.Tests;

/// <summary>
/// **旧画刷键名零残留**：T2 把 <c>TintCard</c> / <c>AccentBtn</c> / <c>WarnBg</c> 等 6 个写死画刷键
/// 搬进了主题令牌体系（<c>App.*</c>），键名不再由 UIKit.xaml 定义。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独一条护栏</b>：XAML 里的 <c>{StaticResource K}</c> 能被文本替换脚本抓到，
/// 但**代码里的字符串字面量**（<c>TryFindResource("WarnBg")</c>、<c>FindResource("AccentBtn")</c>）不会 ——
/// 而 <c>TryFindResource</c> 找不到资源时**静默返回 null**（不抛），表现为"图标不显色 / 底色变透明"
/// 这类看不见摸不着的缺陷。
/// </para>
/// <para>
/// <b>实测</b>：T2 改名后 <c>PillToneToBrushConverter</c> 仍查 <c>"AccentBtn"</c> → 回收站右栏
/// 「还原」图标钮 Foreground = null（渲染检查断言 <c>fg= accent=#FFA18EB0</c> 抓到）。故这条必须机器化。
/// </para>
/// </remarks>
public class LegacyBrushKeyTests
{
    /// <summary>T2 起由主题发布、不再由 UIKit.xaml 定义的旧键名。</summary>
    private static readonly string[] LegacyKeys =
    {
        "TintSurface", "TintCard", "TintPanel", "TintBg", "AccentBtn",
        // 旧删除/警告底色键：本条清单是它唯一的合法留存处（护栏必须写出它才能禁止它）
        "WarnBg",
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

    [Fact]
    public void 界面层_代码与XAML都不得再按旧画刷键名取资源()
    {
        var offenders = new List<string>();
        var srcDir = Path.Combine(RepoRoot, "src");

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            var ext = Path.GetExtension(file);
            if (ext is not (".cs" or ".xaml")) continue;

            var rel = Path.GetRelativePath(RepoRoot, file);
            var text = File.ReadAllText(file);

            foreach (var key in LegacyKeys)
            {
                // 资源查找/引用形态：TryFindResource("X") / FindResource("X") / {Static|DynamicResource X}
                if (Regex.IsMatch(text, $@"(?:Try)?FindResource\(\s*""{Regex.Escape(key)}"""))
                    offenders.Add($"{rel} → FindResource(\"{key}\")");
                if (Regex.IsMatch(text, $@"\{{(?:Static|Dynamic)Resource\s+{Regex.Escape(key)}\s*\}}"))
                    offenders.Add($"{rel} → {{Resource {key}}}");
            }
        }

        Assert.True(offenders.Count == 0,
            "旧画刷键已搬进主题令牌体系，禁止再按旧键名取资源（会静默拿到 null）：\n" + string.Join("\n", offenders));
    }
}
