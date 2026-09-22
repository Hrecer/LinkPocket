using System;
using System.Collections.Generic;

namespace LinkPocket.Contracts;

/// <summary>
/// <b>文本匹配的唯一口径</b>：搜索的"命中判定"（引擎侧与界面侧各算一遍）与结果<b>高亮</b>共用这一份实现。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须收成一处</b>：命中判定与高亮是同一件事的两个出口——一个决定"这条在不在结果里"、
/// 一个决定"这条里哪几段被染成强调色"。两边各写一遍匹配规则时，非 ASCII 文本上必然分叉：
/// 高亮那一侧原先走 <c>ToLowerInvariant()</c> + <c>Ordinal</c> 比较，<b>长度会变</b>
/// （<c>İ</c> / <c>K</c> 这类字符折叠后与原文不等长），于是从<b>折叠后的串</b>上算出的下标拿去切<b>原串</b>，
/// 轻则串位、重则抛。它和引擎侧的 <c>OrdinalIgnoreCase</c> 判定也就此变成两套规则。
/// </para>
/// <para>
/// <b>口径 = ASCII 大小写不敏感</b>（<see cref="StringComparison.OrdinalIgnoreCase"/>），
/// 与 SQL 侧 <c>LIKE</c>（<c>COLLATE NOCASE</c>，只折叠 ASCII A–Z）同义。
/// 选它而不是"完整 Unicode 折叠"的理由：SQL 是筛选的第一道门（LIKE 之下推到数据库），
/// 内存侧若比它<b>更宽</b>就会报出"命中字段为空"的条目；若比它<b>更窄</b>就会漏标高亮的段。
/// 要放宽到完整 Unicode 折叠，必须连 SQL 侧一起换（FTS5 / ICU），那是另一件事。
/// </para>
/// <para>
/// <b>放在契约层</b>的理由与 <c>LogRedactor</c> 同族：调用方一头在 <c>Modules.Search</c>（引擎侧），
/// 一头在 <c>UI.Search</c> / <c>UIKit</c>（界面侧），而界面层<b>禁引 Kernel</b>
/// （<c>DependencyRulesTests</c> 卡住）——只有契约层两侧都到得了。本类零依赖、纯函数。
/// </para>
/// </remarks>
public static class TextMatch
{
    /// <summary>
    /// 一个半开区间 <c>[Start, Start + Length)</c>：命中片段在**原串**里的位置。
    /// </summary>
    /// <remarks>
    /// 返回值刻意是"原串上的下标"而不是"折叠后的串"——调用方拿它直接 <c>Substring</c> 就切出高亮段，
    /// 不需要知道匹配是怎么算的（这是原来那套实现出错的地方：它把两种串的下标混用了）。
    /// </remarks>
    public readonly record struct Range(int Start, int Length)
    {
        /// <summary>区间结束（不含）。</summary>
        public int End => Start + Length;
    }

    /// <summary>包含判定（空查询恒假：空查询不是"匹配一切"，它由调用方按范围报错或走空态）。</summary>
    public static bool Contains(string? text, string? needle)
        => !string.IsNullOrEmpty(text)
           && !string.IsNullOrEmpty(needle)
           && text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 全部不重叠的命中区间，按出现顺序（空串任一侧 → 空清单）。
    /// </summary>
    /// <remarks>
    /// 步进 = <c>区间长度</c>，不是 <c>+1</c>：重叠命中（<c>aa</c> 在 <c>aaa</c> 里）按左到右取尽，
    /// 与高亮时的逐段拼接一致——若按 <c>+1</c> 步进，高亮会画出互相覆盖的段。
    /// </remarks>
    public static IReadOnlyList<Range> Ranges(string? text, string? needle)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return Array.Empty<Range>();

        var hits = new List<Range>();
        var pos = 0;
        while (pos <= text.Length - needle.Length)
        {
            var hit = text.IndexOf(needle, pos, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;
            hits.Add(new Range(hit, needle.Length));
            pos = hit + needle.Length;
        }
        return hits;
    }
}
