using System.Text.Json;
using System.Text.Json.Nodes;
using LinkPocket.Contracts;
using Xunit;
using static LinkPocket.Ai.Tests.AiTestHost;

namespace LinkPocket.Ai.Tests;

/// <summary>
/// 工具目录 / 权限链 / 整条回合链路（假传输层 + 真实引擎 + 临时库）：
/// 只读拒写、破坏性必审批、审批拒绝如实回灌、写入冻结在回合内生效、台账逐条落地。
/// </summary>
public class AiLoopTests
{
    // ── 工具目录与权限链 ──────────────────────────────────────

    private static AiToolCatalog Catalog()
    {
        var manifest = new EngineManifest(DateTimeOffset.UtcNow,
        [
            new CommandDescriptor("folders.create", "folders", "create folder",
                [ParamSpec.Req<string>("name", "Folder name"), ParamSpec.Opt<string>("parent_id", "Parent ID")],
                CommandCaps.Mutation | CommandCaps.Reversible),
            new CommandDescriptor("links.query", "links", "query links",
                [ParamSpec.Opt<JsonElement>("filter", "filter", schema: ParamSchemas.LinkQueryFilter),
                 ParamSpec.Opt<JsonElement>("page", "page", schema: ParamSchemas.LinkQueryPage)],
                CommandCaps.Query),
            new CommandDescriptor("trash.purge", "trash", "purge",
                [ParamSpec.Req<string>("id", "id")], CommandCaps.Mutation | CommandCaps.Destructive),
            new CommandDescriptor("maintenance.reinit", "maintenance", "reset",
                [], CommandCaps.Mutation | CommandCaps.Destructive),
        ]);
        return new AiToolCatalog(manifest);
    }

    [Fact]
    public void 工具目录_分层暴露_清库永不暴露_永久删除需高级开关()
    {
        var catalog = Catalog();

        Assert.True(catalog.IsExposed("links.query", advancedTools: false));
        Assert.True(catalog.IsExposed("folders.create", advancedTools: false));
        Assert.False(catalog.IsExposed("trash.purge", advancedTools: false));
        Assert.True(catalog.IsExposed("trash.purge", advancedTools: true));
        Assert.False(catalog.IsExposed("maintenance.reinit", advancedTools: true));   // 永不暴露
        Assert.DoesNotContain(catalog.Build(advancedTools: true), t => t.Name == "maintenance.reinit");
        Assert.DoesNotContain(catalog.Build(advancedTools: false), t => t.Name == "trash.purge");
    }

