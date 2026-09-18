using System.Globalization;
using System.Text;

namespace LinkPocket.Modules.Bookmarks;

/// <summary>
/// Netscape 书签文件解析器（internal，现状算法平移）。
///
/// <para><b>格式</b>：Chrome、Edge、Firefox、Safari 导出的书签文件是同一份格式，
/// 权威判别标记 = 首行 <c>&lt;!DOCTYPE NETSCAPE-Bookmark-file-1&gt;</c>；
/// 文件夹 = <c>&lt;DT&gt;&lt;H3&gt;名&lt;/H3&gt;</c> + 紧随的一对 <c>&lt;DL&gt;&lt;p&gt;…&lt;/DL&gt;&lt;p&gt;</c>，
/// 书签 = <c>&lt;DT&gt;&lt;A HREF="…" ADD_DATE="…"&gt;标题&lt;/A&gt;</c>，可选 <c>&lt;DD&gt;描述</c>；时间戳 Unix 秒。</para>
///
/// <para><b>健壮性要点</b>：单遍线性扫描（无正则回溯、无 O(n²) 子串）；HTML 实体解码（与导出转义严格互逆）；
/// 空文件夹不会"抢"兄弟文件夹的 &lt;DL&gt;（子 &lt;DL&gt; 必须紧跟 &lt;/H3&gt;）；
/// 标签不完整时按容错方式解析并把问题作为告警上报。</para>
/// </summary>
internal static class NetscapeReader
{
    private const int MaxFolderNameLength = 255;
    private const int MaxLinkTitleLength = 255;
    private const int MaxLinkUrlLength = 2048;
    private const int MaxLinkFaviconLength = 512;
    private const int MaxNestingDepth = 64;

    // ============================================================
    // —— 对外入口 ——
    // ============================================================

    /// <summary>读取文件并解析（只读，不碰数据库；可取消）。</summary>
    public static async Task<ParsedDocument> ParseFileAsync(string filePath, CancellationToken ct = default)
    {
        using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var html = await reader.ReadToEndAsync(ct);
        ct.ThrowIfCancellationRequested();
        return Parse(html);
    }

    // ============================================================
    // —— 解析：单遍线性扫描 ——
    // ============================================================

    internal static ParsedDocument Parse(string html)
    {
        var doc = new ParsedDocument();

        if (string.IsNullOrWhiteSpace(html))
        {
            doc.Error = "文件为空";
            return doc;
        }

        var hasDoctype = html.Contains("NETSCAPE-Bookmark-file", StringComparison.OrdinalIgnoreCase);
        doc.Format = hasDoctype
            ? "Netscape 书签文件（NETSCAPE-Bookmark-file-1）"
            : "Netscape 书签文件（无 DOCTYPE 声明）";

        var topOpen = IndexOfOpenTag(html, "DL", 0, html.Length);
        if (topOpen < 0)
        {
            doc.Error = "不是有效的书签文件：未找到 <DL> 列表容器";
            return doc;
        }

        var topOpenEnd = FindTagEnd(html, topOpen, html.Length);
        if (topOpenEnd < 0)
        {
            doc.Error = "文件解析失败：<DL> 标签未闭合";
            return doc;
        }

        var topClose = FindMatchingDlClose(html, topOpenEnd, html.Length);
        if (topClose < 0)
        {
            // 容错：标签不完整（截断/手工编辑过）时按剩余全文解析，并把问题作为告警上报
            doc.Warnings.Add("文件结构不完整（缺少匹配的 </DL>），已按容错方式解析");
            topClose = html.Length;
        }

        ParseEntries(html, topOpenEnd, topClose, parentIndex: -1, depth: 0, doc);

        if (doc.FolderCount == 0 && doc.LinkCount == 0)
        {
            doc.Error = "文件中未找到书签或文件夹（缺少 <A HREF=…> 或 <H3>）";
            return doc;
        }

        doc.IsValid = true;
        return doc;
    }

