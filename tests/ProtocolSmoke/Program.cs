using LinkPocket.Api;
using System.Text.Json;

// LinkPocket 协议冒烟测试（对应 HANDOFF.md 12.1）
// 运行：dotnet run --project tests/ProtocolSmoke
// 覆盖：folders.contents 分页、total_link_count 直接子链接语义、
//       事件推送（links.changed / folders.changed / trash.changed）、
//       回收站闭环（v2：层级化 trash_folders + trashed_links）、错误通道。
// 注意：测试库是本测试 exe 目录下的独立 linkpocket.db，不会污染正式数据。

static void Assert(bool cond, string msg)
{
    if (!cond) throw new Exception("断言失败: " + msg);
}

var events = new List<string>();
var backend = new LinkPocketApi();
var transport = new InProcessTransport(new LinkPocketApiDispatcher(backend));
transport.EventReceived += (_, payload) =>
{
    using var doc = JsonDocument.Parse(payload);
    events.Add(doc.RootElement.GetProperty("event").GetString() ?? "");
};
ILinkPocketApi api = new TransportedLinkPocketApi(transport);

await api.ReinitializeDatabaseAsync(resetData: true);
events.Clear(); // 重置数据库本身也会推送事件，这里只测业务操作

// —— 1. 分页 + 计数语义 ——
var folder = await api.CreateFolderAsync("测试目录", null);
await api.CreateLinkAsync("https://example.com/1", "链接1", null, folder.FolderId);
await api.CreateLinkAsync("https://example.com/2", "链接2", null, folder.FolderId);
await api.CreateLinkAsync("https://example.com/3", "链接3", null, folder.FolderId);

var sub = await api.CreateFolderAsync("子目录", folder.FolderId);
await api.CreateLinkAsync("https://example.com/sub", "子链接", null, sub.FolderId);

var p1 = await api.GetFolderContentsAsync(folder.FolderId, perPage: 2);
Assert(p1.Links.Count == 2, $"page1 链接数应为 2，实际 {p1.Links.Count}");
Assert(p1.CurrentPage == 1, $"current_page 应为 1，实际 {p1.CurrentPage}");
Assert(p1.LastPage == 2, $"last_page 应为 2，实际 {p1.LastPage}");
Assert(p1.PerPage == 2, $"per_page 应为 2，实际 {p1.PerPage}");
Assert(p1.TotalLinkCount == 3, $"直接子链接计数应为 3（不含子目录），实际 {p1.TotalLinkCount}");
Assert(p1.SubFolders.Single().LinkCount == 1, "子目录 link_count 应为 1");

var p2 = await api.GetFolderContentsAsync(folder.FolderId, page: 2, perPage: 2);
Assert(p2.Links.Count == 1, $"page2 链接数应为 1，实际 {p2.Links.Count}");
Assert(p2.CurrentPage == 2, $"page2 current_page 应为 2，实际 {p2.CurrentPage}");

// 旧调用（不传分页参数）：行为与旧版一致，一次取回全部
var legacy = await api.GetFolderContentsAsync(folder.FolderId);
Assert(legacy.Links.Count == 3, $"不分页应返回全部 3 条，实际 {legacy.Links.Count}");
Assert(legacy.LastPage == 1, "不分页 last_page 应为 1");

// —— 2. 事件推送 ——
events.Clear();
await api.CreateFolderAsync("事件目录", null);
Assert(events.Contains("folders.changed"), "新建文件夹应推 folders.changed");

events.Clear();
var le = await api.CreateLinkAsync("https://example.com/e", "事件链接", null, folder.FolderId);
Assert(events.Contains("links.changed"), "新建链接应推 links.changed");

events.Clear();
await api.TrashLinkAsync(le.LinkId);
Assert(events.Contains("trash.changed"), "移入回收站应推 trash.changed");

