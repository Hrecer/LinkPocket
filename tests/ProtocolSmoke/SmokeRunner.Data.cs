using LinkPocket.Api;
using LinkPocket.Contracts;

namespace ProtocolSmoke;

/// <summary>§2 数据流（排序口径/分页/同名编号/面包屑/环检测）+ §3 回收站闭环（含两阶段确认）。</summary>
internal static partial class SmokeRunner
{
    private static async Task SectionDataFlow(SmokeState s)
    {
        var client = s.Client;

        // 建树：folder → 3 链接 + sub → 1 链接
        var folder = (await client.FolderCreateAsync("测试目录")).Data!;
        await client.LinkCreateAsync("https://example.com/1", "链接1", listId: folder.FolderId);
        await client.LinkCreateAsync("https://example.com/2", "链接2", listId: folder.FolderId);
        await client.LinkCreateAsync("https://example.com/3", "链接3", listId: folder.FolderId);
        var sub = (await client.FolderCreateAsync("子目录", parentId: folder.FolderId)).Data!;
        await client.LinkCreateAsync("https://example.com/sub", "子链接", listId: sub.FolderId);

        // 分页 + direct_link_count 直接子链接语义
        var p1 = (await client.FolderContentsAsync(folder.FolderId, perPage: 2));
        Asserts.That(p1.Links.Count == 2 && p1.CurrentPage == 1 && p1.LastPage == 2 && p1.PerPage == 2,
            "分页第一页应为 2 条 / last_page 2");
        Asserts.That(p1.DirectLinkCount == 3, $"直接子链接计数应为 3（不含子目录），实际 {p1.DirectLinkCount}");
        var p2 = (await client.FolderContentsAsync(folder.FolderId, page: 2, perPage: 2));
        Asserts.That(p2.Links.Count == 1 && p2.CurrentPage == 2, "分页第二页应为 1 条");

        // 默认排序 = 名称升序（方案红线 3）
        var full = (await client.FolderContentsAsync(folder.FolderId));
        Asserts.That(full.Links.Select(l => l.Title).SequenceEqual(["链接1", "链接2", "链接3"]),
            "默认排序应为名称升序");
        var desc = (await client.FolderContentsAsync(folder.FolderId, sortOrder: "desc"));
        Asserts.That(desc.Links.Select(l => l.Title).SequenceEqual(["链接3", "链接2", "链接1"]),
            "desc 排序应反转");

        // 面包屑（根显示名 + 层级）
        var breadcrumb = (await client.FolderBreadcrumbAsync(sub.FolderId));
        Asserts.That(breadcrumb.SequenceEqual([FolderIds.RootDisplayName, "测试目录", "子目录"]),
            "面包屑应为 全部书签 / 测试目录 / 子目录");

        // 树 + 子目录 LinkCount（递归）与 direct_link_count（直接）两口径并存
        var tree = (await client.FolderTreeAsync());
        var treeNode = tree.First(f => f.Name == "测试目录");
        Asserts.That(treeNode.LinkCount == 4, "树节点 LinkCount 应为递归 4 条");
        Asserts.That(treeNode.DirectLinkCount == 3, "树节点 DirectLinkCount 应为直接 3 条");

        // 环检测：移入自己的子树必拒
        var cycle = (await client.FolderCycleCheckAsync(folder.FolderId, sub.FolderId));
        Asserts.That(cycle, "移动父目录到子目录应判定为环");

        // 同名文件夹自动编号（Windows 口径「工作 (2)」）
        var dup1 = (await client.FolderCreateAsync("重名")).Data!;
        var dup2 = (await client.FolderCreateAsync("重名")).Data!;
        var dup3 = (await client.FolderCreateAsync("重名")).Data!;
        var target = (await client.FolderCreateAsync("归档重名")).Data!;
        var moveBatch = (await client.FolderMoveBatchAsync([dup1.FolderId, dup2.FolderId, dup3.FolderId],
            target.FolderId)).Data!;
        Asserts.That(moveBatch.Moved == 3, "批量移动应处理 3 条");
        var targetContents = (await client.FolderContentsAsync(target.FolderId));
        var names = targetContents.SubFolders.Select(f => f.Name).ToList();
        Asserts.That(names.Contains("重名") && names.Contains("重名 (2)") && names.Contains("重名 (3)"),
            $"同名文件夹应自动编号，实际 {string.Join(", ", names)}");

        Console.WriteLine("[OK] §2 数据流：分页/直接子计数/名称升序/面包屑/递归计数/环检测/同名自动编号");
    }

