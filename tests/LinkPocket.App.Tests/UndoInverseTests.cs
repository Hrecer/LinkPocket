using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 真正的撤销：内核级逆向链路金标准。
/// 覆盖面 = **移动 / 新建 / 复制副本 / 删文件夹**；**重命名与改属性明确不可撤销**。
/// 每条都断言"撤销后状态真的回到操作前、重做后再次生效"（黑盒经引擎命令读写，不开 internal 后门）。
/// </summary>
public class UndoInverseTests
{
    // —— 移动：撤销 = 移回原父（逆向参数带旧父；引擎无法从原参数反推）——

    [Fact]
    public async Task 移动文件夹_撤销后回原父_重做后再次移出()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var child = (await client.FolderCreateAsync("C", parentId: a.FolderId)).Data!;

            await client.FolderMoveAsync(child.FolderId, b.FolderId);
            var top = (await UndoEntries(client))[0];
            Assert.Equal("folders.move", top.Command);
            Assert.Equal("folders.move", Assert.Single(top.Steps).InverseCommand);
            Assert.Equal(b.FolderId, await ParentOf(client, child.FolderId));

            await client.UndoAsync();
            Assert.Equal(a.FolderId, await ParentOf(client, child.FolderId));      // 撤销 → 回 A

            await client.RedoAsync();
            Assert.Equal(b.FolderId, await ParentOf(client, child.FolderId));      // 重做 → 再移出
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 移动链接_撤销后回原目录_含移回根级()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var link = (await client.LinkCreateAsync("https://mv.example", title: "M",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;

            await client.LinkMoveBatchAsync(new[] { link.LinkId }, null);          // 移到根级
            Assert.Null((await client.LinkGetAsync(link.LinkId)).ListId);

            await client.UndoAsync();
            Assert.Equal(a.FolderId, (await client.LinkGetAsync(link.LinkId)).ListId);   // 撤销 → 回 A

            await client.RedoAsync();
            Assert.Null((await client.LinkGetAsync(link.LinkId)).ListId);          // 重做 → 再回根
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    // —— 新建 / 复制副本：撤销 = 移入回收站（软删除，可重做还原）——

    [Fact]
    public async Task 新建文件夹_撤销后进回收站_重做后还原()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var f = (await client.FolderCreateAsync("新建夹")).Data!;
            Assert.Contains(await client.FolderTreeAsync(), n => n.FolderId == f.FolderId);

            await client.UndoAsync();
            Assert.DoesNotContain(await client.FolderTreeAsync(), n => n.FolderId == f.FolderId);   // 主表已无
            Assert.Contains(await client.TrashTreeAsync(), t => t.TrashFolderId == f.FolderId);     // 进了回收站

            await client.RedoAsync();
            Assert.Contains(await client.FolderTreeAsync(), n => n.FolderId == f.FolderId);         // 重做 → 还原
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 新建链接_撤销后进回收站_重做后还原且原ID不变()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://new.example", title: "新",
                autoFetchMetadata: false)).Data!;

            await client.UndoAsync();
            var gone = await client.LinkListAsync(perPage: 0);
            Assert.DoesNotContain(gone.Links, l => l.LinkId == link.LinkId);                        // 主表已无
            Assert.Contains(await client.TrashListAsync(), t => t.Id == link.LinkId);               // 回收站保留原 ID

