using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;
using static LinkPocket.Ai.Tests.AiTestHost;

namespace LinkPocket.Ai.Tests;

/// <summary>
/// P3+-3 AI 侧变更对账（功能书 §7.1）：白名单边界 → 纯函数判据（兜底 / 校验 / 归一）
/// → 端到端（真实引擎快照往返）。判据三面：①引擎无 diff 必须兜底为 Reconciled；
/// ②引擎有 diff 且一致 → 零不一致标注；③引擎与快照值不同 → 必须标不一致（能红）。
/// </summary>
public class AiReconcileTests
{
    // ── 白名单 ─────────────────────────────────────────────────

    [Theory]
    [InlineData("links.create")]
    [InlineData("links.update")]
    [InlineData("links.trash")]
    [InlineData("links.move_batch")]
    [InlineData("folders.create")]
    [InlineData("folders.update")]
    [InlineData("folders.move")]
    [InlineData("folders.move_batch")]
    [InlineData("folders.delete")]
    [InlineData("folders.copy")]
    [InlineData("trash.restore")]
    [InlineData("trash.restore_unit")]
    [InlineData("trash.restore_batch")]
    public void 对账白名单_十三条写命令_命中(string command) => Assert.True(AiReconciler.IsWhitelisted(command));

    [Theory]
    [InlineData("batch.run")]           // 目标藏在脚本里，快照无从定位
    [InlineData("backup.import")]       // 文件驱动全库
    [InlineData("links.copy_batch")]    // 结果派生多实体，读面不展开
    [InlineData("links.visit_record")]  // 计数不入 diff 字段集
    [InlineData("folders.sort")]        // 同上
    [InlineData("trash.purge")]         // 实体已离开回收站两表
    [InlineData("dedup.apply")]         // 目标由分组结果决定
    [InlineData("links.search")]        // 只读命令零对账开销
    public void 对账白名单_边界外命令_不命中(string command) => Assert.False(AiReconciler.IsWhitelisted(command));

    // ── 纯函数判据（Compute）──────────────────────────────────

    private static readonly JsonElement TitleOld = FieldValue.Str("甲");
    private static readonly JsonElement TitleNew = FieldValue.Str("乙");
    private static readonly JsonElement JsonNull = FieldValue.Str(null);

    private static AiSnapshot Snap(bool present, string title)
        => new(present
            ? new Dictionary<string, IReadOnlyDictionary<string, JsonElement?>>(StringComparer.Ordinal)
            {
                [AiReconciler.Key("link", "L1")] = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                {
                    ["url"] = FieldValue.Str("https://a.test/x"),
                    ["title"] = FieldValue.Str(title),
                    ["description"] = JsonNull,
                    ["folder_id"] = JsonNull,
                    ["is_important"] = FieldValue.Bool(false),
                },
            }
            : new Dictionary<string, IReadOnlyDictionary<string, JsonElement?>>(StringComparer.Ordinal));

    private static ChangeSet Engine(string? touchedId, params FieldChange[] diff)
        => new(
            touchedId is null ? [] : [new EntityRef("link", touchedId)],
            ["links.changed"],
            null,
            null,
            diff.Length > 0 ? diff : null);

    private static readonly string Key = AiReconciler.Key("link", "L1");

    [Fact]
    public void 对账_引擎有diff且与快照一致_零不一致零兜底()
    {
        var engine = Engine("L1", new FieldChange("link", "L1", "title", TitleOld, TitleNew));
        var outcome = AiReconciler.Compute("links.update", Snap(true, "甲"), Snap(true, "乙"), engine);

        Assert.Empty(outcome.Mismatched);
        Assert.Empty(outcome.Reconciled);
        Assert.Empty(outcome.Unreported);
    }

    [Fact]
    public void 对账_引擎报变更但快照观测未变_标不一致_能红()
    {
        // 引擎说 title 甲→乙，快照两侧都是甲 = 校验器必须抓出来
        var engine = Engine("L1", new FieldChange("link", "L1", "title", TitleOld, TitleNew));
        var outcome = AiReconciler.Compute("links.update", Snap(true, "甲"), Snap(true, "甲"), engine);

        Assert.Contains(Key, outcome.Mismatched);
        Assert.Empty(outcome.Reconciled);   // 不一致绝不冒充兜底字段
    }

    [Fact]
    public void 对账_引擎无diff但实体被touch_观测到字段变化_兜底Reconciled()
    {
        // folders.copy 形态：ChangeSet.Of 只有 touched、没有 diff → 对账给字段级答案
        var outcome = AiReconciler.Compute("links.update", Snap(true, "甲"), Snap(true, "乙"),
            Engine("L1"));

        Assert.True(outcome.Reconciled.ContainsKey(Key));
        var field = outcome.Reconciled[Key].Single();
        Assert.Equal("title", field.Field);
        Assert.Equal("甲", field.Before!.Value.GetString());
        Assert.Equal("乙", field.After!.Value.GetString());
        Assert.Empty(outcome.Mismatched);
    }

