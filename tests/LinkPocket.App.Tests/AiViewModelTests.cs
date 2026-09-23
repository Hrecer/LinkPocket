using LinkPocket.Contracts;
using LinkPocket.UI.Ai;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// AI 页 VM 金标准：斜杠命令（本地解析、不走模型）与引擎审计分页。
/// 断言口径 = 可观测结果（提示条文案键 / 页签投影 / 桩记录的调用），不测实现细节。
/// </summary>
public class AiViewModelTests
{
    private static (AiViewModel Vm, StubAiAssistant Stub) NewVm()
    {
        var stub = new StubAiAssistant
        {
            Selection = new AiSelectionResolution(new AiModelSelection("gw", "m1"), null, "gw", "m1"),
        };
        stub.Sessions.Add(new AiSessionSummary("s-1", "会话", AiMode.ConfirmEach, "gw", "m1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, null));
        return (new AiViewModel(stub), stub);
    }

    private static string? LastNoticeKey(AiViewModel vm)
        => vm.Feed.LastOrDefault(i => i.Kind == AiFeedItem.ItemKind.Notice)?.NoticeValue.Key;

    [Fact]
    public async Task 斜杠命令_help_提示条入流_不发给模型()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        vm.ComposerText = "/help";

        await vm.SendAsync();

        Assert.Empty(stub.SendCalls);
        Assert.Equal("ai.slash.help", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 斜杠命令_未知命令_如实提示命令名()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        vm.ComposerText = "/不存在";

        await vm.SendAsync();

        Assert.Empty(stub.SendCalls);
        Assert.Equal("ai.slash.unknown", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 斜杠命令_mode带参数_切模式并提示()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        vm.ComposerText = "/mode auto";

        await vm.SendAsync();

        Assert.Equal(AiMode.AutoApply, vm.Mode);
        Assert.Equal(AiMode.AutoApply, Assert.Single(stub.SetModeCalls).Mode);
        Assert.Equal("ai.slash.modeSet.autoApply", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 斜杠命令_mode带非法参数_给用法提示不切换()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        vm.ComposerText = "/mode 乱写";

        await vm.SendAsync();

        Assert.Empty(stub.SetModeCalls);
        Assert.Equal("ai.slash.modeUsage", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 斜杠命令_audit_切到引擎审计页签()
    {
        var (vm, _) = NewVm();
        await vm.LoadAsync();
        vm.ComposerText = "/audit";

        await vm.SendAsync();

        Assert.True(vm.IsEngineTab);
    }

    [Fact]
    public async Task 斜杠命令_model_无启用模型_如实提示()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        stub.Providers.Add(new AiProviderInfo("gw", "gw", AiProtocol.OpenAiChat, "https://gw.test/v1",
            AiProviderSource.Custom, false, true, true, "sk-1234", AiProviderStatus.Verified, null, null, null,
            [new AiModelInfo("m1", "m1", AiModelSource.Manual, Enabled: false, null, null, true, true)]));
        vm.ComposerText = "/model";

        await vm.SendAsync();

        Assert.Empty(stub.SavePreferencesCalls);
        Assert.Equal("ai.slash.modelNone", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 斜杠命令_model带模型名_写入偏好并提示()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        stub.Providers.Add(new AiProviderInfo("gw", "gw", AiProtocol.OpenAiChat, "https://gw.test/v1",
            AiProviderSource.Custom, false, true, true, "sk-1234", AiProviderStatus.Verified, null, null, null,
            [new AiModelInfo("m1", "m1", AiModelSource.Manual, Enabled: true, null, null, true, true),
             new AiModelInfo("m2", "m2", AiModelSource.Manual, Enabled: true, null, null, true, true)]));
        vm.ComposerText = "/model m2";

        await vm.SendAsync();

        var saved = Assert.Single(stub.SavePreferencesCalls);
        Assert.Equal("m2", saved.ModelId);
        Assert.Equal("ai.slash.modelSet", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 斜杠命令_undo_按结果如实提示()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        stub.UndoResult = new AiUndoResult(2, 2, 0, null);
        vm.ComposerText = "/undo";

        await vm.SendAsync();

        Assert.Equal(1, stub.UndoCalls);
        Assert.Equal("ai.slash.undoDone", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 发送_未配置时普通消息禁发_斜杠命令仍可发()
    {
        var stub = new StubAiAssistant
        {
            Selection = new AiSelectionResolution(null, AiSelectionIssue.ProviderMissing, null, null),
        };
        stub.Sessions.Add(new AiSessionSummary("s-1", "会话", AiMode.ConfirmEach, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, null));
        var vm = new AiViewModel(stub);
        await vm.LoadAsync();

        vm.ComposerText = "/help";          // 斜杠命令不走模型：未配置也可发
        Assert.True(vm.CanSend);
        await vm.SendAsync();
        Assert.Empty(stub.SendCalls);

        vm.ComposerText = "帮我整理书签";     // 普通消息需要可用模型
        Assert.False(vm.CanSend);
    }

    [Fact]
    public async Task 引擎审计_翻页携带页码_到尾页禁用下一页()
    {
        var (vm, stub) = NewVm();
        stub.AuditPageCount = 2;
        await vm.LoadAsync();
        vm.TabIndex = 3;
        await WaitUntilAsync(() => stub.AuditQueries.Count > 0);   // 页签切换触发的异步重载落地

        Assert.Equal(1, stub.AuditQueries[^1].Page);
        Assert.True(vm.CanNextAuditPage);
        Assert.False(vm.CanPrevAuditPage);

        await vm.AuditNextAsync();
        Assert.Equal(2, stub.AuditQueries[^1].Page);
        Assert.False(vm.CanNextAuditPage);
        Assert.True(vm.CanPrevAuditPage);

        await vm.AuditPrevAsync();
        Assert.Equal(1, stub.AuditQueries[^1].Page);
    }

    /// <summary>等一个可观测副作用落地（异步重载是 fire-and-forget 的界面口径；上限 2 秒，超时即失败）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "异步重载未在 2 秒内落地");
    }
}
