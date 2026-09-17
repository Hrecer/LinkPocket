using System.Globalization;
using System.IO;
using System.Text;
using LinkPocket.Data;

namespace LinkPocket.Services;

/// <summary>
/// Netscape 书签文件格式（Netscape Bookmark File Format）导入器 / 预检器。
///
/// <para><b>格式确认（2026-09-16 全面复核）</b>：Chrome、Edge、Firefox、Safari 导出的书签文件是同一份格式，
/// 权威判别标记 = 首行 <c>&lt;!DOCTYPE NETSCAPE-Bookmark-file-1&gt;</c>；正文为 UTF-8（无 BOM）、
/// 文件夹 = <c>&lt;DT&gt;&lt;H3&gt;名&lt;/H3&gt;</c> + 紧随的一对 <c>&lt;DL&gt;&lt;p&gt;…&lt;/DL&gt;&lt;p&gt;</c>，
/// 书签 = <c>&lt;DT&gt;&lt;A HREF="…" ADD_DATE="…" [ICON="…"]&gt;标题&lt;/A&gt;</c>，
/// 可选 <c>&lt;DD&gt;描述</c>；时间戳为 Unix 秒。</para>
///
/// <para><b>解耦约定</b>：只依赖 EF 数据上下文（<see cref="LinkPocketDbContext"/>），不含任何界面类型；
/// 对外可达路径见 <c>ILinkPocketApi.ImportBookmarksHtmlAsync</c> / <c>InspectBookmarksHtmlAsync</c>
/// 与协议方法 <c>import.bookmarks_html</c> / <c>bookmarks.inspect_html</c>。
/// <see cref="InspectAsync"/> 为纯只读预检（不写库）：界面在导入前用它做格式识别与条目统计，
/// 与 <see cref="ImportAsync"/> 共用同一套解析器，因此"看到的"与"导入的"必然一致。</para>
///
/// <para><b>健壮性要点</b>：单遍线性扫描（无正则回溯、无 O(n²) 子串）；HTML 实体解码（&amp;amp; 等，
/// 保证与导出的转义严格互逆）；空文件夹不会"抢"兄弟文件夹的 &lt;DL&gt;；
/// 文件夹 ID 在内存中生成，全部条目一次 SaveChanges 落库。</para>
/// </summary>
public class BookmarkImporter
{
    private readonly LinkPocketDbContext _db;

    public BookmarkImporter(LinkPocketDbContext db)
    {
        _db = db;
    }

    // —— 数据库列长上限（schema 约束，超出会截断）——
    private const int MaxFolderNameLength = 255;
    private const int MaxLinkTitleLength = 255;
    private const int MaxLinkUrlLength = 2048;
    private const int MaxLinkFaviconLength = 512;
    private const int MaxNestingDepth = 64;

    // ============================================================
    // —— 只读预检（不写库）——
    // ============================================================

    /// <summary>
    /// 解析文件并返回格式识别与条目统计，<b>不写数据库</b>。
    /// 用于导入前的预检展示（文件选择后立即调用），也可用于校验自己刚导出的文件。
    /// </summary>
    public static async Task<BookmarkInspection> InspectAsync(string filePath)
    {
        var inspection = new BookmarkInspection();

        if (string.IsNullOrWhiteSpace(filePath))
        {
            inspection.Error = "未指定文件";
            return inspection;
        }

        try
        {
            if (!File.Exists(filePath))
            {
                inspection.Error = "文件不存在";
                return inspection;
            }

            inspection.FileBytes = new FileInfo(filePath).Length;

            var doc = await ParseFileAsync(filePath);
            inspection.IsValid = doc.IsValid;
            inspection.Error = doc.Error;
            inspection.Warnings = doc.Warnings;
            inspection.Format = doc.Format;
            inspection.FolderCount = doc.FolderCount;
            inspection.LinkCount = doc.LinkCount;
            inspection.SkippedCount = doc.SkippedCount;
            inspection.MaxDepth = doc.MaxDepth;
        }
        catch (Exception ex)
        {
            inspection.IsValid = false;
            inspection.Error = "读取文件失败：" + ex.Message;
        }

        return inspection;
    }

    // ============================================================
    // —— 导入（写库）——
    // ============================================================

    /// <summary>
    /// 从 Netscape 书签文件导入全部书签与文件夹（追加到现有数据；顶层条目落在根级）。
    /// </summary>
    public async Task<ImportResult> ImportAsync(string filePath,
        IProgress<(string message, int current, int total)>? progress = null)
    {
        var result = new ImportResult();

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            result.Errors.Add("文件不存在");
            return result;
        }

        progress?.Report(("正在解析文件...", 0, 0));

