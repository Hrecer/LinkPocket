using LinkPocket.Api;
using System.Text.Json;

// LinkPocket 协议冒烟测试（对应 HANDOFF.md 12.1）
// 运行：dotnet run --project tests/ProtocolSmoke
// 覆盖：folders.contents 分页、total_link_count 直接子链接语义、
//       事件推送（links.changed / folders.changed / trash.changed）、
//       回收站闭环、错误通道。
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

await api.RestoreLinkAsync(le.LinkId);
Assert(events.Contains("trash.changed"), "恢复应推 trash.changed");

events.Clear();
await api.TrashLinkAsync(le.LinkId); // 恢复后再次移入回收站，才能彻底删除
await api.PurgeLinkAsync(le.LinkId);
Assert(events.Contains("trash.changed"), "彻底删除应推 trash.changed");

// —— 3. 错误通道 ——
try
{
    await api.GetFolderContentsAsync("不存在");
    Assert(false, "访问不存在的文件夹应抛 LinkPocketApiException");
}
catch (LinkPocketApiException ex)
{
    Assert(ex.ErrorCode == -32000, $"错误码应为 -32000，实际 {ex.ErrorCode}");
}

Console.WriteLine("全部通过");