// 单独删除的书签：平铺条目（根级）+ 树里没有单元
var trashEntries = await api.GetTrashAsync();
Assert(trashEntries.Any(e => e.Id == le.LinkId && e.EntryType == "link"),
    "单独删除的书签应出现在回收站平铺条目中且类型为 link");
var trashLinkEntry = trashEntries.First(e => e.Id == le.LinkId);
Assert(trashLinkEntry.OriginPath == "全部书签 / 测试目录",
    $"单独删除书签的 origin_path 应为「全部书签 / 测试目录」，实际「{trashLinkEntry.OriginPath}」");
Assert(!(await api.GetTrashTreeAsync()).Any(f => f.TrashFolderId == le.LinkId),
    "单独删除的书签不应产生回收站树节点");

await api.RestoreLinkAsync(le.LinkId);
Assert(events.Contains("trash.changed"), "恢复应推 trash.changed");
Assert(!(await api.GetTrashAsync()).Any(e => e.Id == le.LinkId), "恢复后平铺条目应消失");

events.Clear();
await api.TrashLinkAsync(le.LinkId); // 恢复后再次移入回收站，才能彻底删除
await api.PurgeTrashAsync(le.LinkId, isFolder: false);
Assert(events.Contains("trash.changed"), "彻底删除应推 trash.changed");
Assert(!(await api.GetTrashAsync()).Any(e => e.Id == le.LinkId), "永久删除后条目应消失");

// —— 3. 文件夹整树进回收站（v2 层级化） ——
events.Clear();
await api.DeleteFolderAsync(sub.FolderId, "trash_links");
Assert(events.Contains("trash.changed"), "文件夹进回收站应推 trash.changed");

// 子目录原内容：子链接行与子目录行从主表消失
var mainLinks = await api.GetAllLinksAsync();
Assert(!mainLinks.Any(l => l.Title == "子链接"), "被删文件夹内的链接应从主表消失");
Assert(!(await api.GetFolderTreeAsync()).Any(f => f.FolderId == sub.FolderId), "被删文件夹应从主树消失");

// 树：一个删除根单元（子目录），LinkCount=1
var tree = await api.GetTrashTreeAsync();
var rootUnit = tree.FirstOrDefault(f => f.Name == "子目录" && f.ParentTrashFolderId == null);
Assert(rootUnit != null, "删除根单元应挂在回收站根");
Assert(rootUnit!.LinkCount == 1, $"删除根单元 LinkCount 应为 1，实际 {rootUnit.LinkCount}");

// 平铺：folder 条目（origin_path 含完整路径）
var flat = await api.GetTrashAsync();
var folderEntry = flat.FirstOrDefault(e => e.EntryType == "folder" && e.Name == "子目录");
Assert(folderEntry != null, "删除根单元应以 folder 条目出现在平铺列表");
Assert(folderEntry!.OriginPath == "全部书签 / 测试目录 / 子目录",
    $"folder 条目 origin_path 应为「全部书签 / 测试目录 / 子目录」，实际「{folderEntry.OriginPath}」");

// 永久删除整单元 → 树与平铺同时清空该单元
await api.PurgeTrashAsync(rootUnit.TrashFolderId, isFolder: true);
Assert(!(await api.GetTrashTreeAsync()).Any(f => f.Name == "子目录"), "永久删除后树单元应消失");
Assert(!(await api.GetTrashAsync()).Any(e => e.EntryType == "folder" && e.Name == "子目录"),
    "永久删除后平铺 folder 条目应消失");

// —— 4. 错误通道 ——
try
{
    await api.GetFolderContentsAsync("不存在");
    Assert(false, "访问不存在的文件夹应抛 LinkPocketApiException");
}
catch (LinkPocketApiException ex)
{
    Assert(ex.ErrorCode == -32000, $"错误码应为 -32000，实际 {ex.ErrorCode}");
}

