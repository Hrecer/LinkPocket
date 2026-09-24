using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Ai.Tests;

/// <summary>
/// 技能库（P4-3）：存储（名称唯一 / 损坏如实暴露 / 占位提取）、绑定宏校验、运行（渲染参数 + 宏绑定行 →
/// 作为用户消息发起回合）、技能清单进系统提示（渐进披露）。
/// </summary>
public sealed class AiSkillTests
{
    private static AiSkillDraft Draft(string name, string template, string? macro = null, string? id = null)
        => new(id, name, "描述：何时用", template, macro);

    [Fact]
    public void 技能存储_保存列表删除_名称重复拒绝()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var store = new AiSkillStore(root);
            var saved = store.Save(Draft("整理", "把 {folder} 里的链接整理一下"));
            Assert.Equal(["folder"], saved.Parameters);
            Assert.Single(store.List());

            // 名称唯一（大小写不敏感）
            var duplicate = Assert.Throws<AiException>(() => store.Save(Draft("整理", "别的模板")));
            Assert.Equal(AiErrors.InvalidInput, duplicate.Error.Code);

            // 更新（同一 SkillId）
            var updated = store.Save(Draft("整理", "把 {folder} 里的链接搬到 {target}", id: saved.SkillId));
            Assert.Single(store.List());
            Assert.Equal(["folder", "target"], updated.Parameters);

            Assert.True(store.Delete(saved.SkillId));
            Assert.Empty(store.List());
        }
        finally
        {
            AiTestEnv.Drop(root);
        }
    }

    [Fact]
    public void 技能存储_损坏如实暴露且拒绝覆盖()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "skills.json"), "{ not json");
            var store = new AiSkillStore(root);
            var error = Assert.Throws<AiException>(() => store.List());
            Assert.Equal(AiErrors.AiDataStoreFailed, error.Error.Code);
            var refused = Assert.Throws<AiException>(() => store.Save(Draft("新技能", "模板")));
            Assert.Equal(AiErrors.AiDataStoreFailed, refused.Error.Code);
        }
        finally
        {
            AiTestEnv.Drop(root);
        }
    }

    [Fact]
    public async Task 技能_绑定宏必须存在_否则如实拒绝()
    {
        using var host = AiTestHost.NewHost([[AiTestHost.TextChunk("ok")]]);
        await AiTestHost.ConfigureAsync(host, AiMode.AutoApply);

        var missing = await Assert.ThrowsAsync<AiException>(() =>
            host.Assistant.SaveSkillAsync(Draft("搬运", "用宏搬运", macro: "no-such-macro")));
        Assert.Equal(AiErrors.InvalidInput, missing.Error.Code);

        await host.Client.ExecuteAsync<object>("macro.save", new
        {
            name = "搬运宏",
            script = AiTestHost.MinimalMacroScript("搬运宏"),
        });
        var saved = await host.Assistant.SaveSkillAsync(Draft("搬运", "用宏搬运", macro: "搬运宏"));
        Assert.Equal("搬运宏", saved.MacroName);
        Assert.Contains("搬运宏", await host.Assistant.ListMacroNamesAsync());
    }

    [Fact]
    public async Task 技能运行_渲染参数与宏绑定行_以用户消息发起回合()
    {
        using var host = AiTestHost.NewHost([[AiTestHost.TextChunk("好的")]]);
        var sessionId = await AiTestHost.ConfigureAsync(host, AiMode.ReadOnly);
        await host.Client.ExecuteAsync<object>("macro.save", new
        {
            name = "重排宏",
            script = AiTestHost.MinimalMacroScript("重排宏"),
        });
        var skill = await host.Assistant.SaveSkillAsync(Draft("重排", "把 {folder} 里的链接按名称重排", macro: "重排宏"));
        host.Transport.Captured.Clear();

        await host.Assistant.RunSkillAsync(sessionId, skill.SkillId,
            new Dictionary<string, string> { ["folder"] = "工作" });

        var file = AiTestHost.ReadSessionFile(host.DataRoot, sessionId);
        var userText = file.GetProperty("Messages").EnumerateArray()
            .Last(m => m.GetProperty("Role").GetInt32() == (int)AiRole.User)
            .GetProperty("Text").GetString()!;
        Assert.Equal("把 工作 里的链接按名称重排\n\nBound macro: `重排宏` - run it with the macro.run tool when the plan is confirmed.",
            userText);
        Assert.Contains("把 工作 里的链接按名称重排", AiTestHost.BodyText(host.Transport.Captured[^1]));
    }

    [Fact]
    public async Task 技能运行_未填占位保持字面()
    {
        using var host = AiTestHost.NewHost([[AiTestHost.TextChunk("好的")]]);
        var sessionId = await AiTestHost.ConfigureAsync(host, AiMode.ReadOnly);
        var skill = await host.Assistant.SaveSkillAsync(Draft("重排", "把 {folder} 里的链接重排"));

        await host.Assistant.RunSkillAsync(sessionId, skill.SkillId);

        var file = AiTestHost.ReadSessionFile(host.DataRoot, sessionId);
        var userText = file.GetProperty("Messages").EnumerateArray()
            .Last(m => m.GetProperty("Role").GetInt32() == (int)AiRole.User)
            .GetProperty("Text").GetString()!;
        Assert.Equal("把 {folder} 里的链接重排", userText);   // 未填=字面（模型看得见，可再问）
    }

    [Fact]
    public async Task 技能清单_进系统提示_渐进披露只给名称与说明()
    {
        using var host = AiTestHost.NewHost([[AiTestHost.TextChunk("好的")]]);
        var sessionId = await AiTestHost.ConfigureAsync(host, AiMode.AutoApply);
        await host.Assistant.SaveSkillAsync(Draft("周报整理", "把上周新增的链接汇总"));

        await host.Assistant.SendAsync(sessionId, "随便聊聊");
        var body = AiTestHost.BodyText(host.Transport.Captured[^1]);
        Assert.Contains("## Skills", body);
        Assert.Contains("周报整理: 描述：何时用", body);
        Assert.Contains("skill.load", body);                    // 正文按需加载（渐进披露）
        Assert.DoesNotContain("把上周新增的链接汇总", body);      // 正文不整篇塞进系统提示
    }
}
