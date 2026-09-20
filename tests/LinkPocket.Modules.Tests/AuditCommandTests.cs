using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using LinkPocket.Kernel;
using LinkPocket.Modules.Maintenance;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// audit.query / audit.prune 黑盒语义（S2）：**审计读侧**是 AI 自省与排障的唯一入口，
/// 这里覆盖过滤矩阵 / 分页 / 负载开关 / 干跑标记 / 保留清理与两阶段确认。
/// 口径：SqlAudit=true 的宿主（读的就是 audit_log 表），其余断言一律落在可观测结果（返回值 / 行数 / 错误码）。
/// </summary>
public class AuditCommandTests
{
    private static async Task<AuditPagedResult> QueryAsync(EngineCore engine, object? args = null)
        => await engine.QueryAsync<AuditPagedResult>("audit.query", args);

    /// <summary>制造审计行（只关心"发生过"，不关心返回 DTO 形态：T=object 兼容任意处理器结果类型）。</summary>
    private static Task<CommandResult<object>> Run(
        EngineCore engine, string command, object? args = null, CallOptions? options = null)
        => engine.ExecuteAsync<object>(command, args, options);

    [Fact]
    public async Task 查询_倒序分页_失败行带错误码()
    {
        var (engine, _, _) = TestHost.CreateWithAudit();
        await Run(engine, "folders.create", new { name = "甲" });
        await Run(engine, "folders.create", new { name = "乙" });
        var folder = await engine.QueryAsync<List<FolderDto>>("folders.find", new { name = "甲" });
        var fail = await Assert.ThrowsAsync<EngineException>(
            () => Run(engine, "folders.move", new { folder_id = folder[0].FolderId, target_parent_id = "000000000000" }));
        Assert.Equal(EngineErrors.EntityNotFound, fail.Error.Code);

        var all = await QueryAsync(engine);
        Assert.Equal(3, all.Total);
        Assert.Equal(3, all.Items.Count);
        Assert.Equal("folders.move", all.Items[0].Command);   // 新→旧
        Assert.False(all.Items[0].Success);
        Assert.Equal(EngineErrors.EntityNotFound, all.Items[0].ErrorCode);
        Assert.All(all.Items.Skip(1), row => Assert.True(row.Success));

        var firstPage = await QueryAsync(engine, new { page = 1, per_page = 2 });
        Assert.Equal(3, firstPage.Total);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(2, firstPage.PageCount);
    }

    [Fact]
    public async Task 查询_按命令与关联与时间过滤()
    {
        var (engine, _, _) = TestHost.CreateWithAudit();
        var corr = "corr-audit-1";
        await Run(engine, "folders.create", new { name = "带关联" },
            new CallOptions(CorrelationId: corr));
        await Run(engine, "folders.create", new { name = "无关联" });

        var byCommand = await QueryAsync(engine, new { command = "folders.create" });
        Assert.Equal(2, byCommand.Total);

        var byCorr = await QueryAsync(engine, new { correlation_id = corr });
        var row = Assert.Single(byCorr.Items);
        Assert.Equal(corr, row.CorrelationId);

        // 时间半开区间：未来下界 = 无命中；过去上界 = 无命中
        var future = DateTimeOffset.Now.AddDays(1).ToString("O");
        var past = DateTimeOffset.Now.AddDays(-1).ToString("O");
        Assert.Equal(0, (await QueryAsync(engine, new { from = future })).Total);
        Assert.Equal(0, (await QueryAsync(engine, new { to = past })).Total);
        Assert.Equal(2, (await QueryAsync(engine, new { from = past, to = future })).Total);
    }

    [Fact]
    public async Task 查询_负载开关与截断标记()
    {
        var (engine, _, _) = TestHost.CreateWithAudit();
        await Run(engine, "folders.create", new { name = "负载开关" });

        var slim = await QueryAsync(engine, new { command = "folders.create" });
        var slimRow = Assert.Single(slim.Items);
        Assert.Null(slimRow.ArgsJson);
        Assert.Null(slimRow.ChangesJson);
        Assert.False(slimRow.ArgsTruncated);

        var full = await QueryAsync(engine, new { command = "folders.create", include_payloads = true });
        var fullRow = Assert.Single(full.Items);
        using var argsDoc = JsonDocument.Parse(fullRow.ArgsJson!);
        Assert.Equal("负载开关", argsDoc.RootElement.GetProperty("name").GetString());
        Assert.NotNull(fullRow.ChangesJson);
    }

    [Fact]
    public async Task 查询_参数校验_越界与非法时间明确报错()
    {
        var (engine, _, _) = TestHost.CreateWithAudit();

        var tooSmall = await Assert.ThrowsAsync<EngineException>(() => QueryAsync(engine, new { per_page = 0 }));
        Assert.Equal(EngineErrors.EnumOutOfRange, tooSmall.Error.Code);

        var tooLarge = await Assert.ThrowsAsync<EngineException>(() => QueryAsync(engine, new { per_page = 1001 }));
        Assert.Equal(EngineErrors.EnumOutOfRange, tooLarge.Error.Code);

        var badTime = await Assert.ThrowsAsync<EngineException>(() => QueryAsync(engine, new { from = "昨天" }));
        Assert.Equal(EngineErrors.TypeMismatch, badTime.Error.Code);
    }

    [Fact]
    public async Task 干跑_审计标记dry_run且可按其过滤()
    {
        var (engine, _, _) = TestHost.CreateWithAudit();
        await Run(engine, "folders.create", new { name = "预演目录" },
            new CallOptions(DryRun: true));

        var dry = await QueryAsync(engine, new { dry_run = true });
        var row = Assert.Single(dry.Items);
        Assert.True(row.DryRun);
        Assert.Equal("folders.create", row.Command);

        Assert.Equal(0, (await QueryAsync(engine, new { dry_run = false })).Total);
    }