            await client.RedoAsync();
            var back = await client.LinkListAsync(perPage: 0);
            Assert.Contains(back.Links, l => l.LinkId == link.LinkId);                              // 原 ID 还原
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 新建链接_在文件夹内_撤销重做后回原文件夹()
    {
        // 回归：重做载荷必须**显式带落点**（曾残留已废弃参数 to_origin，靠缺省值兜底才碰巧正确）
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("目标夹")).Data!;
            var link = (await client.LinkCreateAsync("https://redo-in-folder.example", title: "F",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;

            await client.UndoAsync();
            Assert.Contains(await client.TrashListAsync(), t => t.Id == link.LinkId);

            await client.RedoAsync();
            Assert.Equal(folder.FolderId, (await client.LinkGetAsync(link.LinkId)).ListId);   // 回原文件夹（而非落根）
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 复制文件夹_撤销后副本进回收站_源不受影响()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var src = (await client.FolderCreateAsync("源夹")).Data!;
            var srcLink = (await client.LinkCreateAsync("https://src.example", title: "源链接",
                listId: src.FolderId, autoFetchMetadata: false)).Data!;

            var copy = (await client.FolderCopyAsync(src.FolderId, null)).Data!;
            Assert.Contains(await client.FolderTreeAsync(), n => n.FolderId == copy.NewFolderId);

            await client.UndoAsync();
            Assert.DoesNotContain(await client.FolderTreeAsync(), n => n.FolderId == copy.NewFolderId);  // 副本已撤
            Assert.Contains(await client.FolderTreeAsync(), n => n.FolderId == src.FolderId);            // 源仍在
            Assert.Contains((await client.LinkListAsync(perPage: 0)).Links,
                l => l.LinkId == srcLink.LinkId && l.ListId == src.FolderId);                            // 源链接未动
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    // —— 删文件夹：撤销 = 还原整单元（新命令 trash.restore_unit）——

    [Fact]
    public async Task 删文件夹_撤销后整子树回原父_含子夹与夹内链接()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var parent = (await client.FolderCreateAsync("父")).Data!;
            var unit = (await client.FolderCreateAsync("被删", parentId: parent.FolderId)).Data!;
            var sub = (await client.FolderCreateAsync("子夹", parentId: unit.FolderId)).Data!;
            var link = (await client.LinkCreateAsync("https://del.example", title: "夹内链接",
                listId: unit.FolderId, autoFetchMetadata: false)).Data!;

            await client.FolderDeleteAsync(unit.FolderId, "trash_links");
            Assert.DoesNotContain(await client.FolderTreeAsync(), n => n.FolderId == unit.FolderId);

            var top = (await UndoEntries(client))[0];
            Assert.Equal("folders.delete", top.Command);
            Assert.Equal("trash.restore_unit", Assert.Single(top.Steps).InverseCommand);

            await client.UndoAsync();
            var tree = await client.FolderTreeAsync();
            Assert.Contains(tree, n => n.FolderId == unit.FolderId);                 // 单元回来
            Assert.Contains(tree, n => n.FolderId == sub.FolderId);                  // 子夹一并回来
            Assert.Equal(parent.FolderId, tree.Single(n => n.FolderId == unit.FolderId).ParentId);  // 落回原父
            Assert.Equal(unit.FolderId, tree.Single(n => n.FolderId == sub.FolderId).ParentId);     // 层级保持
            Assert.Contains((await client.LinkListAsync(perPage: 0)).Links,
                l => l.LinkId == link.LinkId && l.ListId == unit.FolderId);          // 夹内链接回原夹
            Assert.DoesNotContain(await client.TrashTreeAsync(), t => t.TrashFolderId == unit.FolderId);  // 已出回收站
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 删文件夹_物理删除模式不可撤销()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var f = (await client.FolderCreateAsync("硬删")).Data!;
            await client.FolderDeleteAsync(f.FolderId, "delete_all");

            // 不可逆动作**绝不入撤销栈**（否则 Ctrl+Z 会给出"撤销成功"的假象）
            Assert.DoesNotContain(await UndoEntries(client), e => e.Command == "folders.delete");
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    // —— 删链接：撤销 = 还原回删除前目录（v5 修正：旧实现撤销后落根）——

    [Fact]
    public async Task 删链接_撤销后回删除前目录()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("原目录")).Data!;
            var link = (await client.LinkCreateAsync("https://undo-origin.example", title: "U",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;
            await client.LinkTrashAsync(link.LinkId);

            var top = (await UndoEntries(client))[0];
            Assert.Equal("links.trash", top.Command);
            var step = Assert.Single(top.Steps);
            Assert.Equal("trash.restore", step.InverseCommand);
            // 逆向参数必须带落点（to: origin）——描述符"退回原参数"会丢落点信息（旧实现 = 撤销后落根）
            Assert.Equal("origin", step.InverseArgs.GetProperty("to").GetString());

            await client.UndoAsync();
            Assert.Equal(folder.FolderId, (await client.LinkGetAsync(link.LinkId)).ListId);   // 回原目录

            await client.RedoAsync();
            Assert.Contains(await client.TrashListAsync(), t => t.Id == link.LinkId);          // 重做 → 再进回收站
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    // —— 批量还原：链接落回同批还原的单元 → 撤销步覆盖去重（只发单元步，撤销/重做不双次处理）——

    [Fact]
    public async Task 批量还原_链接落回同批单元_撤销重做往返()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("还原夹")).Data!;
            var link = (await client.LinkCreateAsync("https://undo-batch.example", title: "B",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;
            await client.LinkTrashAsync(link.LinkId);                        // 链接单独删除（回收站根）
            await client.FolderDeleteAsync(folder.FolderId, "trash_links");  // 目录整删（单元）

            // 混合批量还原：链接 origin = 同批还原的单元 → 落回夹内
            await client.TrashRestoreBatchAsync(new[] { link.LinkId }, new[] { folder.FolderId });
            Assert.Equal(folder.FolderId, (await client.LinkGetAsync(link.LinkId)).ListId);

            // 一次用户动作 = 一条撤销记录；链接落点被单元步覆盖 → 只发 1 步（覆盖去重）
            var top = (await UndoEntries(client))[0];
            Assert.Equal("trash.restore_batch", top.Command);
            Assert.Equal("folders.delete", Assert.Single(top.Steps).InverseCommand);

            // 撤销 → 单元（含夹内链接）回回收站
            await client.UndoAsync();
            Assert.Contains(await client.TrashTreeAsync(), t => t.TrashFolderId == folder.FolderId);
            Assert.Contains(await client.TrashUnitContentsAsync(folder.FolderId), e => e.Id == link.LinkId);
            Assert.DoesNotContain((await client.LinkListAsync(perPage: 0)).Links, l => l.LinkId == link.LinkId);

            // 重做 → 原样回来（链接回夹、原 ID 不变）
            await client.RedoAsync();
            Assert.Equal(folder.FolderId, (await client.LinkGetAsync(link.LinkId)).ListId);
            Assert.Contains(await client.FolderTreeAsync(), f => f.FolderId == folder.FolderId);
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    // —— 明确排除：重命名 / 改属性绝不入撤销栈 ——

    [Fact]
    public async Task 重命名与改属性_绝不入撤销栈()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var f = (await client.FolderCreateAsync("原名")).Data!;
            var link = (await client.LinkCreateAsync("https://r.example", title: "原题",
                listId: f.FolderId, autoFetchMetadata: false)).Data!;
            var before = (await UndoEntries(client)).Count;

            await client.FolderUpdateAsync(f.FolderId, name: "改名后");
            await client.LinkUpdateAsync(link.LinkId, title: "改题后");
            await client.LinkUpdateAsync(link.LinkId, description: "改描述");
            await client.LinkUpdateAsync(link.LinkId, isImportant: true);

            // 一条都不该新增（重命名与改属性不属于可撤销动作）
            Assert.Equal(before, (await UndoEntries(client)).Count);
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    // —— 批量：同一次用户动作合并为一条（一次 Ctrl+Z 撤销整批）——

    [Fact]
    public async Task 分组_同组多次调用合并为一条撤销记录()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var target = (await client.FolderCreateAsync("目标")).Data!;
            var l1 = (await client.LinkCreateAsync("https://g1.example", title: "G1", autoFetchMetadata: false)).Data!;
            var l2 = (await client.LinkCreateAsync("https://g2.example", title: "G2", autoFetchMetadata: false)).Data!;
            var before = (await UndoEntries(client)).Count;

            // 一次"粘贴多选"：两次顶层调用携带同一分组 ID
            var group = Guid.NewGuid().ToString("N");
            var o = new CallOptions(UndoGroupId: group);
            await client.LinkMoveBatchAsync(new[] { l1.LinkId }, target.FolderId, o);
            await client.LinkMoveBatchAsync(new[] { l2.LinkId }, target.FolderId, o);

            var entries = await UndoEntries(client);
            Assert.Equal(before + 1, entries.Count);                 // 两条调用 → 一条记录
            Assert.Equal(2, entries[0].Steps.Count);
            Assert.Equal(group, entries[0].GroupId);

            await client.UndoAsync();                                // 一次 Ctrl+Z 撤销整个动作
            Assert.Null((await client.LinkGetAsync(l1.LinkId)).ListId);
            Assert.Null((await client.LinkGetAsync(l2.LinkId)).ListId);
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 撤销链推进_条目被消费_不会卡在同一条上()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var link = (await client.LinkCreateAsync("https://x.example", title: "X",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;

            await client.LinkMoveBatchAsync(new[] { link.LinkId }, b.FolderId);   // 移动（可撤销）
            var beforeCount = (await UndoEntries(client)).Count;

            await client.UndoAsync();
            Assert.Equal(a.FolderId, (await client.LinkGetAsync(link.LinkId)).ListId);
            Assert.Equal(beforeCount - 1, (await UndoEntries(client)).Count);      // 条目已消费

            await client.UndoAsync();                                              // 继续推进（撤销新建链接）
            Assert.Equal(beforeCount - 2, (await UndoEntries(client)).Count);
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 撤销重做_仅列表上下文可用_详情页或编辑器打开时让位()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var vm = new LinkPocket.ViewModels.BrowserViewModel(client);
            await vm.LoadAsync(null);
            var link = (await client.LinkCreateAsync("https://gate.example", title: "门",
                autoFetchMetadata: false)).Data!;
            await vm.LoadAsync(null);
            await vm.RefreshUndoStateAsync();

            var row = vm.Rows.Single(r => r.Id == link.LinkId);
            vm.SelectRowWithModifiers(row, System.Windows.Input.ModifierKeys.None);
            Assert.True(vm.IsListContextActive);
            Assert.True(vm.UndoCommand.CanExecute(null));                 // 列表上下文：可撤销
            Assert.True(vm.CutCommand.CanExecute(null));

            // 详情页覆盖列表 → 列表动作（危险键）一律让位
            await vm.OpenDetailPageAsync(row);
            Assert.False(vm.IsListContextActive);
            Assert.False(vm.UndoCommand.CanExecute(null));
            Assert.False(vm.CutCommand.CanExecute(null));
            vm.CloseDetailPage();
            Assert.True(vm.UndoCommand.CanExecute(null));

            // 编辑器覆盖列表 → 同样让位
            vm.OpenEditorForCreate();
            Assert.False(vm.UndoCommand.CanExecute(null));
            Assert.False(vm.CutCommand.CanExecute(null));
            vm.CloseEditorPage();
            Assert.True(vm.UndoCommand.CanExecute(null));
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 超过撤销栈容量后_新动作仍可撤销()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            // 铺满 100 条历史（再多的动作应淘汰最旧的，而不是把新动作弹掉）
            for (var i = 0; i < 105; i++) await client.FolderCreateAsync($"容量 {i}");
            var before = (await client.FolderTreeAsync()).Count;

            await client.UndoAsync();

            var names = (await client.FolderTreeAsync()).Select(f => f.Name).ToHashSet();
            Assert.DoesNotContain("容量 104", names);   // 撤销的必须是**最新**动作
            Assert.Contains("容量 0", names);           // 最旧的动作仍在（只是被容量淘汰出栈）
            Assert.Equal(before - 1, names.Count);
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 超过容量后_分组批量删除仍可整体撤销()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            for (var i = 0; i < 105; i++) await client.FolderCreateAsync($"历史 {i}");
            var folder = (await client.FolderCreateAsync("批量目标")).Data!;
            var ids = new List<string>();
            for (var i = 0; i < 5; i++)
                ids.Add((await client.LinkCreateAsync($"https://bulk{i}.example", title: $"B{i}",
                    listId: folder.FolderId, autoFetchMetadata: false)).Data!.LinkId);

            // 一次批量删除 = 同组多步 → 一条撤销记录（撤销时合并为 restore_batch）
            var group = Guid.NewGuid().ToString("N");
            foreach (var id in ids) await client.LinkTrashAsync(id, new CallOptions(UndoGroupId: group));
            var entry = (await UndoEntries(client))[0];
            Assert.Equal(5, entry.Steps.Count);

            await client.UndoAsync();

            foreach (var id in ids)
            {
                var link = await client.LinkGetAsync(id);
                Assert.NotNull(link);                       // 全部还原（不再是"撤销一条很旧的动作"）
                Assert.Equal(folder.FolderId, link.ListId);
            }
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 批量删除命令_一次撤销全部还原_重做全部再删()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("批量删")).Data!;
            var ids = new List<string>();
            for (var i = 0; i < 5; i++)
                ids.Add((await client.LinkCreateAsync($"https://batch-trash-undo.example/{i}", title: $"T{i}",
                    listId: folder.FolderId, autoFetchMetadata: false)).Data!.LinkId);

            var result = await client.LinkTrashBatchAsync(ids);
            Assert.Equal(5, result.Data!.Affected);
            foreach (var id in ids) Assert.Null(await TryLinkAsync(client, id));

            var undo = await client.UndoAsync();   // 一条撤销记录 → 一次 restore_batch
            Assert.True(undo.Ok);
            foreach (var id in ids)
            {
                var link = await TryLinkAsync(client, id);
                Assert.NotNull(link);
                Assert.Equal(folder.FolderId, link!.ListId);   // 保留原位置
            }

            var redo = await client.RedoAsync();   // 一条重做动作 → 一次 links.trash_batch
            Assert.True(redo.Ok);
            foreach (var id in ids) Assert.Null(await TryLinkAsync(client, id));
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 批量创建_撤销走批量删除_重做全部按原ID恢复()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("批量建")).Data!;
            var group = Guid.NewGuid().ToString("N");
            var ids = new List<string>();
            for (var i = 0; i < 10; i++)
                ids.Add((await client.LinkCreateAsync($"https://batch-create-undo.example/{i}", title: $"C{i}",
                    listId: folder.FolderId, autoFetchMetadata: false,
                    o: new CallOptions(UndoGroupId: group))).Data!.LinkId);

            await client.UndoAsync();   // 10 步 links.trash → 合并为一次 trash_batch
            Assert.Empty((await client.FolderContentsAsync(folder.FolderId)).Links);

            await client.RedoAsync();   // 10 步 trash.restore → 合并为一次 restore_batch
            var restored = (await client.FolderContentsAsync(folder.FolderId)).Links;
            Assert.Equal(10, restored.Count);
            Assert.Equal(ids.OrderBy(x => x), restored.Select(x => x.LinkId).OrderBy(x => x));
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 批量撤销_部分回收站快照丢失_其余仍还原并如实告警()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("部分丢失")).Data!;
            var ids = new List<string>();
            for (var i = 0; i < 3; i++)
                ids.Add((await client.LinkCreateAsync($"https://partial-undo.example/{i}", title: $"P{i}",
                    listId: folder.FolderId, autoFetchMetadata: false)).Data!.LinkId);

            var group = Guid.NewGuid().ToString("N");
            foreach (var id in ids) await client.LinkTrashAsync(id, new CallOptions(UndoGroupId: group));

            // 模拟一条回收站快照已被外部永久清理（批量还原的预检必须失败）
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM trash_links WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", ids[0]);
                cmd.ExecuteNonQuery();
            }

            var result = await client.UndoAsync();

            Assert.True(result.Ok);
            Assert.NotEmpty(result.Changes!.Warnings!);          // 失败项如实上报，不静默
            Assert.Null(await TryLinkAsync(client, ids[0]));     // 快照丢失的那条不伪造恢复
            foreach (var id in ids.Skip(1))
            {
                var link = await TryLinkAsync(client, id);
                Assert.NotNull(link);                            // 其余照常还原（不是整批失败）
                Assert.Equal(folder.FolderId, link!.ListId);
            }
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 批量重做_部分回收站快照丢失_其余仍还原并如实告警()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("重做部分丢失")).Data!;
            var ids = new List<string>();
            var group = Guid.NewGuid().ToString("N");
            for (var i = 0; i < 3; i++)
                ids.Add((await client.LinkCreateAsync($"https://partial-redo.example/{i}", title: $"R{i}",
                    listId: folder.FolderId, autoFetchMetadata: false,
                    o: new CallOptions(UndoGroupId: group))).Data!.LinkId);

            await client.UndoAsync();   // 批量撤销新建 → 3 条进回收站；重做 = 从回收站还原
            Assert.Null(await TryLinkAsync(client, ids[0]));   // 已在回收站（主表没有）

            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM trash_links WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", ids[0]);
                cmd.ExecuteNonQuery();
            }