    [Fact]
    public void 对账_快照观测到变化但引擎未touch_只记Unreported不建账()
    {
        // 台账只记引擎事实：引擎连 touched 都没报 → 只告警，不产兜底字段
        var outcome = AiReconciler.Compute("links.update", Snap(true, "甲"), Snap(true, "乙"),
            Engine(touchedId: null));

        Assert.Contains(Key, outcome.Unreported);
        Assert.Empty(outcome.Reconciled);
        Assert.Empty(outcome.Mismatched);
    }

    [Fact]
    public void 对账_创建型_前缺席后在_全字段新值且不判不一致()
    {
        // restore 形态：执行前实体不在主表（读面缺席），执行后在 → 创建型 before=不适用
        var engine = Engine("L1",
            new FieldChange("link", "L1", "title", null, TitleNew),
            new FieldChange("link", "L1", "url", null, FieldValue.Str("https://a.test/x")),
            new FieldChange("link", "L1", "description", null, JsonNull),
            new FieldChange("link", "L1", "folder_id", null, JsonNull),
            new FieldChange("link", "L1", "is_important", null, FieldValue.Bool(false)));
        var outcome = AiReconciler.Compute("trash.restore", Snap(present: false, "乙"), Snap(true, "乙"), engine);

        Assert.Empty(outcome.Mismatched);
        Assert.Empty(outcome.Reconciled);
    }

    [Fact]
    public void 对账_引擎null与快照空串_读面归一不判不一致()
    {
        // DTO 投影把 null 归一为 ""：引擎报 JSON null、快照读到 "" 是同一读面事实 → 归一相等
        var engine = Engine("L1", new FieldChange("link", "L1", "title", JsonNull, TitleNew));  // 引擎侧 Before = JSON null
        var outcome = AiReconciler.Compute("links.update", Snap(true, ""), Snap(true, "乙"), engine);  // 快照侧 Before = ""

        Assert.Empty(outcome.Mismatched);
    }

    [Fact]
    public void 对账_实体双缺席_无从比对_不产出任何结论()
    {
        // 双缺席 = 快照没有反证 → 引擎 diff 原样保留（不标不一致，也不兜底）
        var outcome = AiReconciler.Compute("links.trash", Snap(false, "甲"), Snap(false, "甲"),
            Engine("L1", new FieldChange("link", "L1", "title", TitleOld, JsonNull)));

        Assert.Empty(outcome.Reconciled);
        Assert.Empty(outcome.Unreported);
        Assert.Empty(outcome.Mismatched);
    }

    [Fact]
    public void 对账_引擎有diff但快照观测到引擎缺的字段变化_标不一致_能红()
    {
        // 引擎只报 url，快照观测到 title 变了 → 引擎漏报 title → 必须抓出来
        var engine = Engine("L1", new FieldChange("link", "L1", "url", FieldValue.Str("https://old.test"), TitleNew));
        var outcome = AiReconciler.Compute("links.update", Snap(true, "甲"), Snap(true, "乙"), engine);

        Assert.Contains(Key, outcome.Mismatched);
    }

    // ── 快照目标定位（白名单入参 → 读面 ID）────────────────────

    [Fact]
    public void 快照目标_创建类取结果ID_更新类取入参ID()
    {
        using var create = JsonDocument.Parse("""{"name":"x"}""");
        Assert.Empty(AiReconciler.Targets("links.create", create.RootElement.Clone(), null));   // 前快照无目标
        using var result = JsonDocument.Parse("""{"id":"L9"}""");
        var after = AiReconciler.Targets("links.create", create.RootElement.Clone(), result.RootElement.Clone());
        Assert.Equal(("link", "L9"), Assert.Single(after));

        using var update = JsonDocument.Parse("""{"id":"L1","title":"y"}""");
        Assert.Equal(("link", "L1"), Assert.Single(AiReconciler.Targets("links.update", update.RootElement.Clone(), null)));
    }

    [Fact]
    public void 快照目标_批量入参_展开为多目标_restore_batch两类型分列()
    {
        using var move = JsonDocument.Parse("""{"link_ids":["L1","L2"]}""");
        Assert.Equal(2, AiReconciler.Targets("links.move_batch", move.RootElement.Clone(), null).Count);

        using var restore = JsonDocument.Parse("""{"link_ids":["L1"],"folder_ids":["F1"]}""");
        var targets = AiReconciler.Targets("trash.restore_batch", restore.RootElement.Clone(), null);
        Assert.Contains(("link", "L1"), targets);
        Assert.Contains(("folder", "F1"), targets);
    }

