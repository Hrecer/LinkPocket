using System;
using System.Collections.Generic;
using System.Linq;
using LinkPocket.Contracts;

namespace LinkPocket.Views;

/// <summary>路径层级的一个节点（Id + 显示名）：地址栏解析/候选的数据源单元。</summary>
public readonly record struct PathNode(string Id, string Name);

/// <summary>
/// 地址栏路径解析器（逐级按名解析 + 候选补全）——**唯一实现**，浏览页与回收站共用：
/// 层级数据源以委托注入（浏览页 = 文件夹层级；回收站 = 被删单元层级），算法只有这一份。
///
/// <para>口径（与既有地址栏行为逐字一致）：同级**重名取排序第一**（按 ID 序）、名字比较**大小写不敏感**、
/// 根段名（「全部书签」/「回收站」）在解析时跳过（根不是实体、无 ID）；候选 = 当前层里以输入前缀开头、
/// 按名称排序取前 8。</para>
/// </summary>
public sealed class PathResolver
{
    /// <summary>候选上限（与既有口径一致）。</summary>
    public const int MaxCandidates = 8;

    private readonly string _rootToken;
    private readonly Func<string?, IReadOnlyList<PathNode>> _childrenOf;

    /// <param name="rootToken">本路径属于哪个根（<see cref="BookmarkPath.RootToken"/> / <see cref="BookmarkPath.TrashToken"/>）。</param>
    /// <param name="childrenOf">取某层（null = 根层）的直接子项；名字用显示名。</param>
    public PathResolver(string rootToken, Func<string?, IReadOnlyList<PathNode>> childrenOf)
    {
        _rootToken = rootToken;
        _childrenOf = childrenOf;
    }

    /// <summary>
    /// 位置链 → <b>canonical</b> 路径（<c>@root/A/B</c>，段内 / 转义）。
    /// 这是复制/粘贴/AI/日志用的形态；地址栏里给用户看的是它的投影（<c>I18n.BookmarkDisplay.Path</c>）。
    /// </summary>
    public string BuildCanonical(IEnumerable<(string Id, string Name)> chain)
        => BookmarkPath.Build(_rootToken, chain.Select(seg => seg.Name));

    /// <summary>
    /// 逐级按名解析路径；成功输出目标 ID（根 = null）与失败段名（失败时）。
    /// </summary>
    public bool TryResolve(string text, out string? id, out string? invalidSegment)
    {
        id = null;
        invalidSegment = null;
        foreach (var seg in PathText.Split(text))
        {
            // 根段：token 或**任一语言的根显示名**都认（中文下复制的路径，切英文照样粘得回来）；它不产生 ID
            if (id == null && BookmarkPath.MatchesRoot(_rootToken, seg)) continue;
            var current = id;
            var match = _childrenOf(current)
                .Where(n => string.Equals(n.Name, seg, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Id, StringComparer.Ordinal)
                .ToList();
            if (match.Count == 0)
            {
                invalidSegment = seg;
                return false;
            }
            id = match[0].Id;
        }
        return true;
    }

    /// <summary>
    /// 当前输入的候选补全清单（取最后一段的层级；前缀匹配；按名称排序取前 <see cref="MaxCandidates"/>）。
    /// head（最后一段之前的部分）解析失败时返回空清单（不猜测意图）。
    /// </summary>
    public IReadOnlyList<string> Candidates(string text)
    {
        var idx = PathText.LastSeparatorIndex(text);
        var headText = idx >= 0 ? text.Substring(0, idx + 1) : string.Empty;
        var typed = idx >= 0 ? text.Substring(idx + 1) : text;

        if (!TryResolve(headText, out var head, out _)) return Array.Empty<string>();

        var typedPlain = PathText.Unescape(typed.Trim());   // 用户输入的可能是转义名（如 "A\/B" 查找 A/B）
        return _childrenOf(head)
            .Where(n => typedPlain.Length == 0 || n.Name.StartsWith(typedPlain, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Name, NameOrder.Comparer)
            .Select(n => n.Name)
            .Take(MaxCandidates)
            .ToList();
    }

    /// <summary>选择候选（点击或 Tab）：把最后一段替换为候选名 + 分隔符（继续输入下一级）。</summary>
    public static string ApplyCandidate(string text, string name)
    {
        var idx = PathText.LastSeparatorIndex(text);
        var prefix = idx >= 0 ? text.Substring(0, idx + 1) : string.Empty;
        return prefix + PathText.Escape(name) + "/";
    }
}
