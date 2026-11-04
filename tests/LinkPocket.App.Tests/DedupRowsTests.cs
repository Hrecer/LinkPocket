using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 查重页的行模型与删除局部更新：
/// · <see cref="LocValue"/> 必须按**内容**比较（否则 SameSequence 恒 false、每次重扫都整表重建）；
/// · 删除只更新当前组（批量入站一次），不再全库重读、不把组内旧行留在主表；
/// · URL 分组与引擎同口径（完全相同的字符串，Ordinal）。
/// </summary>
public class DedupRowsTests
{
    [Fact]
    public void 文案值按内容相等_嵌套参数同样比较()
    {
        Assert.Equal(Loc.K("tools.checkedCount", 3), Loc.K("tools.checkedCount", 3));
        Assert.NotEqual(Loc.K("tools.checkedCount", 3), Loc.K("tools.checkedCount", 4));
        Assert.Equal(
            Loc.K("tools.pathsHint", Loc.K("nav.root.bookmarks"), 2),
            Loc.K("tools.pathsHint", Loc.K("nav.root.bookmarks"), 2));
        Assert.Equal(Loc.K("tools.checkedCount"), LocValue.Of("tools.checkedCount"));
        Assert.Equal(Loc.K("tools.checkedCount", 3).GetHashCode(), Loc.K("tools.checkedCount", 3).GetHashCode());
    }

    [Fact]
    public void 渲染等价_同内容不同实例为真_字段变化为假()
    {
        var a = Rows("https://dup.example/a", "L1", "L2");
        var b = Rows("https://dup.example/a", "L1", "L2");
        Assert.True(DedupGroupRow.SameSequence(a, b));

        var changedLink = Rows("https://dup.example/a", "L1", "L2");
        changedLink[0].Links[1].Title = "改过";
        Assert.False(DedupGroupRow.SameSequence(a, changedLink));

        var changedLocations = Rows("https://dup.example/a", "L1", "L2");
        changedLocations[0].LocationsSummary = Loc.K("path.unknown");
        Assert.False(DedupGroupRow.SameSequence(a, changedLocations));
    }

    [Fact]
    public async Task 扫描按目录解析路径_删除一条只更新当前组()
    {
        var (client, _, db) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("目标目录")).Data!;
            var created = new List<LinkDto>();
            for (var i = 0; i < 4; i++)
                created.Add((await client.LinkCreateAsync("https://dup.example/partial",
                    title: $"副本 {i}", listId: folder.FolderId)).Data!);

            var calls = 0;
            var vm = new ToolsViewModel(client, null,
                _ => { calls++; return Task.FromResult(Loc.K("nav.root.bookmarks")); });

            var groups = await vm.RunDedupAsync();
            Assert.Single(groups);
            Assert.Equal(4, groups[0].Count);
            Assert.Equal(1, calls);   // 同一目录只解析一次（旧写法逐 LinkId 是 4 次）

            vm.EnterGroup(groups[0]);
            await vm.LoadGroupLinksAsync(groups[0]);
            Assert.True(vm.ToggleChecked(created[0].LinkId));
            var rest = await vm.DeleteCheckedAsync();

            Assert.NotNull(rest);
            Assert.Equal(3, rest!.Count);
            Assert.Equal(3, vm.Groups.Single().Count);
            Assert.DoesNotContain(vm.CurrentGroupLinks!, l => l.LinkId == created[0].LinkId);
            Assert.DoesNotContain(vm.Groups.Single().Links, l => l.LinkId == created[0].LinkId);

