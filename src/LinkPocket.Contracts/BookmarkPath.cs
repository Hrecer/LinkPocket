using System;
using System.Collections.Generic;
using System.Linq;

namespace LinkPocket.Contracts;

/// <summary>
/// 书签路径：<b>canonical 形态是唯一持久 / 输入 / 机器面形态，与界面语言无关</b>
/// （<c>@root/工作/前端</c>）。显示串 <c>全部书签 / 工作 / 前端</c> 与
/// <c>Bookmarks / Work / Frontend</c> 都只是它的投影，不是事实来源。
/// </summary>
/// <remarks>
/// 两个虚根（全部书签 / 回收站）不落库、没有 ID，它们在路径里的身份就是下面两枚 token。
/// <b>不要把显示名写进路径</b>：那等于把界面语言烙进数据库、剪贴板与 AI 契约——换一次语言，
/// 昨天存下的路径今天就解析不出来，而它看起来完全正常。
/// </remarks>
public static class BookmarkPath
{
    /// <summary>段分隔符（段名里的字面 <c>/</c> 由 <see cref="PathText.Escape"/> 转义成 <c>\/</c>）。</summary>
    public const char Separator = '/';

    /// <summary>「全部书签」虚根的 token。</summary>
    public const string RootToken = "@root";

    /// <summary>「回收站」虚根的 token。</summary>
    public const string TrashToken = "@trash";

    /// <summary>
    /// 「路径已不可解析」的哨兵 token：id 指向的文件夹已不在库里时用它，
    /// <b>不得伪装成根、也不得在数据层写中文</b>（显示名由 I18n 投影）。
    /// </summary>
    public const string UnknownToken = "@unknown";

    /// <summary>是不是根 token（大小写不敏感，与地址栏段匹配同口径）。</summary>
    public static bool IsToken(string? name)
        => string.Equals(name, RootToken, StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, TrashToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>根段：token 本身、空串（用户从根开始打字）都算。</summary>
    public static bool IsRootSegment(string? segment) => string.IsNullOrEmpty(segment) || IsToken(segment);

    /// <summary>由"根之后的段名链"拼 canonical 路径：<c>@root/A/B</c>。</summary>
    public static string Build(string rootToken, IEnumerable<string> segments)
        => string.Join(Separator, new[] { rootToken }.Concat(segments.Select(PathText.Escape)));

    /// <summary>在已有 canonical 路径后追加一段（段名转义由这里负责，调用方不再手拼分隔符）。</summary>
    public static string Append(string canonical, string name)
        => canonical + Separator + PathText.Escape(name);

    /// <summary>拆段（含首段 token，段名已解转义）；空串 = 只有根。</summary>
    public static IReadOnlyList<string> Split(string? canonical)
        => string.IsNullOrWhiteSpace(canonical)
            ? new[] { RootToken }
            : PathText.Split(canonical);

    // ── 根别名与根级保留名 ──────────────────────────────────────────────
    // token 天生保留；**各语言的根显示名由组合根在启动时登记**（App → ReserveRootAlias）：
    // 引擎与 UIKit 都不能引 I18n，而"哪个段名代表哪个根""哪些名字被根占用"必须在这两层成立，
    // 所以别名在这里落地。漏登记不会静默放行——探针 language/trash 套件的保留名与路径往返用例（P4/P6）会红。
    // 别名**按根分家**：浏览器路径的首段不许认「回收站」，否则 `回收站/A` 会被解析成 `@root/A`。

    private static readonly Dictionary<string, HashSet<string>> RootAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [RootToken] = new(StringComparer.OrdinalIgnoreCase),
        [TrashToken] = new(StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>登记一枚根显示名（<paramref name="rootToken"/> 只能是 <see cref="RootToken"/> / <see cref="TrashToken"/>）。</summary>
    public static void ReserveRootAlias(string rootToken, string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || !RootAliases.TryGetValue(rootToken, out var set)) return;
        set.Add(alias.Trim());
    }

    /// <summary>某根的全部占用名（token 本身 + 已登记的各语言显示名）。</summary>
    public static IReadOnlyCollection<string> RootAliasSet(string rootToken)
        => RootAliases.TryGetValue(rootToken, out var set)
            ? new[] { rootToken }.Concat(set).ToArray()
            : new[] { rootToken };

    /// <summary>这个段名是不是<paramref name="rootToken"/> 那个根（canonical token 或任一语言的显示名，大小写不敏感）。</summary>
    public static bool MatchesRoot(string rootToken, string? segment)
        => !string.IsNullOrWhiteSpace(segment) && RootAliasSet(rootToken).Contains(segment.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>被**任一**根占用的名字（根级建夹/改名的判据：不许与任何根同名，跨语言一律算）。</summary>
    public static IReadOnlyCollection<string> ReservedRootNames()
        => RootAliases.Keys.Concat(RootAliases.Values.SelectMany(s => s)).ToArray();

    /// <summary>这个名字是否被某个根占用。</summary>
    public static bool IsReservedRootName(string? name)
        => !string.IsNullOrWhiteSpace(name) && ReservedRootNames().Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);
}