    private static async Task SectionTrash(SmokeState s)
    {
        var client = s.Client;
        s.Events.Clear();

        // 单独删除 → 平铺条目 + 无树节点；还原落根
        var folder = (await client.FolderCreateAsync("回收源目录")).Data!;
        var link = (await client.LinkCreateAsync("https://trash.example/x", "回收书签", listId: folder.FolderId)).Data!;
        var trashed = (await client.LinkTrashAsync(link.LinkId)).Data!;
        Asserts.That(trashed.LinkId == link.LinkId, "回收站保留原链接 ID");
        Asserts.That(trashed.OriginPath == "全部书签 / 回收源目录",
            $"origin_path 快照应为「全部书签 / 回收源目录」");
        Asserts.That(s.Events.Contains("trash.changed"), "移入回收站应推 trash.changed");

        var flat = (await client.TrashListAsync());
        var entry = flat.FirstOrDefault(e => e.Id == link.LinkId && e.EntryType == "link");
        Asserts.That(entry != null, "单独删除书签应出现在回收站平铺列表");
        Asserts.That(!(await client.TrashTreeAsync()).Any(f => f.TrashFolderId == link.LinkId),
            "单独删除书签不应产生回收站树节点");

        await client.TrashRestoreAsync(link.LinkId);
        Asserts.That(s.Events.Contains("trash.changed"), "还原应推 trash.changed");
        Asserts.That(!(await client.TrashListAsync()).Any(e => e.Id == link.LinkId), "还原后平铺条目应消失");
        var restored = (await client.LinkGetAsync(link.LinkId));
        Asserts.That(restored.ListId == null, "还原固定落根（行为等价项）");

        // 文件夹整树进回收站 → 平铺 folder 条目 + 树单元（含子树书签计数）→ 永久删除（两阶段确认）
        var child = (await client.FolderCreateAsync("回收子目录", parentId: folder.FolderId)).Data!;
        await client.LinkCreateAsync("https://trash.example/sub", "子树书签", listId: child.FolderId);
        await client.FolderDeleteAsync(folder.FolderId);   // 默认 cascade = trash_links
        Asserts.That(s.Events.Contains("trash.changed"), "文件夹整树入回收站应推 trash.changed");

        var mainLinks = (await client.LinkListAsync(perPage: 0)).Links;
        Asserts.That(!mainLinks.Any(l => l.Title == "子树书签"), "被删子树书签应从主表消失");
        Asserts.That(!(await client.FolderTreeAsync()).Any(f => f.FolderId == folder.FolderId),
            "被删文件夹应从主树消失");

        var trashTree = (await client.TrashTreeAsync());
        var rootUnit = trashTree.FirstOrDefault(f => f.Name == "回收源目录" && f.ParentTrashFolderId == null);
        Asserts.That(rootUnit != null, "删除根单元应挂在回收站根");
        Asserts.That(rootUnit!.LinkCount == 1, $"删除根单元 LinkCount 应为 1（子树书签），实际 {rootUnit.LinkCount}");

        var flatAfter = (await client.TrashListAsync());
        var folderEntry = flatAfter.FirstOrDefault(e => e.EntryType == "folder" && e.Name == "回收源目录");
        Asserts.That(folderEntry != null, "删除根单元应以 folder 条目出现在平铺列表");
        Asserts.That(folderEntry!.OriginPath == "全部书签 / 回收源目录",
            $"folder 条目 origin_path 应为完整路径快照，实际「{folderEntry.OriginPath}」");

        var unitContents = (await client.TrashUnitContentsAsync(folder.FolderId));
        Asserts.That(unitContents.Any(e => e.EntryType == "link" && e.OriginPath == "全部书签 / 回收源目录 / 回收子目录"),
            "单元内书签快照应带完整 origin_path");

        // purge：无令牌 = CONFIRM_REQUIRED（token 在 Details 下发）→ 持令牌重调 → 条目消失
        EngineException confirm;
        try
        {
            await client.TrashPurgeAsync(rootUnit!.TrashFolderId, isFolder: true);
            throw new Exception("无令牌 purge 必须抛 CONFIRM_REQUIRED");
        }
        catch (EngineException ex)
        {
            confirm = ex;
        }
        Asserts.That(confirm.Error.Code == EngineErrors.ConfirmRequired, "purge 无令牌应报 LP.SEC.003");
        Asserts.That(confirm.Error.Details!.Value.TryGetProperty("confirm_token", out _),
            "CONFIRM_REQUIRED 的 Details 应带 confirm_token");
        Asserts.That(confirm.Error.Details!.Value.TryGetProperty("ttl_seconds", out _),
            "CONFIRM_REQUIRED 的 Details 应带 ttl_seconds");

        await client.TrashPurgeAsync(rootUnit!.TrashFolderId, isFolder: true,
            new CallOptions(ConfirmToken: confirm.Error.Details!.Value.GetProperty("confirm_token").GetString()));
        Asserts.That(!(await client.TrashTreeAsync()).Any(f => f.Name == "回收源目录"),
            "永久删除后树单元应消失");
        Asserts.That(!(await client.TrashListAsync()).Any(e => e.EntryType == "folder" && e.Name == "回收源目录"),
            "永久删除后平铺 folder 条目应消失");

        Console.WriteLine("[OK] §3 回收站闭环：原 ID + origin_path 快照 / 树与平铺 / 还原落根 / purge 两阶段确认");
    }
}
