using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Modules.Links;
using LinkPocket.Modules.Maintenance;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 字段级 diff（E3）：逐命令断言「改了哪个实体的哪个字段、旧值新值各是什么」。
/// 口径见 ENGINE-API §1（Before/After 为 null = 该时刻字段不适用；只报真的变了的字段）。
/// </summary>
public class FieldDiffTests
{
    private static string? Str(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString(),
        JsonValueKind.Null => null,
        _ => value?.GetRawText(),
    };

    private static FieldChange Field(IReadOnlyList<FieldChange> diff, string id, string field)
        => diff.Single(f => f.Id == id && f.Field == field);

    [Fact]
    public async Task links_update_只报真的变了的字段_旧值新值成对()
    {
        var (engine, _, _) = TestHost.Create();
        var link = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://example.test/a", title = "标题", description = "旧描述" });

        var result = await engine.ExecuteAsync<LinkDto>("links.update",
            new { id = link.Data!.LinkId, description = "中文教程", title = "标题" });   // title 显式给同值 = 不变

        var diff = result.Changes!.Diff!;
        var entry = Assert.Single(diff);
        Assert.Equal("link", entry.Type);
        Assert.Equal(link.Data.LinkId, entry.Id);
        Assert.Equal("description", entry.Field);
        Assert.Equal("旧描述", Str(entry.Before));
        Assert.Equal("中文教程", Str(entry.After));
    }

    [Fact]
    public async Task links_create_全字段新值_Before不适用()
    {
        var (engine, _, _) = TestHost.Create();
        var result = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://example.test/b", title = "甲", is_important = true });

        var diff = result.Changes!.Diff!;
        Assert.Equal("https://example.test/b", Str(Field(diff, result.Data!.LinkId, "url").After));
        Assert.Null(Field(diff, result.Data.LinkId, "url").Before);                 // Before = 不适用
        Assert.True(Field(diff, result.Data.LinkId, "is_important").After!.Value.GetBoolean());
        Assert.Equal(JsonValueKind.Null, Field(diff, result.Data.LinkId, "folder_id").After!.Value.ValueKind);  // 根级 = 空值
    }

    [Fact]
    public async Task links_trash_删除快照_After不适用且保留原归属()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        var link = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://example.test/c", title = "Rust 圣经", list_id = folder.Data!.FolderId });

        var result = await engine.ExecuteAsync<object>("links.trash", new { id = link.Data!.LinkId });

