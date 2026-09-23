using System.Runtime.CompilerServices;
using System.Text.Json;
using LinkPocket.Composition;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using Xunit;

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
                [ParamSpec.Opt<JsonElement>("filter", "filter"), ParamSpec.Opt<JsonElement>("page", "page")],
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
            Assert.Equal("array", filter.GetProperty("type").GetString());   // 精选 schema 而非塌陷成 object
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

    // ── 整条回合链路（假传输层 + 真实引擎）────────────────────

    /// <summary>照剧本吐 SSE 行的假传输层（CI 不打真实网络；每个剧本项对应一次模型请求）。</summary>
    private sealed class ScriptedTransport(params string[][] scripts) : IAiHttpTransport
    {
        private int _index;

        public int Requests => _index;

        public Task<AiHttpResponse> SendAsync(AiHttpRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("回合循环只用流式");

        public async IAsyncEnumerable<string> SendStreamLinesAsync(AiHttpRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var script = scripts[Math.Min(_index++, scripts.Length - 1)];
            foreach (var line in script)
            {
                await Task.Yield();
                yield return line;
            }
        }
    }

    private static string TextChunk(string text)
        => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } });

    private static string ToolChunk(int index, string? id, string? name, string? argsJson)
        => "data: " + JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { delta = new { tool_calls = new[] { new { index, id, function = new { name, arguments = argsJson } } } } },
            },
        });

    private sealed record Host(IAiAssistant Assistant, EngineClient Client, ISessionManager Sessions,
        ScriptedTransport Transport, string DataRoot, string DbPath) : IDisposable
    {
        public void Dispose()
        {
            AiTestEnv.Drop(DataRoot);
            TestEnvCleanup(DbPath);
        }
    }

    private static Host NewHost(string[][] scripts)
    {
        var dbPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpai_{Guid.NewGuid():N}.db");
        var dataRoot = AiTestEnv.NewRoot();
        var composed = EngineComposer.Compose(dbPath);
        var transport = new ScriptedTransport(scripts);
        var assistant = AiRuntime.Create(dataRoot, composed.Client, composed.Sessions, transport);
        return new Host(assistant, composed.Client, composed.Sessions, transport, dataRoot, dbPath);
    }

    /// <summary>带预置数据的宿主：先 seed，再用真实 ID 生成模型剧本（模型/测试需要知道实体 ID）。</summary>
    private static async Task<Host> NewSeededHostAsync(Func<EngineClient, Task> seed,
        Func<EngineClient, Task<string[][]>> scripts)
    {
        var dbPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpai_{Guid.NewGuid():N}.db");
        var dataRoot = AiTestEnv.NewRoot();
        var composed = EngineComposer.Compose(dbPath);
        await seed(composed.Client);
        var transport = new ScriptedTransport(await scripts(composed.Client));
        var assistant = AiRuntime.Create(dataRoot, composed.Client, composed.Sessions, transport);
        return new Host(assistant, composed.Client, composed.Sessions, transport, dataRoot, dbPath);
    }

    private static void TestEnvCleanup(string dbPath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }

    private static async Task ConfigureAsync(Host host, AiMode mode)
    {
        await host.Assistant.SaveProviderAsync(new AiProviderDraft("gw", "Gateway", AiProtocol.OpenAiChat,
            "https://gw.example.com/v1", Enabled: true, IsLocal: false));
        await host.Assistant.SetApiKeyAsync("gw", "sk-1234567890abcdef");
        await host.Assistant.SaveModelAsync(new AiModelDraft("gw", "test-model", "Test model", true,
            ContextWindow: 32_000, MaxOutputTokens: 1024, SupportsTools: true, SupportsStreaming: true));
        var preferences = await host.Assistant.GetPreferencesAsync();
        await host.Assistant.SavePreferencesAsync(preferences with { ProviderId = "gw", ModelId = "test-model" });

        var session = await host.Assistant.CreateSessionAsync();
        await host.Assistant.SetModeAsync(session.SessionId, mode);
    }

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
}