// —— 5. 书签导入 / 导出：Netscape 书签文件格式往返校验 ——
// 覆盖：格式识别（DOCTYPE）、嵌套文件夹、空文件夹不抢兄弟 <DL>、HTML 实体解码、
//       <DD> 描述、ADD_DATE / LAST_MODIFIED / ICON 还原、about:blank 跳过、
//       导出产物自校验（UTF-8 无 BOM）、二次往返逐行一致。
await api.ReinitializeDatabaseAsync(resetData: true);
events.Clear();

// Chrome / Edge / Firefox 导出格式的等价样本（含空文件夹、实体转义、CJK、DD 描述、about:blank 占位）
const string SampleBookmarks =
"""
<!DOCTYPE NETSCAPE-Bookmark-file-1>
<!-- This is an automatically generated file.
     It will be read and overwritten.
     DO NOT EDIT! -->
<META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
<TITLE>Bookmarks</TITLE>
<H1>Bookmarks</H1>
<DL><p>
    <DT><A HREF="https://www.example.com/root" ADD_DATE="1700000000" ICON="https://www.example.com/favicon.ico">根级书签</A>
    <DT><H3 ADD_DATE="1600000000" LAST_MODIFIED="1700000100" PERSONAL_TOOLBAR_FOLDER="true">书签栏</H3>
    <DL><p>
        <DT><A HREF="https://www.google.com/search?a=1&amp;b=2" ADD_DATE="1600000050" ICON="https://www.google.com/favicon.ico">Google 搜索</A>
        <DT><H3 ADD_DATE="1600000100">开发 &amp; 工具</H3>
        <DL><p>
            <DT><A HREF="https://github.com/" ADD_DATE="1600000200" LAST_MODIFIED="1650000000">GitHub</A>
            <DD>代码托管平台 &lt;好用的&gt;
            <DT><A HREF="javascript:void(0)" ADD_DATE="1600000300">JS 小工具</A>
        </DL><p>
        <DT><H3 ADD_DATE="1600000400">空文件夹A</H3>
        <DT><H3 ADD_DATE="1600000410">空文件夹B（有子列表）</H3>
        <DL><p>
        </DL><p>
        <DT><H3 ADD_DATE="1600000420">中文目录 · 深度</H3>
        <DL><p>
            <DT><H3 ADD_DATE="1600000430">二级</H3>
            <DL><p>
                <DT><A HREF="https://cn.example.com/?q=%E4%B8%AD%E6%96%87" ADD_DATE="1600000440">中文链接</A>
            </DL><p>
        </DL><p>
    </DL><p>
    <DT><H3 ADD_DATE="1600000500">其他书签</H3>
    <DL><p>
        <DT><A HREF="about:blank" ADD_DATE="1600000600">占位</A>
        <DT><A HREF="https://edge.example.com/" ADD_DATE="1600000700">Edge 书签</A>
    </DL><p>
</DL><p>
""";

var workDir = AppContext.BaseDirectory;
var samplePath = Path.Combine(workDir, "sample_bookmarks.html");
await File.WriteAllTextAsync(samplePath, SampleBookmarks, new System.Text.UTF8Encoding(false));

var inspection = await api.InspectBookmarksHtmlAsync(samplePath);
Assert(inspection.IsValid, $"预检应判定为有效书签文件：{inspection.Error}");
Assert(inspection.Format.Contains("NETSCAPE-Bookmark-file-1"),
    $"预检应识别为 Netscape 书签文件，实际「{inspection.Format}」");
Assert(inspection.FolderCount == 7, $"预检文件夹数应为 7，实际 {inspection.FolderCount}");
Assert(inspection.LinkCount == 6, $"预检书签数应为 6，实际 {inspection.LinkCount}");
Assert(inspection.SkippedCount == 1, $"应跳过 1 条 about:blank，实际 {inspection.SkippedCount}");
Assert(inspection.MaxDepth == 3, $"最大嵌套应为 3 层，实际 {inspection.MaxDepth}");