            var trash = await client.TrashListAsync();
            Assert.Contains(trash, t => t.Id == created[0].LinkId);
        }
        finally { AppTestEnv.Delete(db); }
    }

    [Fact]
    public async Task 删除到只剩一条_组立即消失且不再重复()
    {
        var (client, _, db) = AppTestEnv.Create();
        try
        {
            var a = (await client.LinkCreateAsync("https://dup.example/pair", title: "A")).Data!;
            var b = (await client.LinkCreateAsync("https://dup.example/pair", title: "B")).Data!;

            var vm = new ToolsViewModel(client, null,
                _ => Task.FromResult(Loc.K("nav.root.bookmarks")));
            var groups = await vm.RunDedupAsync();
            Assert.Single(groups);

            vm.EnterGroup(groups[0]);
            await vm.LoadGroupLinksAsync(groups[0]);
            Assert.True(vm.ToggleChecked(a.LinkId));
            var rest = await vm.DeleteCheckedAsync();

            Assert.Null(rest);
            Assert.Empty(vm.Groups);
            Assert.Null(vm.CurrentGroupLinks);
            Assert.Equal(string.Empty, vm.CurrentGroupUrl);
            Assert.Contains(await client.TrashListAsync(), t => t.Id == a.LinkId);
            Assert.Equal(b.LinkId, (await client.LinkFindByUrlAsync("https://dup.example/pair")).Single().LinkId);
        }
        finally { AppTestEnv.Delete(db); }
    }

    [Fact]
    public async Task URL分组与引擎同口径_大小写不同不算重复()
    {
        var (client, _, db) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://Example.com/x", title: "大写");
            await client.LinkCreateAsync("https://example.com/x", title: "小写");

            var vm = new ToolsViewModel(client, null,
                _ => Task.FromResult(Loc.K("nav.root.bookmarks")));
            var groups = await vm.RunDedupAsync();

            // 引擎的 links.find_by_url / ListDuplicated 都是 Url == Url（Ordinal）——
            // UI 曾用 OrdinalIgnoreCase 分组，与引擎口径不一致；现在统一为完全相同才算重复。
            Assert.Empty(groups);
        }
        finally { AppTestEnv.Delete(db); }
    }

    [Fact]
    public async Task 外部宿主查重仍是全量且字段完整()
    {
        var (client, _, db) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("外部宿主目录")).Data!;
            var a = (await client.LinkCreateAsync("https://dup.example/full", title: "甲",
                description: "描述甲", listId: folder.FolderId, isImportant: true,
                faviconUrl: "https://dup.example/a.ico")).Data!;
            var b = (await client.LinkCreateAsync("https://dup.example/full", title: "乙",
                description: "描述乙", listId: folder.FolderId)).Data!;
            var c = (await client.LinkCreateAsync("https://dup.example/full", title: "丙")).Data!;
            await client.LinkVisitRecordAsync(b.LinkId);
            await client.LinkVisitRecordAsync(b.LinkId);
            await client.LinkCreateAsync("https://dup.example/other", title: "另一组 1");
            await client.LinkCreateAsync("https://dup.example/other", title: "另一组 2");

            // 外部宿主身份（CLI / MCP）：全量、完整字段，且绕过查询缓存（EngineCore 既定口径）。
            var external = new CallOptions(Caller: CallerRef.ExternalAgent);
            var groups = await client.DedupScanAsync(o: external);

            Assert.Equal(2, groups.Count);
            Assert.Equal(5, groups.Sum(g => g.Links.Count));

            var full = groups.Single(g => g.Url == "https://dup.example/full");
            Assert.Equal(3, full.Count);
            var stored = await client.LinkFindByUrlAsync("https://dup.example/full");
            Assert.Equal(
                stored.OrderBy(l => l.CreatedAt).ThenBy(l => l.LinkId, StringComparer.Ordinal)
                    .Select(l => l.LinkId).ToArray(),
                full.Links.Select(l => l.LinkId).ToArray());
            foreach (var link in full.Links)
            {
                var expected = stored.Single(l => l.LinkId == link.LinkId);
                Assert.Equal(expected.Url, link.Url);
                Assert.Equal(expected.Title, link.Title);
                Assert.Equal(expected.Description, link.Description);
                Assert.Equal(expected.FaviconUrl, link.FaviconUrl);
                Assert.Equal(expected.ListId, link.ListId);
                Assert.Equal(expected.VisitCount, link.VisitCount);
                Assert.Equal(expected.IsImportant, link.IsImportant);
                Assert.Equal(expected.CreatedAt, link.CreatedAt);
                Assert.Equal(expected.UpdatedAt, link.UpdatedAt);
                Assert.Equal(expected.LastVisitedAt, link.LastVisitedAt);
            }

            Assert.Equal(2, full.Links.Single(l => l.LinkId == b.LinkId).VisitCount);

            // UI 摘要模式（显式 include_links=false）：组/计数/首条目录一致，但不带链接正文。
            var lean = await client.DedupScanAsync(includeLinks: false, o: external);
            Assert.Equal(groups.Count, lean.Count);
            Assert.All(lean, g => Assert.Empty(g.Links));
            foreach (var group in lean)
            {
                var detailed = groups.Single(g => g.Url == group.Url);
                Assert.Equal(detailed.Count, group.Count);
                Assert.Equal(detailed.FirstListId, group.FirstListId);
            }
        }
        finally { AppTestEnv.Delete(db); }
    }

    private static List<DedupGroupRow> Rows(string url, params string[] ids)
        => new()
        {
            new DedupGroupRow
            {
                Url = url,
                Count = ids.Length,
                Links = ids.Select(id => new LinkDto
                {
                    LinkId = id,
                    Url = url,
                    Title = id,
                    CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                }).ToList(),
                LocationsSummary = Loc.K("nav.root.bookmarks"),
            },
        };
}
