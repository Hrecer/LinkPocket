using System;
using System.Collections.Generic;
using LinkPocket.Models;

namespace LinkPocket.ViewModels;

/// <summary>
/// 活结果集的排序比较器：逐字段对齐引擎 <c>ORDER BY</c> 的口径。
/// <para>链接名称/地址 = SQLite <c>COLLATE NOCASE</c>（仅 ASCII 折叠，其余按 ordinal）；
/// 日期/次数/重要 = 值比较；<c>last_visited_at</c> 为空恒排最后（不随升降序翻转）；
/// ID 兜底方向随排序方向；文件夹块恒在链接块之前，文件夹名称走 OrdinalIgnoreCase
/// （与 SearchSupport.SortFolderHits 同一口径）。</para>
/// </summary>
internal static class SearchOrdering
{
    public static IComparer<LinkItem> Create(string sortField, bool ascending, Func<string?, int> folderOrder)
        => new Comparer(sortField, ascending, folderOrder);

    private sealed class Comparer(string sortField, bool ascending, Func<string?, int> folderOrder)
        : IComparer<LinkItem>
    {
        private readonly string _field = string.IsNullOrEmpty(sortField) ? "title" : sortField;

        public int Compare(LinkItem? x, LinkItem? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            // 文件夹块恒在链接块之前（与引擎/会话的拼接顺序一致）。
            if (x.IsFolder != y.IsFolder) return x.IsFolder ? -1 : 1;
            return x.IsFolder ? CompareFolders(x, y) : CompareLinks(x, y);
        }

        private int CompareLinks(LinkItem a, LinkItem b)
        {
            if (_field == "last_visited_at")
            {
                var byVisit = CompareLastVisited(a.LastVisitedAt, b.LastVisitedAt);
                return byVisit != 0 ? byVisit : ApplyIdDirection(a.LinkId, b.LinkId);
            }

            var result = _field switch
            {
                "title" => CompareAsciiNoCase(a.Title, b.Title),
                "url" => CompareAsciiNoCase(a.Url, b.Url),
                "created_at" => a.CreatedAt.CompareTo(b.CreatedAt),
                "updated_at" => a.UpdatedAt.CompareTo(b.UpdatedAt),
                "visit_count" => a.VisitCount.CompareTo(b.VisitCount),
                "is_important" => a.IsImportant.CompareTo(b.IsImportant),
                "path" => ComparePath(a, b),
                _ => CompareAsciiNoCase(a.Title, b.Title),
            };
            if (result != 0) return ApplyDirection(result);
            return ApplyIdDirection(a.LinkId, b.LinkId);
        }

        private int CompareFolders(LinkItem a, LinkItem b)
        {
            if (_field == "last_visited_at")
            {
                var byVisit = CompareLastVisited(a.LastVisitedAt, b.LastVisitedAt);
                return byVisit != 0 ? byVisit : ApplyIdDirection(a.LinkId, b.LinkId);
            }

            var result = _field switch
            {
                "created_at" => a.CreatedAt.CompareTo(b.CreatedAt),
                "updated_at" => a.UpdatedAt.CompareTo(b.UpdatedAt),
                "visit_count" => a.VisitCount.CompareTo(b.VisitCount),
                // url / is_important 对文件夹无意义 → 按名称（SearchSupport 同口径）；path 也走路径键
                "path" => ComparePath(a, b),
                _ => string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.OrdinalIgnoreCase),
            };
            if (result != 0) return ApplyDirection(result);
            return ApplyIdDirection(a.LinkId, b.LinkId);
        }

        private int ComparePath(LinkItem a, LinkItem b)
        {
            var byFolder = folderOrder(a.ListId).CompareTo(folderOrder(b.ListId));
            if (byFolder != 0) return byFolder;
            var byName = string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.CurrentCulture);
            return byName;
        }

        /// <summary>排序方向只作用于“值”比较；ID 兜底单独按方向处理。</summary>
        private int ApplyDirection(int value) => ascending ? value : -value;

        /// <summary>为空恒排最后（不随方向翻转）；两侧都有值时按方向比较。</summary>
        private int CompareLastVisited(DateTime? a, DateTime? b)
        {
            if (a is null || b is null) return CompareNullsLast(a, b);
            var result = a.Value.CompareTo(b.Value);
            return ApplyDirection(result);
        }

        private int ApplyIdDirection(string a, string b)
        {
            var result = string.CompareOrdinal(a, b);
            return ascending ? result : -result;
        }

        /// <summary>SQLite NOCASE：仅折叠 ASCII A-Z；其余字符按 ordinal 比较。</summary>
        private static int CompareAsciiNoCase(string? a, string? b)
        {
            a ??= string.Empty;
            b ??= string.Empty;
            var length = Math.Min(a.Length, b.Length);
            for (var i = 0; i < length; i++)
            {
                var ca = LowerAscii(a[i]);
                var cb = LowerAscii(b[i]);
                if (ca != cb) return ca < cb ? -1 : 1;
            }
            return a.Length.CompareTo(b.Length);
        }

        private static char LowerAscii(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

        private static int CompareNullsLast(DateTime? a, DateTime? b)
        {
            if (a is null) return b is null ? 0 : 1;
            if (b is null) return -1;
            return a.Value.CompareTo(b.Value);
        }
    }
}