    [Fact]
    public void 工具目录_机械schema与精选schema_必填与描述齐备()
    {
        var catalog = Catalog();
        var tools = catalog.Build(advancedTools: false);

        var create = tools.Single(t => t.Name == "folders.create");
        using (var doc = JsonDocument.Parse(create.ParametersJson))
        {
            Assert.Equal("string", doc.RootElement.GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
            Assert.Equal("name", doc.RootElement.GetProperty("required")[0].GetString());
            Assert.Contains("Returns {ref} fields", create.Description, StringComparison.Ordinal);
        }

        var query = tools.Single(t => t.Name == "links.query");
        using (var doc = JsonDocument.Parse(query.ParametersJson))
        {
            Assert.True(doc.RootElement.GetProperty("properties").TryGetProperty("filter", out var filter));
            Assert.Equal("array", filter.GetProperty("type").GetString());   // 描述符 Schema 片段而非塌陷成 object
        }
    }

    [Fact]
    public void 权限链_七步顺序_只读拒写_破坏性必问_自动应用放行非破坏写()
    {
        var catalog = Catalog();
        var create = catalog.Descriptor("folders.create")!;
        var query = catalog.Descriptor("links.query")!;
        var purge = catalog.Descriptor("trash.purge")!;

        Assert.Equal(AiToolDecision.Deny, AiPermissionChain.Decide(null, AiMode.AutoApply, false));           // ①
        Assert.Equal(AiToolDecision.Deny, AiPermissionChain.Decide(create, AiMode.ReadOnly, false));        // ②
        Assert.Equal(AiToolDecision.Allow, AiPermissionChain.Decide(query, AiMode.ReadOnly, false));        // ②
        Assert.Equal(AiToolDecision.Ask, AiPermissionChain.Decide(purge, AiMode.AutoApply, true));          // ③ 破坏性压过会话允许
        Assert.Equal(AiToolDecision.Ask, AiPermissionChain.Decide(purge, AiMode.ConfirmEach, true));
        Assert.Equal(AiToolDecision.Allow, AiPermissionChain.Decide(create, AiMode.AutoApply, false));      // ⑥
        Assert.Equal(AiToolDecision.Ask, AiPermissionChain.Decide(create, AiMode.ConfirmEach, false));      // ⑦
        Assert.Equal(AiToolDecision.Allow, AiPermissionChain.Decide(create, AiMode.ConfirmEach, true));     // ⑤
    }

    [Fact]
    public void 权限链_每次判定都带显式原因_暴露集压过一切()
    {
        var catalog = Catalog();
        var create = catalog.Descriptor("folders.create")!;
        var query = catalog.Descriptor("links.query")!;
        var purge = catalog.Descriptor("trash.purge")!;

        // ① 暴露集：不在暴露集 = 硬禁止（审批不能把"禁止"变成"允许"）
        AssertReason(AiToolDecision.Deny, "not_exposed",
            AiPermissionChain.Evaluate(create, AiMode.ConfirmEach, false, exposed: false));
        AssertReason(AiToolDecision.Deny, "unknown_tool",
            AiPermissionChain.Evaluate(null, AiMode.AutoApply, false));
        // ② 只读：写拒、读放
        AssertReason(AiToolDecision.Deny, "readonly_write", AiPermissionChain.Evaluate(create, AiMode.ReadOnly, false));
        AssertReason(AiToolDecision.Allow, "readonly_query", AiPermissionChain.Evaluate(query, AiMode.ReadOnly, false));
        // ③ 破坏性：任何模式（含自动应用 + 会话已允许）都要问
        AssertReason(AiToolDecision.Ask, "destructive_needs_approval",
            AiPermissionChain.Evaluate(purge, AiMode.AutoApply, true));
        // ④⑤⑥⑦
        AssertReason(AiToolDecision.Allow, "query", AiPermissionChain.Evaluate(query, AiMode.ConfirmEach, false));
        AssertReason(AiToolDecision.Allow, "session_allowance",
            AiPermissionChain.Evaluate(create, AiMode.ConfirmEach, true));
        AssertReason(AiToolDecision.Allow, "auto_apply", AiPermissionChain.Evaluate(create, AiMode.AutoApply, false));
        AssertReason(AiToolDecision.Ask, "confirm_each", AiPermissionChain.Evaluate(create, AiMode.ConfirmEach, false));

        static void AssertReason(AiToolDecision expected, string reason, AiPermissionChain.AiPermissionVerdict actual)
        {
            Assert.Equal(expected, actual.Decision);
            Assert.Equal(reason, actual.Reason);
        }
    }

    // ── 审批卡的影响面（P3-7）：对象名称/数量/路径 · 批逐步骤 · 暴露集逐条套到批上 ──

    [Fact]
    public async Task 审批_对象的名称数量与canonical路径取自引擎而非模型自述()
    {
        using var host = await NewSeededHostAsync(
            seed: async client =>
            {
                var folder = await client.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
                await client.ExecuteAsync<object>("links.create",
                    new { url = "https://scope.test/x", title = "Rust 圣经", list_id = folder.Data!.FolderId });
            },
            scripts: async client =>
            {
                var id = (await client.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://scope.test/x" }))
                    .Single().LinkId;
                return
                [
                    [ToolChunk(0, "c1", "links.update",
                        $$"""{"id":"{{id}}","title":"Rust 圣经（第 2 版）"}"""), "data: [DONE]"],
                    [TextChunk("renamed"), "data: [DONE]"],
                ];
            });
        await ConfigureAsync(host, AiMode.ConfirmEach);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        AiApproval? seen = null;
        host.Assistant.Notified += notification =>
        {
            if (notification.Kind == AiNotificationKind.ApprovalChanged && notification.Approval?.Decision is null)
            {
                seen = notification.Approval;
                _ = host.Assistant.RespondToApprovalAsync(sessionId, notification.Approval!.ApprovalId,
                    AiApprovalDecision.AllowOnce, null);
            }
        };

        await host.Assistant.SendAsync(sessionId, "改个标题");

        Assert.NotNull(seen);
        Assert.Equal(1, seen!.TargetCount);
        Assert.Equal("Rust 圣经", Assert.Single(seen.TargetNames));          // 名称来自 locate.resolve
        Assert.Equal(0, seen.TargetMore);
        Assert.Equal("@root/工作/Rust 圣经", seen.TargetPath);               // canonical 路径（界面再投影）
        Assert.Equal("links.update · Rust 圣经", seen.AllowScope);           // 会话允许将记住的作用域
        Assert.Null(seen.Steps);                                             // 非批没有逐步骤
        Assert.Null(seen.PreviewSummary);                                    // 非破坏性 = 无引擎影响面
        Assert.Equal(AiApprovalDecision.AllowOnce,
            (await host.Assistant.GetSessionAsync(sessionId)).Approvals.Single().Decision);
    }

    [Fact]
    public async Task 批_每一步都过暴露集闸_混入清库命令的批整批拒绝()
    {
        using var host = NewHost(
        [
            [ToolChunk(0, "c1", "batch.run", BatchArgs(
                ("a", "folders.create", """{"name":"不该出现"}""", null),
                ("b", "maintenance.reinit", "{}", null))), "data: [DONE]"],
            [TextChunk("ok"), "data: [DONE]"],
        ]);
        await ConfigureAsync(host, AiMode.AutoApply);   // 自动应用也压不过暴露集
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "清库");

        var detail = await host.Assistant.GetSessionAsync(sessionId);
        var call = detail.ToolCalls.Single();
        Assert.Equal(AiToolCallState.Rejected, call.State);
        Assert.Empty(detail.Approvals);                                              // 禁止 ≠ 要问：连审批卡都没有
        using var doc = JsonDocument.Parse(call.ResultJson!);
        Assert.Equal("tool_not_allowed", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("step_not_exposed:maintenance.reinit", doc.RootElement.GetProperty("reason").GetString());
        var found = await host.Client.QueryAsync<object>("folders.find", new { name = "不该出现" });
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(found));   // 第一步也没执行
    }

    [Fact]
    public async Task 批_审批卡逐步骤影响_模板不算值_破坏性步骤打标()
    {
        using var host = await NewSeededHostAsync(
            seed: async client =>
                await client.ExecuteAsync<object>("links.create",
                    new { url = "https://step.test/x", title = "待删" }),
            scripts: async client =>
            {
                var id = (await client.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://step.test/x" }))
                    .Single().LinkId;
                return
                [
                    [
                        ToolChunk(0, "c1", "batch.run", BatchArgs(
                            ("a", "folders.create", """{"name":"批夹"}""", null),
                            ("b", "links.query", $$"""{"filter":[{"field":"id","op":"eq","value":"{{id}}"}]}""", null),
                            ("c", "links.trash", """{"id":"{b.items[0].id}"}""", "continue"),
                            ("d", "audit.prune", """{"keep_days":90}""", "abort"))),
                        "data: [DONE]",
                    ],
                    [TextChunk("done"), "data: [DONE]"],
                ];
            });
        await ConfigureAsync(host, AiMode.ConfirmEach, advancedTools: true);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        AiApproval? seen = null;
        host.Assistant.Notified += notification =>
        {
            if (notification.Kind == AiNotificationKind.ApprovalChanged && notification.Approval?.Decision is null)
            {
                seen = notification.Approval;
                _ = host.Assistant.RespondToApprovalAsync(sessionId, notification.Approval!.ApprovalId,
                    AiApprovalDecision.AllowOnce, null);
            }
        };

        await host.Assistant.SendAsync(sessionId, "建个夹，删掉那条重复的");

        Assert.NotNull(seen);
        var steps = seen!.Steps!;
        Assert.Equal(4, steps.Count);
        Assert.Equal("folders.create", steps[0].Command);
        Assert.Equal("批夹", steps[0].TargetName);                       // 字面名称 = 对象
        Assert.Equal(1, steps[0].TargetCount);
        Assert.False(steps[0].IsDestructive);
        Assert.Null(steps[0].OnError);                                   // 没写策略 = 不显示（不编造）
        Assert.Equal("links.query", steps[1].Command);
        Assert.Equal(0, steps[1].TargetCount);                           // 过滤器不是对象，不冒领数量
        Assert.Equal("links.trash", steps[2].Command);
        Assert.Null(steps[2].TargetName);                                // {ref} 是占位符不是值
        Assert.Equal(0, steps[2].TargetCount);                           // 展开后才知道动几个 → 如实报"未指明"
        Assert.Equal("continue", steps[2].OnError);                      // 错误策略按脚本原样带出
        Assert.Equal("audit.prune", steps[3].Command);
        Assert.True(steps[3].IsDestructive);                             // 破坏性步骤打标
        Assert.Equal("abort", steps[3].OnError);

        // 批准后真的执行了：夹建出来、那条链接进回收站（批内的步骤逐条落到引擎）
        var detail = await host.Assistant.GetSessionAsync(sessionId);
        var run = detail.ToolCalls.Single();
        Assert.True(run.State == AiToolCallState.Completed,
            $"state={run.State} error={run.ErrorCode} result={run.ResultJson}");
        Assert.Single(await host.Client.QueryAsync<List<FolderDto>>("folders.find", new { name = "批夹" }));
        Assert.Empty(await host.Client.QueryAsync<List<LinkDto>>("links.find_by_url",
            new { url = "https://step.test/x" }));
    }

    [Fact]
    public async Task 破坏性_先探测影响面_批准前零副作用_批准后才真删()
    {
        using var host = await NewSeededHostAsync(
            seed: async client =>
            {
                var link = await client.ExecuteAsync<LinkDto>("links.create", new { url = "https://gone.test/x", title = "待清" });
                await client.ExecuteAsync<object>("links.trash", new { id = link.Data!.LinkId });
            },
            scripts: async client =>
            {
                var trashed = (await client.TrashListAsync()).Single().Id;
                return
                [
                    [ToolChunk(0, "c1", "trash.purge",
                        $$"""{"id":"{{trashed}}","is_folder":false}"""), "data: [DONE]"],
                    [TextChunk("purged"), "data: [DONE]"],
                ];
            });
        await ConfigureAsync(host, AiMode.ConfirmEach, advancedTools: true);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        AiApproval? seen = null;
        var stillThereBeforeApproval = false;
        host.Assistant.Notified += notification =>
        {
            if (notification.Kind != AiNotificationKind.ApprovalChanged || notification.Approval?.Decision is not null)
                return;
            seen = notification.Approval;
            // 探测只是"要令牌"的校验类错误：批准之前那条快照必须还躺在回收站里（Task.Run 避开测试同步上下文）
            stillThereBeforeApproval = Task.Run(() => host.Client.TrashListAsync()).GetAwaiter().GetResult().Count == 1;
            _ = host.Assistant.RespondToApprovalAsync(sessionId, notification.Approval!.ApprovalId,
                AiApprovalDecision.AllowOnce, null);
        };

        await host.Assistant.SendAsync(sessionId, "彻底删掉那条");

        Assert.NotNull(seen);
        Assert.True(seen!.IsDestructive);
        Assert.Equal(EngineErrors.ConfirmRequired, seen.ErrorCodeWhenWaiting);   // 影响面由引擎签发
        Assert.False(string.IsNullOrWhiteSpace(seen.PreviewSummary));             // 引擎给的影响面（不空转）
        Assert.True(stillThereBeforeApproval, "批准前必须零副作用（探测 = 校验类错误）");
        Assert.Equal(1, seen.TargetCount);                                        // 对象数认得出来（快照 ID 解析不到名称，如实只报数量）

        var detail = await host.Assistant.GetSessionAsync(sessionId);
        var purged = detail.ToolCalls.Single();
        Assert.True(purged.State == AiToolCallState.Completed,
            $"state={purged.State} error={purged.ErrorCode} result={purged.ResultJson}");  // 批准后带令牌重发，真删了
        Assert.Empty(await host.Client.TrashListAsync());
    }

    // ── 撤销本会话 / 可撤销批次计数 / 审计时间范围（P3-8）──────────────────

    [Fact]
    public async Task 撤销本会话_按批次分组逐批退_跨回合也退_撤完归零()
    {
        using var host = NewHost(
        [
            [TextChunk("creating"), ToolChunk(0, "c1", "folders.create", """{"name":"会话夹一"}"""), "data: [DONE]"],
            [TextChunk("ok"), "data: [DONE]"],
            [TextChunk("creating"), ToolChunk(0, "c2", "folders.create", """{"name":"会话夹二"}"""), "data: [DONE]"],
            [TextChunk("ok"), "data: [DONE]"],
        ]);
        await ConfigureAsync(host, AiMode.AutoApply);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "建夹一");
        await host.Assistant.SendAsync(sessionId, "建夹二");

        Assert.Equal(2, await host.Assistant.CountUndoableAsync(sessionId));   // 两个回合 = 两个可撤销批次

        var result = await host.Assistant.UndoSessionAsync(sessionId);

        // 逐批定点撤（一个归属键 = 撤销栈里的一条记录）：新者先撤、两条都成
        Assert.Equal(2, result.TotalCalls);
        Assert.Equal(2, result.UndoneCalls);
        Assert.Equal(0, result.MissingCalls);
        Assert.Null(result.ErrorCode);

        // 撤销"新建" = 软删进回收站（原 ID 保留可还原）
        Assert.Empty(await host.Client.QueryAsync<List<FolderDto>>("folders.find", new { name = "会话夹一" }));
        Assert.Empty(await host.Client.QueryAsync<List<FolderDto>>("folders.find", new { name = "会话夹二" }));
        Assert.Equal(0, await host.Assistant.CountUndoableAsync(sessionId));   // 撤完就没得再给按钮了
    }

    [Fact]
    public async Task 可撤销批次计数_只数引擎真登记过的_没登记的变更不给按钮()
    {
        using var host = await NewSeededHostAsync(
            seed: async client =>
                await client.ExecuteAsync<object>("links.create", new { url = "https://rename.test/x", title = "甲" }),
            scripts: async client =>
            {
                var id = (await client.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://rename.test/x" }))
                    .Single().LinkId;
                return
                [
                    [ToolChunk(0, "c1", "links.update", $$"""{"id":"{{id}}","title":"改名后的甲"}"""), "data: [DONE]"],
                    [TextChunk("ok"), "data: [DONE]"],
                ];
            });
        await ConfigureAsync(host, AiMode.AutoApply);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "把那条改个名");

        // 台账里有变更，但改名不入引擎撤销栈 → 一个可撤销批次都没有（不给会失败的按钮）
        Assert.Single((await host.Assistant.GetSessionAsync(sessionId)).Changes);
        Assert.Equal(0, await host.Assistant.CountUndoableAsync(sessionId));

        var result = await host.Assistant.UndoSessionAsync(sessionId);
        Assert.Equal(0, result.TotalCalls);
        Assert.Equal(0, result.UndoneCalls);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task 审计时间范围过滤_服务端from把区间外的行挡掉()
    {
        using var host = NewHost(
        [
            [ToolChunk(0, "c1", "folders.create", """{"name":"范围夹"}"""), "data: [DONE]"],
            [TextChunk("ok"), "data: [DONE]"],
        ]);
        await ConfigureAsync(host, AiMode.AutoApply);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;
        await host.Assistant.SendAsync(sessionId, "建个夹");

        var all = await host.Assistant.QueryEngineAuditAsync(new AiAuditQuery(sessionId));
        Assert.NotEmpty(all.Items);

        // 下界推到未来 = 一行都不该进来（服务端过滤：分页与总数一起按它算）
        var none = await host.Assistant.QueryEngineAuditAsync(
            new AiAuditQuery(sessionId, From: DateTimeOffset.Now.AddHours(1)));
        Assert.Equal(0, none.Total);
        Assert.Empty(none.Items);

        var recent = await host.Assistant.QueryEngineAuditAsync(
            new AiAuditQuery(sessionId, From: DateTimeOffset.Now.AddHours(-1)));
        Assert.Equal(all.Total, recent.Total);
    }

    /// <summary>拼一个 <c>batch.run</c> 入参（步骤按给定的 ref/命令/args[/on_error] 顺序；
    /// JSON 由序列化器产出，不手拼花括号）。</summary>
    private static string BatchArgs(params (string Ref, string Command, string Args, string? OnError)[] steps)
    {
        var array = new JsonArray();
        foreach (var step in steps)
        {
            var node = new JsonObject
            {
                ["ref"] = step.Ref,
                ["command"] = step.Command,
                ["args"] = JsonNode.Parse(step.Args),
            };
            if (!string.IsNullOrEmpty(step.OnError)) node["on_error"] = step.OnError;
            array.Add(node);
        }
        return JsonSerializer.Serialize(new { script = new JsonObject { ["scope"] = "transactional", ["steps"] = array } });
    }

    // ── 台账的粒度来源 / 名称路径解析 / 可撤销性（P2）────────────

    [Fact]
    public async Task 台账_引擎字段级diff_按实体分组_带名称与容器路径()
    {
        using var host = NewHost(
        [
            [
                TextChunk("work"), ToolChunk(0, "call_1", "folders.create", """{"name":"工作"}"""),
                ToolChunk(1, "call_2", "links.create", """{"url":"https://a.test/x","title":"Rust 圣经","description":"中文教程"}"""),
                "data: [DONE]",
            ],
            [TextChunk("done"), "data: [DONE]"],
        ]);
        await ConfigureAsync(host, AiMode.AutoApply);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "建夹并加书签");

        var detail = await host.Assistant.GetSessionAsync(sessionId);
        Assert.Equal(2, detail.Changes.Count);

        var folder = detail.Changes.Single(c => c.EntityType == "folder");
        Assert.Equal(AiChangeKind.Create, folder.Kind);
        Assert.Equal(AiChangeSource.EngineDiff, folder.Source);
        Assert.Equal("工作", folder.EntityName);
        Assert.Equal("@root", folder.EntityPath);                              // 位置 = 容器目录
        Assert.Contains(folder.Fields!, f => f.Field == "name" && f.After?.GetString() == "工作");
        Assert.True(folder.EntityExists);
        Assert.True(folder.Undoable);                                         // 引擎撤销栈里确实登记了（单命令可逆变更）

        var link = detail.Changes.Single(c => c.EntityType == "link");
        Assert.Equal(AiChangeKind.Create, link.Kind);
        Assert.Equal(AiChangeSource.EngineDiff, link.Source);
        Assert.Equal("Rust 圣经", link.EntityName);                            // 名称取自 diff/查询结果（title 优先）
        Assert.Equal("@root", link.EntityPath);
        Assert.Contains(link.Fields!, f => f.Field == "url" && f.After?.GetString() == "https://a.test/x");
    }

    [Fact]
    public async Task 台账_没有字段差异的实体_降级为仅实体级()
    {
        using var host = await NewSeededHostAsync(
            seed: client => client.ExecuteAsync<object>("links.create", new { url = "https://same.test/x", title = "甲" }),
            scripts: async client =>
            {
                var id = (await client.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://same.test/x" }))
                    .Single().LinkId;
                // 同值改名 = 零字段变化（touched 里仍有该实体）→ 台账如实降级为"仅实体级"
                return
                [
                    [ToolChunk(0, "call_1", "links.update", $$"""{"id":"{{id}}","title":"甲"}"""), "data: [DONE]"],
                    [TextChunk("noop"), "data: [DONE]"],
                ];
            });
        await ConfigureAsync(host, AiMode.AutoApply);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "改成同名");

        var change = (await host.Assistant.GetSessionAsync(sessionId)).Changes.Single();
        Assert.Equal(AiChangeSource.EntityOnly, change.Source);
        Assert.Null(change.Fields);
        Assert.Equal("甲", change.EntityName);                                 // 名称仍解析（实体还在主表）
        Assert.False(change.Undoable);                                         // 改名不入撤销栈 → 不给会失败的撤销按钮
    }

    [Fact]
    public async Task 台账_可撤销性以引擎撤销栈为准_移动带旧归属()
    {
        using var host = await NewSeededHostAsync(
            seed: async client =>
            {
                var folder = await client.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
                await client.ExecuteAsync<object>("links.create",
                    new { url = "https://move.test/x", title = "乙", list_id = folder.Data!.FolderId });
            },
            scripts: async client =>
            {
                var link = (await client.QueryAsync<List<LinkDto>>("links.find_by_url", new { url = "https://move.test/x" }))
                    .Single();
                // 移出目录 → 归属变化 = 可逆变更（引擎撤销栈登记 + diff 带旧归属）
                return
                [
                    [ToolChunk(0, "call_1", "links.move_batch", $$"""{"link_ids":["{{link.LinkId}}"]}"""), "data: [DONE]"],
                    [TextChunk("moved"), "data: [DONE]"],
                ];
            });
        await ConfigureAsync(host, AiMode.AutoApply);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "移到根");

        var change = (await host.Assistant.GetSessionAsync(sessionId)).Changes.Single();
        Assert.Equal(AiChangeKind.Move, change.Kind);
        Assert.Equal(AiChangeSource.EngineDiff, change.Source);
        Assert.True(change.Undoable);
        Assert.Equal("@root", change.EntityPath);                              // 移动后位置 = 根
        var location = Assert.Single(change.Fields!, f => f.Field == "folder_id");
        Assert.Equal("工作", (await host.Client.QueryAsync<List<FolderDto>>("folders.find", new { name = "工作" }))
            .Single().Name);                                                   // 旧归属目录可解析（台账按当时事实留痕）
        // 归属从"工作"变为根级：Before 有值、After 为空（会话文件往返后"空值/不适用"统一为 null）
        Assert.NotNull(location.Before);
        Assert.Null(location.After);
    }