            var result = await client.RedoAsync();

            Assert.True(result.Ok);
            Assert.NotEmpty(result.Changes!.Warnings!);
            Assert.Null(await TryLinkAsync(client, ids[0]));
            foreach (var id in ids.Skip(1)) Assert.NotNull(await TryLinkAsync(client, id));
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 撤销步骤全部失败_返回可解析结果_不变成内部错误()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://undofail.example", title: "F",
                autoFetchMetadata: false)).Data!;
            await client.LinkTrashAsync(link.LinkId);   // 先移入回收站 → 再撤销"新建链接"（逆向 = links.trash）必失败

            var createEntry = (await UndoEntries(client)).First(e => e.Command == "links.create");
            var result = await client.UndoAsync(createEntry.Id);   // 曾因 lastData=null 强转 JsonElement 抛 NRE

            Assert.True(result.Ok);
            Assert.NotNull(result.Changes?.Warnings);
            Assert.NotEmpty(result.Changes!.Warnings!);
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    [Fact]
    public async Task 撤销新建链接_定位不误报失败_且列表立即移除()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var dialogs = new RecordingDialogs();
            var vm = new BrowserViewModel(client, new UiPortProvider { Dialogs = dialogs });
            await vm.LoadAsync(null);
            var link = (await client.LinkCreateAsync("https://undo-locate.example", title: "定位",
                autoFetchMetadata: false)).Data!;
            await vm.LoadAsync(null);
            await vm.RefreshUndoStateAsync();
            vm.SelectRowWithModifiers(vm.Rows.Single(r => r.Id == link.LinkId),
                System.Windows.Input.ModifierKeys.None);
            Assert.True(vm.UndoCommand.CanExecute(null));

            vm.UndoCommand.Execute(null);   // 撤销"新建链接"：目标已不存在，定位不得抛 EntityNotFound

            Assert.True(await WaitUntilAsync(() => vm.Rows.All(r => r.Id != link.LinkId), TimeSpan.FromSeconds(5)),
                "撤销后列表应立即移除该行（不是等 300ms 防抖）");
            Assert.Empty(dialogs.Alerts);   // 曾被 LinkGetAsync 的 EntityNotFound 误报成"撤销失败"
            Assert.True(vm.CanRedo);
        }
        finally { AppTestEnv.Delete(dbPath); }
    }

    // —— 工具 ——

    private sealed class RecordingDialogs : IDialogService
    {
        public List<(string Title, string Message)> Alerts { get; } = new();
        public bool ConfirmDeleteFolder(string folderName) => true;
        public bool Confirm(string title, string message, string? confirmText = null, string iconKind = "delete-outline") => true;
        public void Alert(string title, string message) => Alerts.Add((title, message));
    }

    private static async Task<LinkDto?> TryLinkAsync(EngineClient client, string id)
    {
        try { return await client.LinkGetAsync(id); }
        catch (EngineException ex) when (ex.Error.Code == EngineErrors.EntityNotFound) { return null; }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    private static async Task<string?> ParentOf(EngineClient client, string folderId)
        => (await client.FolderTreeAsync()).FirstOrDefault(f => f.FolderId == folderId)?.ParentId;

    /// <summary>撤销栈快照（undo.list 的 steps 形状 → 强类型）。</summary>
    private static async Task<IReadOnlyList<UndoEntry>> UndoEntries(EngineClient client)
    {
        var list = await client.UndoListAsync();
        return list.GetProperty("entries").EnumerateArray()
            .Select(e => new UndoEntry(
                e.GetProperty("id").GetString()!,
                DateTimeOffset.Parse(e.GetProperty("at").GetString()!),
                e.GetProperty("steps").EnumerateArray()
                    .Select(st => new UndoStep(
                        st.GetProperty("command").GetString()!,
                        st.GetProperty("args").Clone(),
                        st.GetProperty("inverse_command").GetString()!,
                        st.GetProperty("inverse_args").Clone()))
                    .ToArray(),
                CallerRef.Test,
                e.TryGetProperty("group_id", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null))
            .ToList();
    }
}