    /// <summary>在 [start,end) 区间内解析 &lt;DT&gt; 条目；文件夹递归进入其子 &lt;DL&gt;。整体复杂度 O(n)。</summary>
    private static void ParseEntries(string s, int start, int end, int parentIndex, int depth, ParsedDocument doc)
    {
        if (depth > MaxNestingDepth) return;

        var pos = start;
        while (pos < end)
        {
            var dt = IndexOfOpenTag(s, "DT", pos, end);
            if (dt < 0) break;

            var afterDt = FindTagEnd(s, dt, end);
            if (afterDt < 0) break;

            var p = SkipWhitespace(s, afterDt, end);
            if (p >= end) break;

            // —— 文件夹条目：<DT><H3>…</H3> [<DL>…</DL>] ——
            if (StartsWithTag(s, p, end, "H3"))
            {
                var openEnd = FindTagEnd(s, p, end);
                if (openEnd < 0) break;

                var close = IndexOfCloseTag(s, "H3", openEnd, end);
                if (close < 0)
                {
                    pos = openEnd;
                    continue;
                }
                var closeEnd = close + "</H3>".Length;

                var attrs = ParseAttributes(s, p, openEnd);
                var title = DecodeHtml(StripTags(Slice(s, openEnd, close)));
                var item = new ParsedItem
                {
                    IsFolder = true,
                    Title = string.IsNullOrWhiteSpace(title) ? "未命名文件夹" : title.Trim(),
                    ParentIndex = parentIndex,
                    Depth = depth + 1,
                    AddDate = ParseUnixSeconds(GetAttr(attrs, "ADD_DATE")),
                    LastModified = ParseUnixSeconds(GetAttr(attrs, "LAST_MODIFIED")),
                };

                var myIndex = doc.Items.Count;
                doc.Items.Add(item);
                doc.FolderCount++;
                if (item.Depth > doc.MaxDepth) doc.MaxDepth = item.Depth;   // 根级文件夹 = 1

                // 子 DL 必须紧跟 </H3>（只允许空白）：否则空文件夹会抢走兄弟的子树
                var q = SkipWhitespace(s, closeEnd, end);
                if (StartsWithTag(s, q, end, "DL"))
                {
                    var dlOpenEnd = FindTagEnd(s, q, end);
                    var dlClose = dlOpenEnd > 0 ? FindMatchingDlClose(s, dlOpenEnd, end) : -1;
                    if (dlClose > 0)
                    {
                        ParseEntries(s, dlOpenEnd, dlClose, myIndex, depth + 1, doc);
                        pos = SkipParagraphTags(s, dlClose + "</DL>".Length, end);
                        continue;
                    }
                    doc.Warnings.Add($"文件夹「{item.Title}」的子列表未闭合，其子树可能未被完整解析");
                }

                pos = closeEnd;
                continue;
            }

            // —— 书签条目：<DT><A HREF="…">标题</A> [<DD>描述] ——
            if (StartsWithTag(s, p, end, "A"))
            {
                var openEnd = FindTagEnd(s, p, end);
                if (openEnd < 0) break;

                var close = IndexOfCloseTag(s, "A", openEnd, end);
                var textEnd = close >= 0 ? close : openEnd;
                var closeEnd = close >= 0 ? close + "</A>".Length : openEnd;

                var attrs = ParseAttributes(s, p, openEnd);
                // HREF 在文件里是 HTML 转义形态（&amp; 等），必须解码才能与导出侧严格互逆
                var href = DecodeHtml(GetAttr(attrs, "HREF") ?? string.Empty);
                var title = DecodeHtml(StripTags(Slice(s, openEnd, textEnd))).Trim();

                pos = closeEnd;

                // <DD> 描述（出现在下一条 <DT> 或 </DL> 之前）
                string? description = null;
                var dd = SkipWhitespace(s, pos, end);
                if (StartsWithTag(s, dd, end, "DD"))
                {
                    var ddOpenEnd = FindTagEnd(s, dd, end);
                    if (ddOpenEnd > 0)
                    {
                        var ddEnd = FindNextEntryBoundary(s, ddOpenEnd, end);
                        description = DecodeHtml(StripTags(Slice(s, ddOpenEnd, ddEnd))).Trim();
                        if (description.Length == 0) description = null;
                        pos = ddEnd;
                    }
                }

                if (string.IsNullOrWhiteSpace(href) || href.Trim().Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                {
                    doc.SkippedCount++;   // 无地址 / 占位地址：跳过（与 Chrome 行为一致）
                    continue;
                }

                doc.Items.Add(new ParsedItem
                {
                    IsFolder = false,
                    Title = title.Length > 0 ? title : href.Trim(),
                    Url = href.Trim(),
                    Description = description,
                    IconUrl = DecodeOptionalAttr(attrs, "ICON") ?? DecodeOptionalAttr(attrs, "ICON_URI"),
                    ParentIndex = parentIndex,
                    Depth = depth + 1,
                    AddDate = ParseUnixSeconds(GetAttr(attrs, "ADD_DATE")),
                    LastModified = ParseUnixSeconds(GetAttr(attrs, "LAST_MODIFIED")),
                });
                doc.LinkCount++;
                continue;
            }

            // 其他 <DT> 内容（如 <DT><HR>）→ 跳过该条目继续
            pos = afterDt;
        }
    }

    // ============================================================
    // —— 文本扫描原语 ——
    // ============================================================

    private static string Slice(string s, int start, int end)
        => start >= end || start < 0 ? string.Empty : s.Substring(start, end - start);

    private static int SkipWhitespace(string s, int pos, int end)
    {
        while (pos < end && char.IsWhiteSpace(s[pos])) pos++;
        return pos;
    }

    private static int SkipParagraphTags(string s, int pos, int end)
    {
        while (true)
        {
            pos = SkipWhitespace(s, pos, end);
            if (!StartsWithTag(s, pos, end, "P")) return pos;
            var gt = FindTagEnd(s, pos, end);
            if (gt < 0) return pos;
            pos = gt;
        }
    }

    private static bool MatchesTagName(string s, int start, int end, string name)
    {
        if (start < 0 || start + name.Length >= end) return false;
        for (var k = 0; k < name.Length; k++)
        {
            if (char.ToUpperInvariant(s[start + k]) != name[k]) return false;
        }
        var next = s[start + name.Length];
        return next == '>' || next == '/' || char.IsWhiteSpace(next);
    }

    private static bool StartsWithTag(string s, int pos, int end, string name)
        => pos >= 0 && pos < end && s[pos] == '<' && MatchesTagName(s, pos + 1, end, name);

    private static int IndexOfOpenTag(string s, string name, int from, int end)
    {
        var i = from;
        while (i < end)
        {
            i = s.IndexOf('<', i);
            if (i < 0 || i >= end) return -1;
            if (TrySkipComment(s, ref i, end)) continue;   // HTML 注释（<!-- … -->）内的标签名一律不算
            if (MatchesTagName(s, i + 1, end, name)) return i;
            i++;
        }
        return -1;
    }

    private static int IndexOfCloseTag(string s, string name, int from, int end)
    {
        var i = from;
        while (i < end)
        {
            i = s.IndexOf('<', i);
            if (i < 0 || i + 1 >= end) return -1;
            if (TrySkipComment(s, ref i, end)) continue;   // HTML 注释内的标签名一律不算
            if (s[i + 1] == '/' && MatchesTagName(s, i + 2, end, name)) return i;
            i++;
        }
        return -1;
    }

    /// <summary>返回标签结束（'&gt;' 之后）的下标；引号内的 '&gt;' 不算（属性值可含 &gt;）。</summary>
    private static int FindTagEnd(string s, int from, int end)
    {
        var i = from;
        while (i < end)
        {
            var c = s[i];
            if (c == '"' || c == '\'')
            {
                var quote = s.IndexOf(c, i + 1);
                if (quote < 0 || quote >= end) return -1;
                i = quote + 1;
                continue;
            }
            if (c == '>') return i + 1;
            i++;
        }
        return -1;
    }

    private static int FindMatchingDlClose(string s, int from, int end)
    {
        var depth = 1;
        var pos = from;
        while (pos < end)
        {
            var open = IndexOfOpenTag(s, "DL", pos, end);
            var close = IndexOfCloseTag(s, "DL", pos, end);
            if (open < 0 && close < 0) return -1;

            if (close >= 0 && (open < 0 || close < open))
            {
                depth--;
                if (depth == 0) return close;
                pos = close + "</DL".Length;
            }
            else
            {
                depth++;
                pos = open + "<DL".Length;
            }
        }
        return -1;
    }

    private static int FindNextEntryBoundary(string s, int from, int end)
    {
        var dt = IndexOfOpenTag(s, "DT", from, end);
        var dl = IndexOfCloseTag(s, "DL", from, end);
        if (dt < 0 && dl < 0) return end;
        if (dt < 0) return dl;
        if (dl < 0) return dt;
        return Math.Min(dt, dl);
    }

    /// <summary>若 s[i] 起是 HTML 注释（&lt;!-- … --&gt;），跳过整个注释并把 i 置到注释之后；否则返回 false。</summary>
    private static bool TrySkipComment(string s, ref int i, int end)
    {
        if (i + 3 >= end || s[i + 1] != '!' || s[i + 2] != '-' || s[i + 3] != '-') return false;
        var close = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
        i = close < 0 ? end : close + 3;
        return true;
    }

    private static Dictionary<string, string> ParseAttributes(string s, int tagStart, int tagEnd)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = tagStart + 1;

        while (i < tagEnd && !char.IsWhiteSpace(s[i]) && s[i] != '>' && s[i] != '/') i++;   // 跳过标签名

        while (i < tagEnd)
        {
            while (i < tagEnd && (char.IsWhiteSpace(s[i]) || s[i] == '/')) i++;
            if (i >= tagEnd || s[i] == '>') break;

            var nameStart = i;
            while (i < tagEnd && s[i] != '=' && !char.IsWhiteSpace(s[i]) && s[i] != '>') i++;
            var name = Slice(s, nameStart, i);
            while (i < tagEnd && char.IsWhiteSpace(s[i])) i++;

            if (i >= tagEnd || s[i] != '=')
            {
                if (name.Length > 0) attrs[name] = string.Empty;
                continue;
            }

            i++;   // '='
            while (i < tagEnd && char.IsWhiteSpace(s[i])) i++;

            string value;
            if (i < tagEnd && (s[i] == '"' || s[i] == '\''))
            {
                var quote = s[i++];
                var valueStart = i;
                while (i < tagEnd && s[i] != quote) i++;
                value = Slice(s, valueStart, i);
                if (i < tagEnd) i++;
            }
            else
            {
                var valueStart = i;
                while (i < tagEnd && !char.IsWhiteSpace(s[i]) && s[i] != '>') i++;
                value = Slice(s, valueStart, i);
            }

            if (name.Length > 0) attrs[name] = value;
        }

        return attrs;
    }