    [Fact]
    public async Task 引擎审计_按回合关联取齐本回合的调用史()
    {
        using var host = NewHost(
        [
            [ToolChunk(0, "call_1", "folders.create", """{"name":"审计夹"}"""), "data: [DONE]"],
            [TextChunk("done"), "data: [DONE]"],
        ]);
        await ConfigureAsync(host, AiMode.AutoApply);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "建夹");

        var detail = await host.Assistant.GetSessionAsync(sessionId);
        var turnId = detail.Turns.Single().TurnId;

        var page = await host.Assistant.QueryEngineAuditAsync(new AiAuditQuery(sessionId, turnId, IncludePayloads: true));
        var row = Assert.Single(page.Items);
        Assert.Equal("folders.create", row.Command);
        Assert.Equal($"ai:{turnId}", row.CorrelationId);
        Assert.True(row.Success);
        Assert.False(row.IsNested);
        Assert.Contains("审计夹", row.ArgsJson);                 // 载荷自描述（脱敏后的入参快照）
        Assert.Contains("name", row.ChangesJson);                // 变更载荷里带字段级 diff
        Assert.DoesNotContain("sk-", row.ArgsJson);              // 密钥绝不进审计（兜底断言）

        // 工具调用本身也带上同一条关联（界面「关联」列的取值来源）
        Assert.Equal($"ai:{turnId}", detail.ToolCalls.Single().CorrelationId);
    }

    [Fact]
    public void 工具目录_描述符元数据schema_过滤白名单含id的eq与in()
    {
        // P3-6：精选 schema 已进描述符（ParamSchemas 单一事实源），模型面从 ParamSpec.Schema 透出
        var query = Catalog().Build(advancedTools: false).Single(t => t.Name == "links.query");
        using var doc = JsonDocument.Parse(query.ParametersJson);
        var description = doc.RootElement.GetProperty("properties").GetProperty("filter").GetProperty("description").GetString();
        Assert.Contains("id(eq,in)", description, StringComparison.Ordinal);
        Assert.Equal("array", doc.RootElement.GetProperty("properties").GetProperty("filter").GetProperty("type").GetString());
    }

    [Fact]
    public async Task 引擎审计_本会话范围按页切分_搜索过滤后页数与总数如实()
    {
        using var host = NewHost(
        [
            [
                TextChunk("go"),
                ToolChunk(0, "c1", "folders.create", """{"name":"夹一"}"""),
                ToolChunk(1, "c2", "folders.create", """{"name":"夹二"}"""),
                ToolChunk(2, "c3", "folders.create", """{"name":"夹三"}"""),
                "data: [DONE]",
            ],
            [TextChunk("done"), "data: [DONE]"],
        ]);
        await ConfigureAsync(host, AiMode.AutoApply);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "建三个夹");

        var page1 = await host.Assistant.QueryEngineAuditAsync(new AiAuditQuery(sessionId, PerPage: 2, Page: 1));
        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.PageCount);
        Assert.Equal(2, page1.Items.Count);

        var page2 = await host.Assistant.QueryEngineAuditAsync(new AiAuditQuery(sessionId, PerPage: 2, Page: 2));
        Assert.Equal(2, page2.Page);
        Assert.Single(page2.Items);
        Assert.Equal(3, page1.Items.Concat(page2.Items).Distinct().Count());   // 两页合起来 = 全集、无重叠

        var filtered = await host.Assistant.QueryEngineAuditAsync(
            new AiAuditQuery(sessionId, Search: "folders.create", PerPage: 2, Page: 1));
        Assert.Equal(3, filtered.Total);
        Assert.Equal(2, filtered.PageCount);

        var none = await host.Assistant.QueryEngineAuditAsync(new AiAuditQuery(sessionId, Search: "links."));
        Assert.Equal(0, none.Total);
    }

    [Fact]
    public async Task 撤销上一轮_按归属键定点撤销_再撤一次如实报无可撤销()
    {
        using var host = NewHost(
        [
            [
                TextChunk("go"),
                ToolChunk(0, "c1", "folders.create", """{"name":"撤销一"}"""),
                ToolChunk(1, "c2", "folders.create", """{"name":"撤销二"}"""),
                "data: [DONE]",
            ],
            [TextChunk("done"), "data: [DONE]"],
        ]);
        await ConfigureAsync(host, AiMode.AutoApply);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "建两个夹");

        var result = await host.Assistant.UndoLastTurnAsync(sessionId);
        Assert.Equal(2, result.TotalCalls);
        Assert.Equal(2, result.UndoneCalls);
        Assert.Equal(0, result.MissingCalls);
        Assert.Null(result.ErrorCode);

        // 创建类的撤销 = 软删（进回收站）：主表不再有这两个夹（回收站里保留原 ID 可还原）
        var left = await host.Client.QueryAsync<object>("folders.find", new { name = "撤销一" });
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(left));

        var again = await host.Assistant.UndoLastTurnAsync(sessionId);
        Assert.Equal(0, again.TotalCalls);          // 撤销栈里已无归属记录 = 无可撤销，绝不空转
        Assert.Equal(0, again.UndoneCalls);
    }

    [Fact]
    public async Task 模型能力_显式越界如实报错_合法值往返保留()
    {
        using var host = NewHost([]);
        await ConfigureAsync(host, AiMode.ConfirmEach);

        // 显式 0 = 越界（null 才是"未声明"）：拿不准就报错，不猜意图
        var ex = await Assert.ThrowsAsync<AiException>(() => host.Assistant.SaveModelAsync(
            new AiModelDraft("gw", "test-model", "Test model", true,
                ContextWindow: 0, MaxOutputTokens: 1024, SupportsTools: true, SupportsStreaming: true)));
        Assert.Equal(AiErrors.AiDataStoreFailed, ex.Error.Code);

        await host.Assistant.SaveModelAsync(new AiModelDraft("gw", "test-model", "Test model", true,
            ContextWindow: 64_000, MaxOutputTokens: 4_096, SupportsTools: false, SupportsStreaming: true));
        var model = (await host.Assistant.ListProvidersAsync()).Single(p => p.Id == "gw").Models.Single();
        Assert.Equal(64_000, model.ContextWindow);
        Assert.Equal(4_096, model.MaxOutputTokens);
        Assert.False(model.SupportsTools);
        Assert.True(model.SupportsStreaming);
    }

    // ── 整条回合链路（假传输层 + 真实引擎）：搭建见 AiTestHost ─────────────

    [Fact]
    public async Task 回合_自动应用模式下执行写工具_台账逐条落地_且冻结在回合内生效()
    {
        using var host = NewHost(
        [
            [TextChunk("creating"), ToolChunk(0, "call_1", "folders.create", """{"name":"AI 夹"}"""), "data: [DONE]"],
            [TextChunk("done"), "data: [DONE]"],
        ]);
        await ConfigureAsync(host, AiMode.AutoApply);
        var sessions = await host.Assistant.ListSessionsAsync();
        var sessionId = sessions.Single().SessionId;
        var frozenDuringTurn = false;
        host.Assistant.Notified += notification =>
        {
            if (notification.Kind == AiNotificationKind.ToolCallChanged
                && notification.ToolCall?.State == AiToolCallState.Running)
                frozenDuringTurn = host.Sessions.CurrentWriteHold is not null;
        };

        await host.Assistant.SendAsync(sessionId, "建一个夹");

        // 库里真的有这个文件夹
        var found = await host.Client.QueryAsync<object>("folders.find", new { name = "AI 夹" });
        Assert.NotEmpty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(found));

        var detail = await host.Assistant.GetSessionAsync(sessionId);
        Assert.Equal(AiTurnState.Completed, detail.Turns.Single().State);
        var call = detail.ToolCalls.Single();
        Assert.Equal(AiToolCallState.Completed, call.State);
        var change = detail.Changes.Single();
        Assert.Equal(AiChangeKind.Create, change.Kind);
        Assert.Equal("folder", change.EntityType);

        Assert.True(frozenDuringTurn, "写工具执行期间必须持有写锁");
        Assert.Null(host.Sessions.CurrentWriteHold);   // 回合结束即释放
        Assert.False(host.Assistant.IsWriteFrozen);
        Assert.Equal(2, host.Transport.Requests);
    }

    [Fact]
    public async Task 回合_只读模式下写工具被拒_引擎侧零变更()
    {
        using var host = NewHost(
        [
            [ToolChunk(0, "call_1", "folders.create", """{"name":"不该出现"}"""), "data: [DONE]"],
            [TextChunk("understood"), "data: [DONE]"],
        ]);
        await ConfigureAsync(host, AiMode.ReadOnly);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "建一个夹");

        var found = await host.Client.QueryAsync<object>("folders.find", new { name = "不该出现" });
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(found));
        var detail = await host.Assistant.GetSessionAsync(sessionId);
        Assert.Equal(AiToolCallState.Rejected, detail.ToolCalls.Single().State);
        Assert.Empty(detail.Changes);
        Assert.Null(host.Sessions.CurrentWriteHold);
    }

    [Fact]
    public async Task 回合_每次确认模式下拒绝_工具调用标记为被拒且理由回灌模型()
    {
        using var host = NewHost(
        [
            [ToolChunk(0, "call_1", "folders.create", """{"name":"别建"}"""), "data: [DONE]"],
            [TextChunk("ok, I will not"), "data: [DONE]"],
        ]);
        await ConfigureAsync(host, AiMode.ConfirmEach);
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        AiApproval? seen = null;
        host.Assistant.Notified += notification =>
        {
            if (notification.Kind == AiNotificationKind.ApprovalChanged && notification.Approval?.Decision is null)
            {
                seen = notification.Approval;
                _ = host.Assistant.RespondToApprovalAsync(sessionId, notification.Approval!.ApprovalId,
                    AiApprovalDecision.Reject, "不要动我的书签");
            }
        };

        await host.Assistant.SendAsync(sessionId, "建一个夹");

        Assert.NotNull(seen);
        Assert.Equal("folders.create", seen!.Command);
        var detail = await host.Assistant.GetSessionAsync(sessionId);
        Assert.Equal(AiToolCallState.Rejected, detail.ToolCalls.Single().State);
        Assert.Equal(AiApprovalDecision.Reject, detail.Approvals.Single().Decision);
        Assert.Equal("不要动我的书签", detail.Approvals.Single().Reason);
        Assert.Empty(detail.Changes);
        var found = await host.Client.QueryAsync<object>("folders.find", new { name = "别建" });
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(found));
        using var doc = JsonDocument.Parse(detail.ToolCalls.Single().ResultJson!);
        Assert.Equal("rejected_by_user", doc.RootElement.GetProperty("error").GetString());
    }

    // ── 草稿新建：连点不产空会话 / 首发提升 / 未用草稿可回收 ─────────

    [Fact]
    public async Task 新建会话_是草稿_不进列表不落盘()
    {
        using var host = NewHost([[TextChunk("ok"), "data: [DONE]"]]);
        await ConfigureHostOnlyAsync(host, AiMode.AutoApply);

        var draft = await host.Assistant.CreateSessionAsync();

        Assert.Equal(AiSessionPersistence.Deferred, draft.Persistence);
        Assert.Empty(await host.Assistant.ListSessionsAsync());     // 草稿不进列表
        Assert.False(File.Exists(Path.Combine(host.DataRoot, "sessions", draft.SessionId + ".json")));
        // 连点多次：每次都是新的草稿 id（草稿各自独立），但**都不进列表**——列表始终为空。
        for (var i = 0; i < 5; i++) await host.Assistant.CreateSessionAsync();
        Assert.Empty(await host.Assistant.ListSessionsAsync());
    }

    [Fact]
    public async Task 草稿_首发提升为正式会话_落盘并进列表()
    {
        using var host = NewHost([[TextChunk("答"), "data: [DONE]"]]);
        await ConfigureHostOnlyAsync(host, AiMode.AutoApply);
        var draft = await host.Assistant.CreateSessionAsync();
        var path = Path.Combine(host.DataRoot, "sessions", draft.SessionId + ".json");
        Assert.False(File.Exists(path));                            // 提升前不落盘

        await host.Assistant.SendAsync(draft.SessionId, "你好");

        var listed = Assert.Single(await host.Assistant.ListSessionsAsync());
        Assert.Equal(draft.SessionId, listed.SessionId);
        Assert.Equal(AiSessionPersistence.Immediate, listed.Persistence);
        Assert.True(File.Exists(path));                            // 提升即落盘
        Assert.Equal("你好", listed.Title);                        // 首发顺带定标题
    }

    [Fact]
    public async Task 未提升草稿_可丢弃_正式会话不受影响()
    {
        using var host = NewHost([[TextChunk("答"), "data: [DONE]"]]);
        await ConfigureHostOnlyAsync(host, AiMode.AutoApply);
        var draft = await host.Assistant.CreateSessionAsync();

        await host.Assistant.DiscardDraftSessionAsync(draft.SessionId);

        // 丢弃后连会话都不存在了（内存草稿已删）：再发消息会如实报"会话不存在"，而不是静默复活。
        await Assert.ThrowsAsync<AiException>(() => host.Assistant.SendAsync(draft.SessionId, "喂"));

        // 对一条已提升的正式会话调丢弃 = 幂等 no-op（绝不误删真会话）。
        var real = await host.Assistant.CreateSessionAsync();
        await host.Assistant.SendAsync(real.SessionId, "真的在聊");
        await host.Assistant.DiscardDraftSessionAsync(real.SessionId);
        Assert.Single(await host.Assistant.ListSessionsAsync());
    }

    /// <summary>只配好服务商 / 模型 / 偏好（**不建会话**），供草稿用例自己控制会话生命周期。</summary>
    private static async Task ConfigureHostOnlyAsync(Host host, AiMode mode)
    {
        await host.Assistant.SaveProviderAsync(new AiProviderDraft("gw", "Gateway",
            AiProtocol.OpenAiChat, "https://gw.example.com/v1", Enabled: true, IsLocal: false));
        await host.Assistant.SetApiKeyAsync("gw", "sk-1234567890abcdef");
        await host.Assistant.SaveModelAsync(new AiModelDraft("gw", "test-model", "Test model", true,
            ContextWindow: 32_000, MaxOutputTokens: 1024, SupportsTools: true, SupportsStreaming: true));
        var preferences = await host.Assistant.GetPreferencesAsync();
        await host.Assistant.SavePreferencesAsync(preferences with
        {
            ProviderId = "gw",
            ModelId = "test-model",
        });
    }
}