var importedCount = await api.ImportBookmarksHtmlAsync(samplePath);
Assert(importedCount == 13, $"导入条目数应为 13（7 文件夹 + 6 书签），实际 {importedCount}");
Assert(events.Contains("links.changed") && events.Contains("folders.changed"),
    "导入应推送 links.changed / folders.changed");

var bookTree = await api.GetFolderTreeAsync();
Assert(bookTree.Count == 7, $"导出前库内文件夹应为 7，实际 {bookTree.Count}");
string FolderId(string name) => bookTree.First(f => f.Name == name).FolderId;

Assert(bookTree.First(f => f.Name == "书签栏").ParentId == null, "「书签栏」应落在根级");
Assert(bookTree.First(f => f.Name == "二级").ParentId == FolderId("中文目录 · 深度"),
    "「二级」的父文件夹应为「中文目录 · 深度」");

var bookLinks = await api.GetAllLinksAsync();
Assert(bookLinks.Count == 6, $"库内书签应为 6，实际 {bookLinks.Count}");
Assert(bookLinks.Count(l => l.ListId == FolderId("空文件夹A")) == 0,
    "空文件夹（无子 <DL>）不应把兄弟文件夹的子树认作自己的");
Assert(bookLinks.Count(l => l.ListId == FolderId("空文件夹B（有子列表）")) == 0, "空文件夹不应含有书签");
Assert(bookLinks.Count(l => l.ListId == FolderId("开发 & 工具")) == 2,
    $"「开发 & 工具」应含 2 条书签，实际 {bookLinks.Count(l => l.ListId == FolderId("开发 & 工具"))}");

var google = bookLinks.First(l => l.Title == "Google 搜索");
Assert(google.Url == "https://www.google.com/search?a=1&b=2",
    $"URL 中的 &amp; 应解码为 &，实际「{google.Url}」");
Assert(google.FaviconUrl == "https://www.google.com/favicon.ico",
    $"ICON 应还原为 favicon_url，实际「{google.FaviconUrl}」");

var github = bookLinks.First(l => l.Title == "GitHub");
Assert(github.Description == "代码托管平台 <好用的>",
    $"<DD> 描述应解码导入，实际「{github.Description}」");
Assert(github.CreatedAt == DateTimeOffset.FromUnixTimeSeconds(1600000200).UtcDateTime,
    $"ADD_DATE 应还原为创建时间，实际 {github.CreatedAt:O}");
Assert(github.UpdatedAt == DateTimeOffset.FromUnixTimeSeconds(1650000000).UtcDateTime,
    $"LAST_MODIFIED 应还原为更新时间，实际 {github.UpdatedAt:O}");

Assert(bookTree.First(f => f.Name == "书签栏").CreatedAt == DateTimeOffset.FromUnixTimeSeconds(1600000000).UtcDateTime,
    "文件夹 ADD_DATE 应还原为创建时间");
Assert(bookLinks.Any(l => l.Title == "根级书签" && l.ListId == null), "根级书签应落在根级（list_id = null）");
Assert(!bookLinks.Any(l => l.Title == "占位"), "about:blank 条目应被跳过");

// —— 导出：产物格式自校验 ——
var exportA = Path.Combine(workDir, "export_a.html");
var exportedPath = await api.ExportBookmarksHtmlAsync(exportA);
Assert(File.Exists(exportA) && exportedPath == Path.GetFullPath(exportA), "导出应返回文件完整路径且文件已创建");

var exportBytes = await File.ReadAllBytesAsync(exportA);
Assert(!(exportBytes.Length >= 3 && exportBytes[0] == 0xEF && exportBytes[1] == 0xBB && exportBytes[2] == 0xBF),
    "导出文件应为 UTF-8 无 BOM（与 Chrome 一致）");
var exportText = await File.ReadAllTextAsync(exportA);
Assert(exportText.StartsWith("<!DOCTYPE NETSCAPE-Bookmark-file-1>"),
    "导出文件首行应为 NETSCAPE-Bookmark-file-1 DOCTYPE");
