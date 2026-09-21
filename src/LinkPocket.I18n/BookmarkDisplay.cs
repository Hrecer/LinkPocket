using System;
using System.Collections.Generic;
using System.Linq;
using LinkPocket.Contracts;

namespace LinkPocket.I18n;

/// <summary>
/// canonical 路径 / 段 → 界面显示串的<b>唯一投影点</b>。
/// </summary>
/// <remarks>
/// 数据里只有 <c>@root/A/B</c>；这里把首段换成当前语言的根名，其余段是**用户数据**，
/// 一律原样（书签标题、文件夹名永不翻译）。显示分隔符 <c>" / "</c> 属渲染，不是数据。
/// </remarks>
public static class BookmarkDisplay
{
    /// <summary>显示层段分隔符。</summary>
    public const string Separator = " / ";

    /// <summary>根名键（顺序 = 与 <see cref="BookmarkPath.RootToken"/> / <see cref="BookmarkPath.TrashToken"/> 对应）。</summary>
    private static readonly (string Token, string Key)[] Roots =
    {
        (BookmarkPath.RootToken, "nav.root.bookmarks"),
        (BookmarkPath.TrashToken, "nav.root.trash"),
    };

    /// <summary>单段：根 token 与断链哨兵 → 当前语言的显示名；其它段是用户数据，原样返回。</summary>
    public static string Segment(string? segment)
    {
        if (string.Equals(segment, BookmarkPath.UnknownToken, StringComparison.OrdinalIgnoreCase))
            return Loc.T("path.unknown");
        foreach (var (token, key) in Roots)
            if (string.Equals(segment, token, StringComparison.OrdinalIgnoreCase)) return Loc.T(key);
        return segment ?? string.Empty;
    }

    /// <summary>canonical 路径 → 显示串。</summary>
    public static string Path(string? canonical)
        => string.Join(Separator, BookmarkPath.Split(canonical).Select(Segment));

    /// <summary>
    /// 把**各语言**（不只当前语言）的根显示名登记为根别名与根级保留名；组合根启动时调一次。
    /// </summary>
    /// <remarks>
    /// 为什么登记进契约层而不是让引擎直接引 I18n：路径首段的匹配发生在 UIKit，
    /// 根级占用名的校验发生在 Kernel，两者都不许引 I18n（依赖方向），
    /// 而"哪些名字被根占用"又必须在那两层成立。
    /// </remarks>
    public static void RegisterRootAliases()
    {
        foreach (var locale in Enum.GetValues<AppLocale>())
        {
            var table = StringTables.For(locale);
            foreach (var (token, key) in Roots)
                if (table.TryGetValue(key, out var name)) BookmarkPath.ReserveRootAlias(token, name);
        }
    }
}