        var doc = await ParseFileAsync(filePath);
        result.Warnings.AddRange(doc.Warnings);
        result.Skipped = doc.SkippedCount;

        if (!doc.IsValid)
        {
            result.Errors.Add(string.IsNullOrEmpty(doc.Error) ? "不是有效的书签文件" : doc.Error);
            return result;
        }

        var total = doc.Items.Count;
        var now = DateTime.UtcNow;

        // 文件夹 ID 由实体构造即生成 → 可在内存里直接建立父子关系，无需逐条 SaveChanges
        var folderIds = new string[doc.Items.Count];
        var foldersToAdd = new List<Folder>(doc.FolderCount);
        var linksToAdd = new List<Link>(doc.LinkCount);
        var directLinkCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < doc.Items.Count; i++)
        {
            var item = doc.Items[i];
            var parentFolderId = item.ParentIndex >= 0 ? folderIds[item.ParentIndex] : null;

            if (item.IsFolder)
            {
                var folder = new Folder
                {
                    Name = Truncate(item.Title, MaxFolderNameLength) ?? "未命名文件夹",
                    ParentId = parentFolderId,
                    LinkCount = 0,
                    CreatedAt = item.AddDate ?? now,
                    UpdatedAt = item.LastModified ?? item.AddDate ?? now
                };
                folderIds[i] = folder.FolderId;
                foldersToAdd.Add(folder);
                result.FoldersCreated++;
            }
            else
            {
                var link = new Link
                {
                    Url = Truncate(item.Url, MaxLinkUrlLength) ?? string.Empty,
                    Title = Truncate(item.Title, MaxLinkTitleLength),
                    Description = item.Description,
                    FaviconUrl = Truncate(item.IconUrl, MaxLinkFaviconLength),
                    ListId = parentFolderId,
                    VisitCount = 0,
                    IsImportant = false,
                    CreatedAt = item.AddDate ?? now,
                    UpdatedAt = item.LastModified ?? item.AddDate ?? now
                };
                linksToAdd.Add(link);
                result.LinksCreated++;

                if (parentFolderId != null)
                    directLinkCounts[parentFolderId] = directLinkCounts.TryGetValue(parentFolderId, out var c) ? c + 1 : 1;
            }

            if (progress != null && (i % 50 == 0 || i == doc.Items.Count - 1))
                progress.Report(($"正在导入: {item.Title}", i + 1, total));
        }

        // 文件夹「链接数」缓存字段按直接子链接数回填（与库内其它写入路径口径一致）
        foreach (var folder in foldersToAdd)
            folder.LinkCount = directLinkCounts.TryGetValue(folder.FolderId, out var n) ? n : 0;

        if (foldersToAdd.Count > 0) _db.Folders.AddRange(foldersToAdd);
        if (linksToAdd.Count > 0) _db.Links.AddRange(linksToAdd);
        await _db.SaveChangesAsync();

        progress?.Report(("导入完成", total, total));
        return result;
    }

    // ============================================================
    // —— 解析：单遍线性扫描 ——
    // ============================================================

    private static async Task<ParsedDocument> ParseFileAsync(string filePath)
    {
        string html;
        using (var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            html = await reader.ReadToEndAsync();
        return Parse(html);
    }

    private static ParsedDocument Parse(string html)
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

    /// <summary>
    /// 在 [start,end) 区间内解析 &lt;DT&gt; 条目；文件夹递归进入其子 &lt;DL&gt;。
    /// 全部坐标都在原字符串上推进（不做"剩余全文"切片），整体复杂度 O(n)。
    /// </summary>
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
                    LastModified = ParseUnixSeconds(GetAttr(attrs, "LAST_MODIFIED"))
                };

                var myIndex = doc.Items.Count;
                doc.Items.Add(item);
                doc.FolderCount++;
                if (item.Depth > doc.MaxDepth) doc.MaxDepth = item.Depth;   // 只统计文件夹嵌套（根级文件夹 = 1）

                // 子 DL 必须**紧跟**在 </H3> 之后（只允许空白）：
                // 若允许跨过兄弟 <DT> 去找 <DL>，空文件夹就会把兄弟文件夹的子树错认成自己的。
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

                // <DD> 描述（Netscape 可选字段；出现在下一条 <DT> 或 </DL> 之前）
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
                    doc.SkippedCount++;   // 无地址 / 占位地址：不产生有效书签，跳过（与 Chrome 行为一致）
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
                    LastModified = ParseUnixSeconds(GetAttr(attrs, "LAST_MODIFIED"))
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

    /// <summary>跳过紧跟的 &lt;p&gt; 段落标签（Netscape 格式在每个 &lt;DL&gt; 后都有 &lt;p&gt;）。</summary>
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

    /// <summary>标签名匹配（大小写无关）；name 必须为大写，且其后须是空白、'&gt;' 或 '/'。</summary>
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

    /// <summary>位置 pos 上是否是 &lt;name… 开标签。</summary>
    private static bool StartsWithTag(string s, int pos, int end, string name)
        => pos >= 0 && pos < end && s[pos] == '<' && MatchesTagName(s, pos + 1, end, name);

    /// <summary>在 [from,end) 内查找下一个 &lt;name 开标签（不会匹配 &lt;/name）。</summary>
    private static int IndexOfOpenTag(string s, string name, int from, int end)
    {
        var i = from;
        while (i < end)
        {
            i = s.IndexOf('<', i);
            if (i < 0 || i >= end) return -1;
            if (MatchesTagName(s, i + 1, end, name)) return i;
            i++;
        }
        return -1;
    }

    /// <summary>在 [from,end) 内查找下一个 &lt;/name 闭标签。</summary>
    private static int IndexOfCloseTag(string s, string name, int from, int end)
    {
        var i = from;
        while (i < end)
        {
            i = s.IndexOf('<', i);
            if (i < 0 || i + 1 >= end) return -1;
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

    /// <summary>从 &lt;DL…&gt; 之后找配对的 &lt;/DL&gt;（嵌套深度计数）；返回其起始下标。</summary>
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

    /// <summary>下一条目的边界：下一个 &lt;DT 或 &lt;/DL，取更早者。</summary>
    private static int FindNextEntryBoundary(string s, int from, int end)
    {
        var dt = IndexOfOpenTag(s, "DT", from, end);
        var dl = IndexOfCloseTag(s, "DL", from, end);
        if (dt < 0 && dl < 0) return end;
        if (dt < 0) return dl;
        if (dl < 0) return dt;
        return Math.Min(dt, dl);
    }

    /// <summary>解析标签内的属性（支持双引号 / 单引号 / 无引号值）。</summary>
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

    /// <summary>取属性并做 HTML 实体解码（ICON 等属性里的 &amp; 同样需要还原）。</summary>
    private static string? DecodeOptionalAttr(Dictionary<string, string> attrs, string name)
    {
        var raw = GetAttr(attrs, name);
        return raw == null ? null : DecodeHtml(raw).Trim();
    }

    /// <summary>去掉标签（如标题里残留的 &lt;b&gt;），只保留文本。</summary>
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

            if (code <= 0 || code > 0x10FFFF) return null;
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
            _ => null
        };
    }

    private static DateTime? ParseUnixSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return null;
        if (seconds <= 0 || seconds > 253402300799L) return null;   // 9999-12-31 上限，避开越界
        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }

    // ============================================================
    // —— 解析中间结构 ——
    // ============================================================

    private sealed class ParsedDocument
    {
        public bool IsValid { get; set; }
        public string Error { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public List<string> Warnings { get; } = new();
        public List<ParsedItem> Items { get; } = new();
        public int FolderCount { get; set; }
        public int LinkCount { get; set; }
        public int SkippedCount { get; set; }
        public int MaxDepth { get; set; }
    }

    /// <summary>解析出的单个条目。父子关系用 <see cref="ParentIndex"/>（items 下标）表达：父必先于子出现。</summary>
    private sealed class ParsedItem
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

    // ============================================================
    // —— 对外结果类型 ——
    // ============================================================

    /// <summary>导入结果（含告警与跳过统计）。</summary>
    public class ImportResult
    {
        public int FoldersCreated { get; set; }
        public int LinksCreated { get; set; }
        /// <summary>无地址 / about:blank 等被跳过的条目数。</summary>
        public int Skipped { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public int TotalItems => FoldersCreated + LinksCreated;
        public bool Success => Errors.Count == 0;
    }
}

/// <summary>只读预检结果（格式识别 + 条目统计，不含任何数据库副作用）。</summary>
public class BookmarkInspection
{
    public bool IsValid { get; set; }
    /// <summary>无效原因（有效时为空）。</summary>
    public string Error { get; set; } = string.Empty;
    /// <summary>格式说明（如「Netscape 书签文件（NETSCAPE-Bookmark-file-1）」）。</summary>
    public string Format { get; set; } = string.Empty;
    /// <summary>可容忍的问题（结构不完整等），导入仍会继续。</summary>
    public List<string> Warnings { get; set; } = new();
    public int FolderCount { get; set; }
    public int LinkCount { get; set; }
    /// <summary>被跳过的条目数（无地址 / about:blank）。</summary>
    public int SkippedCount { get; set; }
    /// <summary>最深文件夹嵌套层数（根级文件夹 = 1；无文件夹时为 0）。</summary>
    public int MaxDepth { get; set; }
    public long FileBytes { get; set; }
    public int TotalItems => FolderCount + LinkCount;
}
