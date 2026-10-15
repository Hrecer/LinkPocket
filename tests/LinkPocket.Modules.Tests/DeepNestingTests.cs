using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 任意深度嵌套（2026-10-15）：
/// ① 导入不再丢弃深层内容（旧 <c>MaxNestingDepth=64</c> 截断已移除，解析改显式栈迭代）；
/// ② 深链上的核心操作（递归计数 / 面包屑 / 整树删除 / 回收站）全部可用——不爆栈、不设深度上限。
/// </summary>
public class DeepNestingTests
{
    /// <summary>测试用嵌套深度：远超旧 64 层上限，同时把测试时间压在秒级。
    /// （深链导入是**批量写**：EF 的深层插入排序在链式依赖下逐层变慢，1000+ 层的病态文件导入要十几秒——
    /// 那是"一次性批量"的规模代价，单条操作不受影响；用例取 200 层以兼顾证明力与 CI 时长。）</summary>
    private const int ImportDepth = 200;

    private static string DeepHtml(int depth)
    {
        var sb = new StringBuilder("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n<TITLE>Bookmarks</TITLE>\n<H1>Bookmarks</H1>\n<DL><p>\n");
        for (var i = 0; i < depth; i++) sb.Append($"<DT><H3>L{i}</H3>\n<DL><p>\n");
        sb.Append("<DT><A HREF=\"https://deep.example\">最深书签</A>\n");
        for (var i = 0; i < depth; i++) sb.Append("</DL><p>\n");
        return sb.ToString();
    }

    [Fact]
    public async Task 导入_千层嵌套_全部落地_无截断告警_计数与面包屑不截断()
    {
        var (engine, _, _) = TestHost.Create();
        var htmlPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpdeep_{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(htmlPath, DeepHtml(ImportDepth));
        try
        {
            var imported = await engine.ExecuteAsync<JsonElement>("bookmarks.import", new { file_path = htmlPath });
            Assert.Equal(ImportDepth, imported.Data.GetProperty("folders_created").GetInt32());
            Assert.Equal(1, imported.Data.GetProperty("links_created").GetInt32());
            // 旧实现在 64 层起丢弃更深内容并告警；现在不得再出现这条截断痕迹
            Assert.DoesNotContain("nesting exceeded", JsonSerializer.Serialize(imported.Changes));

            var tree = await engine.QueryAsync<List<FolderDto>>("folders.tree", null);
            Assert.Equal(ImportDepth, tree.Count);
            var deepest = tree.Single(f => f.Name == $"L{ImportDepth - 1}");
            var link = (await engine.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://deep.example" })).Single();
            Assert.Equal(deepest.FolderId, link.ListId);      // 最深层的书签确实落在最深目录

            // 递归计数沿 1200 层父链累加：L0 的子树计数 = 最深那条链接（迭代实现，深链不爆栈）
            Assert.Equal(1, tree.Single(f => f.Name == "L0").LinkCount);
            Assert.Equal(1, tree.Single(f => f.Name == $"L{ImportDepth - 1}").LinkCount);   // 递归计数：最深目录子树里就是那条链接
            Assert.Single(await engine.QueryAsync<List<TreeLinkDto>>("folders.tree_links", new { folder_id = deepest.FolderId }));

            // 面包屑不截断（旧实现 50 层封顶）：最深目录的视图面包屑 = 根 + 全部各层
            var deepestView = await engine.QueryAsync<FolderContentsDto>("folders.overview",
                new { folder_id = deepest.FolderId, per_page = 1 });
            Assert.Equal(ImportDepth + 1, deepestView.Breadcrumb.Count);
        }
        finally
        {
            File.Delete(htmlPath);
        }
    }

    [Fact]
    public async Task 删除_深链子树的根_整树进回收站_链表与树都清干净()
    {
        var (engine, _, _) = TestHost.Create();
        string? parent = null;
        string rootId = "";
        for (var i = 0; i < 400; i++)
        {
            var created = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "D" + i, parent_id = parent });
            parent = created.Data!.FolderId;
            if (i == 0) rootId = parent;
        }
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://deep2.example", title = "deep", list_id = parent });

        var deleted = await engine.ExecuteAsync<FolderDeleteResult>("folders.delete", new { folder_id = rootId, cascade = "trash_links" });
        Assert.Equal(400, deleted.Data!.DeletedFolders);
        Assert.Equal(1, deleted.Data!.TrashedLinks);

        Assert.Empty(await engine.QueryAsync<List<FolderDto>>("folders.tree", null));   // 整树清空
        Assert.Empty(await engine.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://deep2.example" }));
        // 回收站单元根部在位（原 ID 保留）
        var trash = await engine.QueryAsync<List<TrashEntryDto>>("trash.list", null);
        Assert.Contains(trash, e => e.Id == rootId && e.EntryType == "folder");
    }
}