Assert(exportText.Contains("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">"),
    "导出文件应含 Netscape 标准 META 头");

var exportInspection = await api.InspectBookmarksHtmlAsync(exportA);
Assert(exportInspection.IsValid, $"导出产物应能被自身解析：{exportInspection.Error}");
Assert(exportInspection.FolderCount == 7 && exportInspection.LinkCount == 6,
    $"导出产物条目数应与库一致，实际 {exportInspection.FolderCount} 文件夹 / {exportInspection.LinkCount} 书签");
Assert(exportInspection.SkippedCount == 0, $"导出产物不应含 about:blank，实际跳过 {exportInspection.SkippedCount}");
Assert(exportInspection.MaxDepth == 3, $"导出产物最大嵌套应为 3 层，实际 {exportInspection.MaxDepth}");

// —— 二次往返：把导出产物重新导入全新库，再次导出并逐行比对 ——
await api.ReinitializeDatabaseAsync(resetData: true);
var reimported = await api.ImportBookmarksHtmlAsync(exportA);
Assert(reimported == 13, $"往返导入条目数应为 13，实际 {reimported}");

var exportB = Path.Combine(workDir, "export_b.html");
await api.ExportBookmarksHtmlAsync(exportB);

// 比较口径：文件夹的 LAST_MODIFIED 在导入时会被内核按「内容变动」语义刷新为当前时间
// （LinkPocketApi.ImportBookmarksHtmlAsync → FolderService.TouchAllModifiedAsync），
// 这是应用语义而非往返保真度的一部分 → 比对时归一掉文件夹行的 LAST_MODIFIED。
// 书签行（HREF / ADD_DATE / LAST_MODIFIED / ICON / 标题 / 层级缩进）保持严格逐字比对。
static string NormalizeExportLine(string line)
    => line.Contains("<H3")
        ? System.Text.RegularExpressions.Regex.Replace(line, "LAST_MODIFIED=\"\\d+\"", "LAST_MODIFIED=\"X\"")
        : line;

var linesA = (await File.ReadAllLinesAsync(exportA))
    .Where(l => l.Length > 0).Select(NormalizeExportLine).OrderBy(l => l, StringComparer.Ordinal).ToList();
var linesB = (await File.ReadAllLinesAsync(exportB))
    .Where(l => l.Length > 0).Select(NormalizeExportLine).OrderBy(l => l, StringComparer.Ordinal).ToList();
Assert(linesA.Count == linesB.Count,
    $"往返前后导出行数应一致（{linesA.Count} vs {linesB.Count}）");
var firstDiff = linesA.Zip(linesB).FirstOrDefault(pair => pair.First != pair.Second);
Assert(firstDiff == default,
    $"往返前后导出内容应逐行一致，差异：A「{firstDiff.First}」≠ B「{firstDiff.Second}」");

// 往返后结构不变（父链、空文件夹语义）
var treeAfter = await api.GetFolderTreeAsync();
Assert(treeAfter.Count == 7, $"往返后文件夹应为 7，实际 {treeAfter.Count}");
Assert(treeAfter.First(f => f.Name == "二级").ParentId == treeAfter.First(f => f.Name == "中文目录 · 深度").FolderId,
    "往返后嵌套父链应保持");
var linksAfter = await api.GetAllLinksAsync();
Assert(linksAfter.Count == 6, $"往返后书签应为 6，实际 {linksAfter.Count}");
Assert(linksAfter.First(l => l.Title == "Google 搜索").Url == "https://www.google.com/search?a=1&b=2",
    "往返后含 &amp; 的 URL 应保持解码一致");
Assert(linksAfter.First(l => l.Title == "GitHub").Description == "代码托管平台 <好用的>",
    "往返后 <DD> 描述应保持");

// —— 6. 数据闸：并发协议调用必须串行化、零丢失、零踩踏 ——
// 场景 = 用户在导入进行中又去别的页面操作（读写混合、真正并发在途）。
// 无闸时并发调用共享 DbContext 会抛"第二个操作已在此上下文上启动"；
// 有闸则全部串行执行，结果必须分毫不差。
events.Clear();
var stressFolder = await api.CreateFolderAsync("并发压测", null);
const int FanOut = 16;
var stressTasks = new List<Task>();
for (var i = 0; i < FanOut; i++)
{
    var idx = i;
    stressTasks.Add(api.CreateLinkAsync($"https://stress.example.com/{idx}", $"并发链接 {idx}", null, stressFolder.FolderId));
    stressTasks.Add(api.GetAllLinksAsync());                            // 读与写并发
    stressTasks.Add(api.GetFolderContentsAsync(stressFolder.FolderId)); // 同一文件夹读写并发
    stressTasks.Add(api.GetCountsAsync());
}
await Task.WhenAll(stressTasks);   // 任一调用抛异常 → WhenAll 直接失败

var stressContents = await api.GetFolderContentsAsync(stressFolder.FolderId);
Assert(stressContents.Links.Count == FanOut,
    $"并发写后文件夹应有 {FanOut} 条链接，实际 {stressContents.Links.Count}");
var stressTotal = await api.GetAllLinksAsync();
Assert(stressTotal.Count(l => l.ListId == stressFolder.FolderId) == FanOut,
    "并发写不应丢失或重复任何一条链接");
Assert(stressContents.TotalLinkCount == FanOut, "并发写后直接子链接计数应精确");
Assert(events.Count(e => e == "links.changed") == FanOut,
    $"每条并发写入都应推送一次 links.changed（实际 {events.Count(e => e == "links.changed")}）");

// —— 7. 备份 .lpbackup：往返 + 完整性校验 + 抗路径碰撞 + 回收站不备份 ——
// 覆盖：临时 key 身份（同父重名文件夹、名字含 " > " 均不串位）、SHA-256 完整性（篡改必拒）、
//       格式不含任何数据库 ID、回收站内容不进备份（还原后回收站为空）。
await api.ReinitializeDatabaseAsync(resetData: true);
events.Clear();

var bWork1 = await api.CreateFolderAsync("工作", null);
var bWork2 = await api.CreateFolderAsync("工作", null);                    // 同父重名（路径身份会碰撞）
var bOdd = await api.CreateFolderAsync("A > B", bWork1.FolderId);          // 名字含路径分隔符
await api.CreateLinkAsync("https://b.example.com/1", "链接一", null, bWork1.FolderId);
await api.CreateLinkAsync("https://b.example.com/2", "链接二", null, bWork2.FolderId);
await api.CreateLinkAsync("https://b.example.com/3", "链接三", null, bOdd.FolderId);
await api.CreateLinkAsync("https://b.example.com/root", "根级", null, null);
var bTrashLink = await api.CreateLinkAsync("https://b.example.com/trashme", "回收我", null, bWork1.FolderId);
await api.TrashLinkAsync(bTrashLink.LinkId);                               // 单独删除
var bDelFolder = await api.CreateFolderAsync("删我", bWork2.FolderId);
await api.CreateLinkAsync("https://b.example.com/sub", "子链接", null, bDelFolder.FolderId);
await api.DeleteFolderAsync(bDelFolder.FolderId, "trash");                 // 整树进回收站

var backupPath = Path.Combine(workDir, "test_backup.lpbackup");
var tamperedPath = Path.Combine(workDir, "tampered_backup.lpbackup");
foreach (var stale in new[] { backupPath, tamperedPath })
    if (File.Exists(stale)) File.Delete(stale);

await api.ExportBackupAsync(backupPath);
Assert(File.Exists(backupPath), "备份导出后文件应存在");

string ReadZipEntry(string zipPath, string entryName)
{
    using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
    var entry = zip.GetEntry(entryName);
    Assert(entry != null, $"备份包应含 {entryName}");
    using var reader = new StreamReader(entry!.Open());
    return reader.ReadToEnd();
}

var manifestText = ReadZipEntry(backupPath, "manifest.json");
var backupDataText = ReadZipEntry(backupPath, "data.json");
Assert(manifestText.Contains("data_sha256"), "manifest 应含 data.json 的 SHA-256");
Assert(backupDataText.Contains("\"key\""), "data.json 应使用临时 key 表达层级");
Assert(!backupDataText.Contains("folder_id") && !backupDataText.Contains("link_id"),
    "备份格式不应携带任何数据库 ID（防撞）");
Assert(!backupDataText.Contains("\"trash\""), "回收站不备份：data.json 不应含 trash 段");
Assert(!backupDataText.Contains("回收我") && !backupDataText.Contains("删我"),
    "回收站内容不应出现在备份里");

// 完全重置后恢复（回收站本来就随重置清空，导入不含回收站数据）
await api.ReinitializeDatabaseAsync(resetData: true);
var importResult = await api.ImportBackupAsync(backupPath);
Assert(importResult.Success, $"备份导入应成功：{string.Join("; ", importResult.Errors)}");
Assert(importResult.FoldersCreated == 3, $"活数据文件夹应恢复 3 个（工作×2 + A > B），实际 {importResult.FoldersCreated}");
Assert(importResult.LinksCreated == 4, $"活数据书签应恢复 4 条，实际 {importResult.LinksCreated}");

// 结构比对：重名文件夹保持两个且各自的书签不串位
var afterTree = await api.GetFolderTreeAsync();
Assert(afterTree.Count(f => f.Name == "工作") == 2, "同父重名文件夹应原样恢复两个");
var afterLinks = await api.GetAllLinksAsync();
Assert(afterLinks.Count == 4, $"恢复后活数据书签应为 4 条，实际 {afterLinks.Count}");
Assert(afterLinks.First(l => l.Title == "链接一").ListId !=
       afterLinks.First(l => l.Title == "链接二").ListId,
    "两个重名文件夹下的书签不应被合并到同一文件夹");
Assert(afterLinks.First(l => l.Title == "链接三").ListId ==
       afterTree.First(f => f.Name == "A > B").FolderId,
    "名字含「>」的文件夹应正常恢复且书签挂对");
Assert(afterLinks.Any(l => l.Title == "根级" && l.ListId == null), "根级书签应恢复到根级");

// 回收站不备份：导入后回收站必须为空
Assert(!(await api.GetTrashAsync()).Any(), "回收站不备份：导入后回收站平铺列表应为空");
Assert(!(await api.GetTrashTreeAsync()).Any(), "回收站不备份：导入后回收站树应为空");

// —— 完整性校验：篡改 data.json 必须被 SHA-256 拒绝 ——
using (var src = System.IO.Compression.ZipFile.OpenRead(backupPath))
using (var dst = System.IO.Compression.ZipFile.Open(tamperedPath, System.IO.Compression.ZipArchiveMode.Create))
{
    foreach (var entry in src.Entries)
    {
        var newEntry = dst.CreateEntry(entry.FullName);
        using var srcStream = entry.Open();
        using var dstStream = newEntry.Open();
        if (entry.FullName == "data.json")
        {
            var text = new StreamReader(srcStream).ReadToEnd() + " ";   // 篡改一个字符
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            dstStream.Write(bytes, 0, bytes.Length);
        }
        else
        {
            srcStream.CopyTo(dstStream);
        }
    }
}

var tamperedResult = await api.ImportBackupAsync(tamperedPath);
Assert(!tamperedResult.Success, "被篡改的备份必须被完整性校验拒绝");
Assert(tamperedResult.Errors.Any(e => e.Contains("完整性")), $"错误信息应说明完整性校验失败，实际：{string.Join("; ", tamperedResult.Errors)}");

Console.WriteLine("全部通过");