        var diff = result.Changes!.Diff!;
        var location = Field(diff, link.Data.LinkId, "folder_id");
        Assert.Equal(folder.Data.FolderId, Str(location.Before));   // 原位置快照
        Assert.Null(location.After);                                // After = 不适用（已离开主表）
        Assert.Equal("Rust 圣经", Str(Field(diff, link.Data.LinkId, "title").Before));
    }

    [Fact]
    public async Task folders_create_与_update_的diff形态()
    {
        var (engine, _, _) = TestHost.Create();
        var parent = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        var created = await engine.ExecuteAsync<FolderDto>("folders.create",
            new { name = "临时", parent_id = parent.Data!.FolderId, description = "d" });

        var createDiff = created.Changes!.Diff!;
        Assert.Equal("临时", Str(Field(createDiff, created.Data!.FolderId, "name").After));
        Assert.Equal(parent.Data.FolderId, Str(Field(createDiff, created.Data.FolderId, "parent_id").After));
        Assert.Null(Field(createDiff, created.Data.FolderId, "parent_id").Before);

        var updated = await engine.ExecuteAsync<FolderDto>("folders.update",
            new { folder_id = created.Data.FolderId, name = "临时改" });
        var updateDiff = updated.Changes!.Diff!;
        var rename = Assert.Single(updateDiff);
        Assert.Equal("name", rename.Field);
        Assert.Equal("临时", Str(rename.Before));
        Assert.Equal("临时改", Str(rename.After));
    }

    [Fact]
    public async Task folders_move_报parent_id变更_撞名编号也入diff()
    {
        var (engine, _, _) = TestHost.Create();
        var a = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "A" });
        var b = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "B" });
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "同名", parent_id = b.Data!.FolderId });
        var moved = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "同名", parent_id = a.Data!.FolderId });

        var result = await engine.ExecuteAsync<FolderDto>("folders.move",
            new { folder_id = moved.Data!.FolderId, target_parent_id = b.Data.FolderId });

        var diff = result.Changes!.Diff!;
        Assert.Equal(b.Data.FolderId, Str(Field(diff, moved.Data.FolderId, "parent_id").After));
        Assert.Equal(a.Data.FolderId, Str(Field(diff, moved.Data.FolderId, "parent_id").Before));
        Assert.Equal("同名 (2)", Str(Field(diff, moved.Data.FolderId, "name").After));   // 目标层撞名自动编号 = 字段变更
    }

    [Fact]
    public async Task folders_delete_trash_links_整子树进快照()
    {
        var (engine, _, _) = TestHost.Create();
        var parent = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        var child = await engine.ExecuteAsync<FolderDto>("folders.create",
            new { name = "子", parent_id = parent.Data!.FolderId });
        var link = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://example.test/d", list_id = child.Data!.FolderId });

        var result = await engine.ExecuteAsync<object>("folders.delete",
            new { folder_id = parent.Data.FolderId });

        var diff = result.Changes!.Diff!;
        Assert.Equal(parent.Data.FolderId, Field(diff, parent.Data.FolderId, "name").Id);
        Assert.Null(Field(diff, parent.Data.FolderId, "name").After);
        Assert.Equal("子", Str(Field(diff, child.Data!.FolderId, "name").Before));
        Assert.Equal(child.Data.FolderId, Str(Field(diff, link.Data!.LinkId, "folder_id").Before));
    }

    [Fact]
    public async Task trash_restore_还原后给出落点字段()
    {
        var (engine, _, _) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        var link = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://example.test/e", title = "甲", list_id = folder.Data!.FolderId });
        await engine.ExecuteAsync<object>("links.trash", new { id = link.Data!.LinkId });

        var result = await engine.ExecuteAsync<object>("trash.restore", new { id = link.Data.LinkId });

        var diff = result.Changes!.Diff!;
        var location = Field(diff, link.Data.LinkId, "folder_id");
        Assert.Null(location.Before);                                  // 还原 = 重新回到主表（Before 不适用）
        Assert.Equal(folder.Data.FolderId, Str(location.After));       // 落点 = 删除前位置
    }

    [Fact]
    public async Task dedup_apply_嵌套步骤的diff并入父变更集()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://dup.test/x", title = "第一条" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://dup.test/x", title = "第二条" });

        var result = await engine.ExecuteAsync<object>("dedup.apply", new { });

        var diff = result.Changes!.Diff!;
        Assert.Contains(diff, f => f.Field == "url" && Str(f.Before) == "https://dup.test/x");   // 被移入回收站的那条带上原值
        Assert.Contains(diff, f => f.Field == "folder_id" && f.After is null);
    }

    [Fact]
    public async Task links_query_支持id过滤_eq与in()
    {
        var (engine, _, _) = TestHost.Create();
        var first = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://q.test/1" });
        var second = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://q.test/2" });
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://q.test/3" });

        var byEq = await engine.QueryAsync<PagedLinkResult>("links.query", new
        {
            filter = new[] { new { field = "id", op = "eq", value = first.Data!.LinkId } },
            fields = new[] { "id", "url" },
        });
        Assert.Equal(1, byEq.Total);
        Assert.Single(byEq.Items);

        var byIn = await engine.QueryAsync<PagedLinkResult>("links.query", new
        {
            filter = new[] { new { field = "id", op = "in", value = new[] { first.Data.LinkId, second.Data!.LinkId } } },
            fields = new[] { "id" },
        });
        Assert.Equal(2, byIn.Total);
    }

    [Fact]
    public async Task 审计落库_携带字段级diff()
    {
        var (engine, factory, _) = TestHost.CreateWithAudit();
        var link = await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://a.test/x", title = "甲" });
        await engine.ExecuteAsync<LinkDto>("links.update", new { id = link.Data!.LinkId, title = "乙" });

        var audit = await engine.QueryAsync<AuditPagedResult>("audit.query", new
        {
            command = "links.update",
            include_payloads = true,
        });

        var row = Assert.Single(audit.Items);
        var changes = JsonSerializer.Deserialize<JsonElement>(row.ChangesJson!);
        var entry = changes.GetProperty("diff").EnumerateArray().Single();
        Assert.Equal("title", entry.GetProperty("field").GetString());
        Assert.Equal("甲", entry.GetProperty("before").GetString());
        Assert.Equal("乙", entry.GetProperty("after").GetString());
        Assert.False(changes.TryGetProperty("diff_truncated", out _));   // 未截断 = 无标记
        _ = factory;
    }
}
