using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Engine;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 命名体系收敛后的证据：三条此前"不彻底"的路径都必须走**单一命名服务**
/// 的同层占用表 —— 批量移动、复制子树、还原单元（含单元内子层）。
///
/// <para>为什么要有"坏数据"用例：v4 唯一索引 `idx_folders_parent_name` 在正常写入路径下已挡死
/// "同层重名"，所以复制子树的兜底在合法数据上永不触发（这正是它"无行为变化"的原因）。
/// 但外部来源/历史数据一旦带着重名进来，旧写法（直写原名）会让**整条命令被索引拒绝**，
/// 因此这里显式移除索引造出那种状态，验证兜底是"编号"而不是"整条失败"。</para>
/// </summary>
public class NamingServiceTests
{
    /// <summary>批量移动（A-1）：占用表先 Seed 目标层**被占用**名、再逐项累积；本次移入的项自身不算占用者。</summary>
    [Fact]
    public async Task MoveBatch_Seeds_Target_Level_And_Accumulates()
    {
        var (engine, _, _) = TestHost.Create();
        var dest = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "归档" });
        var staging = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "中转" });

        // 目标层里已经有「工作」（本次要"原地不动"再移一次）与「工作 (2)」（留在目标层，构成占用）
        var stays = await engine.ExecuteAsync<FolderDto>(
            "folders.create", new { name = "工作", parent_id = dest.Data!.FolderId });
        await engine.ExecuteAsync<FolderDto>(
            "folders.create", new { name = "工作 (2)", parent_id = dest.Data!.FolderId });
        // 外部来源的同名项（在别处，本次一起移入）
        var incoming = await engine.ExecuteAsync<FolderDto>(
            "folders.create", new { name = "工作", parent_id = staging.Data!.FolderId });

        var result = await engine.ExecuteAsync<FolderMoveBatchResult>("folders.move_batch",
            new { folder_ids = new[] { stays.Data!.FolderId, incoming.Data!.FolderId }, target_parent_id = dest.Data!.FolderId });

        Assert.Equal(2, result.Data!.Moved);
        // 「工作 (2)」是占用者；本次移入的两项自身不算占用 → 先落「工作」，第二项撞名顺延到「工作 (3)」
        Assert.Single(result.Data!.RenamedNotes);

        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", new { folder_id = dest.Data!.FolderId });
        var names = contents.SubFolders.Select(f => f.Name).ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains("工作", names);
        Assert.Contains("工作 (2)", names);
        Assert.Contains("工作 (3)", names);
    }

    /// <summary>
    /// 复制子树（A-2）：子层同样经命名服务累积。造出"源子树内部同层重名"（移除 v4 唯一索引后直插，
    /// 模拟外部/历史来源）→ 副本子层必须被编号，而不是让整条 folders.copy 被索引拒绝。
    /// </summary>
    [Fact]
    public async Task Copy_Subtree_Numbers_Duplicate_Source_Siblings()
    {
        var (engine, factory, _) = TestHost.Create();

        // 正常路径写入不出来的数据：v4 唯一索引挡死同层重名，这里显式移除它来模拟外部来源
        await using (var ctx = factory.CreateDbContext())
            await ctx.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS idx_folders_parent_name;");

        var src = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "源" });
        await using (var ctx = factory.CreateDbContext())
        {
            var now = DateTime.UtcNow.ToString("O");
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO folders (id, parent_id, name, sort_order, created_at, updated_at, visit_count) " +
                "VALUES ('111111111111', {0}, '子', 0, {1}, {1}, 0), ('222222222222', {0}, '子', 1, {1}, {1}, 0)",
                src.Data!.FolderId, now);
        }

        var copy = await engine.ExecuteAsync<FolderCopyResult>("folders.copy", new { folder_id = src.Data!.FolderId });
        var contents = await engine.QueryAsync<FolderContentsDto>("folders.contents", new { folder_id = copy.Data!.NewFolderId });
        var names = contents.SubFolders.Select(f => f.Name).ToList();

        Assert.Equal(2, names.Count);
        Assert.Contains("子", names);
        Assert.Contains("子 (2)", names);
    }

    /// <summary>
    /// 还原单元（A-3）：落点层（单元根）+ **单元内子层**都经占用表。
    /// 造出"单元内同层重名"（回收站表没有唯一索引，直插模拟坏数据）→ 还原必须编号，
    /// 而不是撞 v4 唯一索引让整条还原失败（旧写法直写 unit.Name 的风险）。
    /// </summary>
    [Fact]
    public async Task RestoreUnit_Numbers_Duplicate_Names_Inside_Unit()
    {
        var (engine, factory, _) = TestHost.Create();

        await using (var ctx = factory.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            ctx.Add(new TrashedFolder { TrashFolderId = "111111111111", Name = "单元", DeletedAt = now });
            ctx.Add(new TrashedFolder
            {
                TrashFolderId = "222222222222", Name = "子",
                ParentTrashFolderId = "111111111111", DeletedAt = now,
            });
            ctx.Add(new TrashedFolder
            {
                TrashFolderId = "333333333333", Name = "子",
                ParentTrashFolderId = "111111111111", DeletedAt = now,
            });
            await ctx.SaveChangesAsync();
        }

        await engine.ExecuteAsync<object>("trash.restore_unit", new { unit_id = "111111111111" });

        var atRoot = await engine.QueryAsync<FolderContentsDto>("folders.contents", null);
        Assert.Contains("单元", atRoot.SubFolders.Select(f => f.Name));

        var inner = await engine.QueryAsync<FolderContentsDto>("folders.contents", new { folder_id = "111111111111" });
        var childNames = inner.SubFolders.Select(f => f.Name).ToList();
        Assert.Equal(2, childNames.Count);
        Assert.Contains("子", childNames);
        Assert.Contains("子 (2)", childNames);
    }

    /// <summary>还原单元撞落点层既有名 → 单元根编号（落点层占用名由命名服务预置，不靠调用方查库）。
    /// 回收站保留原文件夹 ID，故单元 ID 就是被删文件夹的 ID。</summary>
    [Fact]
    public async Task RestoreUnit_Numbers_When_Landing_Already_Holds_Same_Name()
    {
        var (engine, _, _) = TestHost.Create();
        var doomed = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "资料", parent_id = doomed.Data!.FolderId });
        await engine.ExecuteAsync<FolderDeleteResult>("folders.delete",
            new { folder_id = doomed.Data!.FolderId, cascade = "trash_links" });

        // 位置被新的「工作」顶掉 → 还原单元根必须编号
        await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });

        await engine.ExecuteAsync<object>("trash.restore_unit", new { unit_id = doomed.Data!.FolderId });

        var root = await engine.QueryAsync<FolderContentsDto>("folders.contents", null);
        var names = root.SubFolders.Select(f => f.Name).ToList();
        Assert.Contains("工作", names);
        Assert.Contains("工作 (2)", names);
    }
}