    [Fact]
    public async Task 快照读失败_降级为对账缺席_不抛出不阻断()
    {
        // 快照是展示增强：读失败（含取消被引擎包装）只降级为"对账缺席"，绝不让回合失败
        using var host = NewHost([]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reconciler = new AiReconciler(host.Client, new CallerRef(CallerKind.Agent, null));
        using var args = JsonDocument.Parse("""{"id":"nope"}""");

        var snapshot = await reconciler.CaptureBeforeAsync("links.update", args.RootElement.Clone(),
            "t-corr", cts.Token);

        Assert.Null(snapshot);   // 降级 = null（对账缺席），而不是抛异常拖垮回合
    }

    // ── 端到端（真实引擎 + 假传输层）────────────────────────────

    [Fact]
    public async Task 对账端到端_引擎无diff的写命令_兜底Reconciled带字段()
    {
        // folders.copy = 引擎 ChangeSet.Of 无 diff（白名单内兜底面）→ 台账必须带字段、Source=Reconciled
        using var host = await NewSeededHostAsync(
            seed: client => client.ExecuteAsync<object>("folders.create", new { name = "对账源" }),
            scripts: async client =>
            {
                var id = (await client.QueryAsync<List<FolderDto>>("folders.find", new { name = "对账源" }))
                    .Single().FolderId;
                return
                [
                    [ToolChunk(0, "call_1", "folders.copy", $$"""{"folder_id":"{{id}}"}"""), "data: [DONE]"],
                    [TextChunk("copied"), "data: [DONE]"],
                ];
            });
        await ConfigureAsync(host, AiMode.AutoApply);   // folders.copy 已放回默认层（2026-09-26 收敛）
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "复制文件夹");

        var change = (await host.Assistant.GetSessionAsync(sessionId)).Changes.Single();
        Assert.Equal(AiChangeSource.Reconciled, change.Source);
        Assert.NotNull(change.Fields);
        Assert.Contains(change.Fields!, f => f.Field == "name");
        Assert.False(change.ReconcileMismatch);   // 引擎无 diff → 不存在"不一致"，是兜底不是冲突
        Assert.True(change.EntityExists);
    }

    [Fact]
    public async Task 对账端到端_引擎有diff的写命令_校验一致零标注()
    {
        // links.update 正常改名：快照与引擎 diff 必须逐值一致 → 零不一致、Source 仍是权威 EngineDiff
        using var host = await NewSeededHostAsync(
            seed: client => client.ExecuteAsync<object>("links.create",
                new { url = "https://rec.test/x", title = "旧标题" }),
            scripts: async client =>
            {
                var id = (await client.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://rec.test/x" }))
                    .Single().LinkId;
                return
                [
                    [ToolChunk(0, "call_1", "links.update", $$"""{"id":"{{id}}","title":"新标题"}"""), "data: [DONE]"],
                    [TextChunk("renamed"), "data: [DONE]"],
                ];
            });
        await ConfigureAsync(host, AiMode.AutoApply);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "改标题");

        var change = (await host.Assistant.GetSessionAsync(sessionId)).Changes.Single();
        Assert.Equal(AiChangeSource.EngineDiff, change.Source);
        Assert.False(change.ReconcileMismatch);   // 校验通过 → 不标注（不一致才标）
        Assert.Contains(change.Fields!, f => f.Field == "title" && f.After?.GetString() == "新标题");
    }

    [Fact]
    public async Task 对账端到端_删除命令_执行前在执行后缺席_校验一致()
    {
        // trash 形态：before 快照在、after 读面缺席 → 删除型 diff 与引擎逐值一致
        using var host = await NewSeededHostAsync(
            seed: client => client.ExecuteAsync<object>("links.create",
                new { url = "https://gone.test/x", title = "待删" }),
            scripts: async client =>
            {
                var id = (await client.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://gone.test/x" }))
                    .Single().LinkId;
                return
                [
                    [ToolChunk(0, "call_1", "links.trash", $$"""{"id":"{{id}}"}"""), "data: [DONE]"],
                    [TextChunk("trashed"), "data: [DONE]"],
                ];
            });
        await ConfigureAsync(host, AiMode.AutoApply);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "删掉这个链接");

        var change = (await host.Assistant.GetSessionAsync(sessionId)).Changes.Single();
        Assert.Equal(AiChangeSource.EngineDiff, change.Source);
        Assert.False(change.ReconcileMismatch);
        Assert.Contains(change.Fields!, f => f.Field == "title" && f.Before?.GetString() == "待删" && f.After is null);
    }
}