    private static string? GetAttr(Dictionary<string, string> attrs, string name)
        => attrs.TryGetValue(name, out var v) && v.Length > 0 ? v : null;

    private static string? DecodeOptionalAttr(Dictionary<string, string> attrs, string name)
    {
        var raw = GetAttr(attrs, name);
        return raw == null ? null : DecodeHtml(raw).Trim();
    }

    private static string StripTags(string text)
    {
        if (text.IndexOf('<') < 0) return text;

        var sb = new StringBuilder(text.Length);
        var inTag = false;
        foreach (var c in text)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>HTML 实体解码（与导出侧的转义严格互逆）。</summary>
    private static string DecodeHtml(string text)
    {
        if (text.IndexOf('&') < 0) return text;

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '&')
            {
                sb.Append(text[i++]);
                continue;
            }

            var semicolon = text.IndexOf(';', i + 1);
            if (semicolon < 0 || semicolon - i > 12)
            {
                sb.Append(text[i++]);
                continue;
            }

            var decoded = DecodeEntity(Slice(text, i + 1, semicolon));
            if (decoded == null)
            {
                sb.Append(text[i++]);
                continue;
            }

            sb.Append(decoded);
            i = semicolon + 1;
        }
        return sb.ToString();
    }

    private static string? DecodeEntity(string entity)
    {
        if (entity.Length == 0) return null;

        if (entity[0] == '#')
        {
            var numeric = entity.Substring(1);
            int code;
            if (numeric.StartsWith("x", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(numeric.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                    return null;
            }
            else if (!int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
            {
                return null;
            }

            if (code <= 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) return null;   // 代理区非法标量：ConvertFromUtf32 会抛，外部输入必须容错
            return char.ConvertFromUtf32(code);
        }

        return entity.ToLowerInvariant() switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            "nbsp" => "\u00A0",
            "ndash" => "\u2013",
            "mdash" => "\u2014",
            "hellip" => "\u2026",
            "laquo" => "\u00AB",
            "raquo" => "\u00BB",
            "copy" => "\u00A9",
            "reg" => "\u00AE",
            "trade" => "\u2122",
            _ => null,
        };
    }

    private static DateTime? ParseUnixSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return null;
        if (seconds <= 0 || seconds > 253402300799L) return null;   // 9999-12-31 上限
        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }

    internal static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }

    /// <summary>解析出的单个条目。父子关系用 ParentIndex（items 下标）表达：父必先于子出现。</summary>
    internal sealed class ParsedItem
    {
        public bool IsFolder { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Url { get; set; }
        public string? Description { get; set; }
        public string? IconUrl { get; set; }
        public DateTime? AddDate { get; set; }
        public DateTime? LastModified { get; set; }
        public int ParentIndex { get; set; } = -1;
        public int Depth { get; set; }
    }

    /// <summary>解析结果（IsValid=false 时 Error 说明原因；Warnings 为可容忍问题）。</summary>
    internal sealed class ParsedDocument
    {
        public bool IsValid { get; set; }
        public string Error { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public List<string> Warnings { get; } = [];
        public List<ParsedItem> Items { get; } = [];
        public int FolderCount { get; set; }
        public int LinkCount { get; set; }
        public int SkippedCount { get; set; }
        public int MaxDepth { get; set; }
    }
}
