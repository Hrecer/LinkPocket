using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// <c>links.move_batch</c> 的**目标语义**回归网（2026-09-26 实数据事故）：
/// 用户让 AI"把文件夹里的东西全部放到根目录、把文件夹拆掉"，AI 用了
/// <c>target_list_id: null</c> ⇒ 命令**报 ok、changes 里还写着 touched links**，但链接**根本没移动**；
/// 紧接着的 <c>folders.delete(cascade: trash_links)</c> 把它们连文件夹一起扫进了回收站（1630 条）。
/// 文档口径：<c>target_list_id</c> 缺省 = 根级 —— 缺省与"显式 null"必须**等价**，且都必须真的移动。
/// </summary>
public class MoveBatchTargetTests
{
    [Fact]
    public async Task 目标字段缺省_必须真的移到根级()
        => await AssertMovedToRoot(argsJson: """{ "link_ids": ["__IDS__"] }""");

    [Fact]
    public async Task 目标显式传null_必须与缺省等价_真的移到根级()
        => await AssertMovedToRoot(argsJson: """{ "link_ids": ["__IDS__"], "target_list_id": null }""");

    /// <summary>建一个文件夹 + 一条链接放进去，按给定 args 调 move_batch，断言链接真的到了根级。</summary>
    private static async Task AssertMovedToRoot(string argsJson)
    {
        var (engine, _, dbPath) = TestHost.Create();
        var folder = await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "movebatch目标" });
        var folderId = folder.Data!.FolderId;
        var link = await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://movebatch.test/1", title = "移动目标", list_id = folderId });
        var linkId = link.Data!.LinkId;

        var args = JsonSerializer.Deserialize<JsonElement>(argsJson.Replace("__IDS__", linkId));
        var result = await engine.ExecuteAsync<LinkBatchResult>("links.move_batch", args);
        Assert.True(result.Ok, "move_batch 应成功（目标缺省/显式 null = 根级）");

        // 判据 = **直接读库**（不看它报的 changes、也不看命令回读——这次事故正是"报成功没做事"）
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select folder_id from links where id = $id";
        cmd.Parameters.AddWithValue("$id", linkId);
        var inDb = cmd.ExecuteScalar();
        Assert.True(inDb is null or DBNull,
            $"链接应已到根级（DB 里 folder_id 为空），实际 DB folder_id={inDb}");

        // 读路径也必须跟着动（否则界面/AI 会在刷新前一直看到旧归属 —— 事故里 AI 正是被这类读数绕晕）
        var after = await engine.QueryAsync<LinkDto>("links.get", new { id = linkId });
        Assert.True(string.IsNullOrEmpty(after.ListId),
            $"读路径应看到新归属（根级），实际 links.get list_id={after.ListId}");
    }
}
