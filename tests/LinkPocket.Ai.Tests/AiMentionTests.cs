using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Ai.Tests;

/// <summary>
/// @提及与跨会话引用（P4-2）：候选检索、稳定 ID 与 canonical 路径注入、`#会话ID` 只作引用标记不展开、
/// `session.read` 本地工具（有界 + untrusted 包裹、只读、不进引擎审计）。
/// </summary>
public sealed class AiMentionTests
{
    [Fact]
    public async Task 提及候选_文件夹与链接按片段过滤()
    {
        using var host = AiTestHost.NewHost([[AiTestHost.TextChunk("ok")]]);
        await AiTestHost.ConfigureAsync(host, AiMode.ReadOnly);
        await host.Client.ExecuteAsync<FolderDto>("folders.create", new { name = "工作资料" });
        await host.Client.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://work.test/x", title = "工作台" });

        var matches = await host.Assistant.SearchMentionsAsync("工作");
        Assert.Equal(2, matches.Count);
        Assert.Equal(AiMentionKind.Folder, matches[0].Kind);          // 文件夹在前（类型序）
        Assert.Equal("工作资料", matches[0].Name);

        var narrowed = await host.Assistant.SearchMentionsAsync("工作台");
        var link = Assert.Single(narrowed);
        Assert.Equal(AiMentionKind.Link, link.Kind);
        Assert.Empty(await host.Assistant.SearchMentionsAsync(""));    // 空查询不检索
    }

    [Fact]
    public async Task 提及_稳定ID与canonical路径进模型上下文_记录随消息持久化()
    {
        using var host = AiTestHost.NewHost([[AiTestHost.TextChunk("ok")]]);
        await AiTestHost.ConfigureAsync(host, AiMode.AutoApply);
        var folder = await host.Client.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" });
        var sessionId = (await host.Assistant.ListSessionsAsync()).Single().SessionId;

        await host.Assistant.SendAsync(sessionId, "@工作 里加一条",
            new AiTurnContext(Mentions: [new AiMentionRef(AiMentionKind.Folder, folder.Data!.FolderId, "工作")]));

        var body = AiTestHost.BodyText(host.Transport.Captured[^1]);
        Assert.Contains("Mentioned objects", body);
        Assert.Contains(folder.Data.FolderId, body);                   // 身份是 ID（重名不歧义）
        Assert.Contains("@root/工作", body);                           // canonical 路径（locate.resolve 解析）

        var file = AiTestHost.ReadSessionFile(host.DataRoot, sessionId);
        var message = file.GetProperty("Messages").EnumerateArray()
            .Last(m => m.GetProperty("Role").GetInt32() == (int)AiRole.User);
        var mention = message.GetProperty("Mentions").EnumerateArray().Single();
        Assert.Equal(folder.Data.FolderId, mention.GetProperty("Id").GetString());
        Assert.Equal("工作", mention.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task 跨会话引用_只作标记不展开_提醒模型用session_read()
    {
        using var host = AiTestHost.NewHost([[AiTestHost.TextChunk("ok")]]);
        await AiTestHost.ConfigureAsync(host, AiMode.AutoApply);
        var other = await host.Assistant.CreateSessionAsync();
        var sessionId = (await host.Assistant.ListSessionsAsync())
            .First(s => s.SessionId != other.SessionId).SessionId;

        await host.Assistant.SendAsync(sessionId, $"看看 #{other.SessionId} 里做了什么");

        var body = AiTestHost.BodyText(host.Transport.Captured[^1]);
        Assert.Contains("Referenced sessions", body);
        Assert.Contains(other.SessionId, body);
        Assert.Contains("NOT automatically expanded", body);
        Assert.Contains("session.read", body);
        Assert.Contains("untrusted", body);
    }

    [Fact]
    public async Task 本地工具_session_read_有界_包不可信标记_不进引擎审计()
    {
        const string targetId = "s-0123456789abcdef0123456789abcdef";   // 会话 ID 形状（s- + 32 hex）
        using var host = AiTestHost.NewHost(
        [
            [
                AiTestHost.ToolChunk(0, "c1", "session.read",
                    JsonSerializer.Serialize(new { session_id = targetId, max_chars = 600 })),
                "data: [DONE]",
            ],
            [AiTestHost.TextChunk("读过了"), "data: [DONE]"],
        ]);
        var sessionId = await AiTestHost.ConfigureAsync(host, AiMode.AutoApply);
        AiTestHost.SeedSessionFile(host.DataRoot, targetId,
            [new AiTestHost.ChatLine("user", "老的用户问题")],
            messages:
            [
                ("老的用户问题", AiRole.User),
                (new string('X', 5_000), AiRole.Assistant),
            ]);

        await host.Assistant.SendAsync(sessionId, "读一下那个会话");

        var detail = await host.Assistant.GetSessionAsync(sessionId);
        var call = Assert.Single(detail.ToolCalls);
        Assert.Equal("session.read", call.Command);
        Assert.Equal(AiToolCallState.Completed, call.State);
        var result = call.ResultJson!;
        Assert.Contains("untrusted-data", result);                      // 不可信包裹
        Assert.Contains(targetId, result);
        Assert.Contains("(cut)", result);                               // 有界截断（超长单条被切）
        Assert.Contains("\"truncated\":true", result);
        Assert.Contains("untrusted-data", AiTestHost.BodyText(host.Transport.Captured[^1]));   // 同一份内容回灌给模型

        // 只读本地文件：引擎审计里没有这条调用（不进第二套审计）
        var audit = await host.Client.QueryAsync<object>("audit.query", new { command = "session.read" });
        var auditJson = JsonSerializer.SerializeToElement(audit,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        Assert.Equal(0, auditJson.GetProperty("total").GetInt32());
    }
}