    [Fact]
    public async Task 嵌套子记录_可按is_nested过滤()
    {
        var (engine, _, _) = TestHost.CreateWithAudit();
        await Run(engine, "links.create", new { url = "https://dup.test/a" });
        await Run(engine, "links.create", new { url = "https://dup.test/a" });
        await Run(engine, "dedup.apply", new { });   // 逐条嵌套 links.trash

        var nested = await QueryAsync(engine, new { is_nested = true });
        Assert.True(nested.Total >= 1, "嵌套派发应产出 IsNested 子记录");
        Assert.All(nested.Items, row => Assert.True(row.IsNested));
        Assert.All(nested.Items, row => Assert.Equal("links.trash", row.Command));

        var top = await QueryAsync(engine, new { command = "dedup.apply" });
        var topRow = Assert.Single(top.Items);
        Assert.False(topRow.IsNested);
    }

    [Fact]
    public async Task 保留_两阶段确认与越界校验与干跑零副作用()
    {
        var (engine, factory, _) = TestHost.CreateWithAudit();
        await Run(engine, "folders.create", new { name = "新行" });

        // 造一条 30 天前的旧行（直接经落表写入器，模拟历史数据）
        new SqlAuditWriter(factory.CreateDbContext).Write(new AuditEntry(
            DateTimeOffset.Now.AddDays(-30), "folders.create", "old-corr", CallerRef.Test,
            ElapsedMs: 1, Success: true, ErrorCode: null, Changes: null,
            DryRun: false, IsNested: false, StackTrace: null, ArgsJson: """{"name":"旧行"}"""));
        Assert.Equal(2, (await QueryAsync(engine)).Total);

        // 破坏性命令：首次调用返回确认令牌（不误删；确认门在写闸之前，失败不落审计）
        var noToken = await Assert.ThrowsAsync<EngineException>(
            () => engine.ExecuteAsync<AuditPruneResult>("audit.prune", new { keep_days = 1 }));
        Assert.Equal(EngineErrors.ConfirmRequired, noToken.Error.Code);
        Assert.Equal(2, (await QueryAsync(engine)).Total);

        // 干跑（跳过确认）：如实报将删行数，零副作用（判据 = 旧行仍在；注意干跑自身会留一条审计痕迹）
        var dry = await engine.ExecuteAsync<AuditPruneResult>("audit.prune", new { keep_days = 1 },
            new CallOptions(DryRun: true));
        Assert.Equal(1, dry.Data!.Deleted);
        Assert.Single((await QueryAsync(engine, new { correlation_id = "old-corr" })).Items);

        // 越界：keep_days 至少 1
        var token = noToken.Error.Details!.Value.GetProperty("confirm_token").GetString();
        var bad = await Assert.ThrowsAsync<EngineException>(
            () => engine.ExecuteAsync<AuditPruneResult>("audit.prune", new { keep_days = 0 },
                new CallOptions(ConfirmToken: token!)));
        Assert.Equal(EngineErrors.EnumOutOfRange, bad.Error.Code);

        // 正式清理（重新取令牌：上一次调用已消费旧令牌——确认门在处理器之前，失败也会消耗）
        var fresh = await Assert.ThrowsAsync<EngineException>(
            () => engine.ExecuteAsync<AuditPruneResult>("audit.prune", new { keep_days = 1 }));
        var token2 = fresh.Error.Details!.Value.GetProperty("confirm_token").GetString();
        var pruned = await engine.ExecuteAsync<AuditPruneResult>("audit.prune", new { keep_days = 1 },
            new CallOptions(ConfirmToken: token2!));
        Assert.Equal(1, pruned.Data!.Deleted);
        Assert.Equal(0, (await QueryAsync(engine, new { correlation_id = "old-corr" })).Total);
        Assert.Equal(1, (await QueryAsync(engine, new { command = "folders.create", success = true })).Total);
    }

    [Fact]
    public async Task 入参快照_落库前即脱敏且结构完好()
    {
        var (engine, _, _) = TestHost.CreateWithAudit();
        // ① 现实泄漏面：链接地址带查询串凭证  ② 值里以敏感键开头（key=value 形态）
        await Run(engine, "links.create", new { url = "https://x.test/page?a=1&token=secret013", title = "带凭证" });
        await Run(engine, "links.create", new { url = "https://safe.test/a", description = "password=hunter2 备注" });

        var rows = await QueryAsync(engine, new { command = "links.create", include_payloads = true });
        Assert.Equal(2, rows.Total);

        var urlRow = rows.Items.Single(r => r.ArgsJson!.Contains("x.test"));
        Assert.DoesNotContain("secret013", urlRow.ArgsJson);
        Assert.Contains("token=***", urlRow.ArgsJson);
        Assert.False(urlRow.ArgsTruncated);

        // 脱敏不得破坏结构：读侧（AI / 脚本）仍能反序列化，非敏感字段原样
        using var doc = JsonDocument.Parse(urlRow.ArgsJson!);
        Assert.Equal("https://x.test/page?a=1&token=***", doc.RootElement.GetProperty("url").GetString());
        Assert.Equal("带凭证", doc.RootElement.GetProperty("title").GetString());

        var noteRow = rows.Items.Single(r => r.ArgsJson!.Contains("safe.test"));
        Assert.DoesNotContain("hunter2", noteRow.ArgsJson);
        Assert.Contains("password=***", noteRow.ArgsJson);
    }
}
