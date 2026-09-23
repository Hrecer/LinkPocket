using LinkPocket.Contracts;
using LinkPocket.UI.Settings;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>设置页「AI 服务」VM 金标准：模型能力字段的界面编辑（功能书 §4.5/§4.9）。</summary>
public class AiSettingsViewModelTests
{
    private static (AiSettingsViewModel Vm, StubAiAssistant Stub) NewVm()
    {
        var stub = new StubAiAssistant();
        stub.Providers.Add(new AiProviderInfo("gw", "gw", AiProtocol.OpenAiChat, "https://gw.test/v1",
            AiProviderSource.Custom, false, true, true, "sk-1234", AiProviderStatus.Verified, null, null, null,
            [new AiModelInfo("m1", "m1", AiModelSource.Manual, Enabled: true,
                ContextWindow: null, MaxOutputTokens: null, SupportsTools: true, SupportsStreaming: true)]));
        return (new AiSettingsViewModel(stub), stub);
    }

    private static async Task<AiModelRow> LoadRowAsync(AiSettingsViewModel vm)
    {
        await vm.LoadAsync();
        return vm.Models.Single();
    }

    [Fact]
    public async Task 模型能力_非法数字_行内报错且不保存()
    {
        var (vm, stub) = NewVm();
        var row = await LoadRowAsync(vm);
        vm.ToggleModelEdit(row);
        row.ContextWindowInput = "0";            // 显式 0 = 越界（留空才是"未声明"）
        row.MaxOutputTokensInput = "4096";

        await vm.SaveModelCapabilitiesAsync(row);

        Assert.Empty(stub.SaveModelCalls);
        Assert.Equal("ai.settings.model.invalidNumber", row.ErrorKey);
        Assert.True(row.IsEditing);               // 报错留在编辑态，改完再存
    }

    [Fact]
    public async Task 模型能力_合法保存_草稿携带四个能力字段()
    {
        var (vm, stub) = NewVm();
        var row = await LoadRowAsync(vm);
        vm.ToggleModelEdit(row);
        row.ContextWindowInput = "64000";
        row.MaxOutputTokensInput = "";            // 留空 = 未声明
        row.SupportsTools = false;
        row.SupportsStreaming = true;

        await vm.SaveModelCapabilitiesAsync(row);

        var draft = Assert.Single(stub.SaveModelCalls);
        Assert.Equal("gw", draft.ProviderId);
        Assert.Equal("m1", draft.Id);
        Assert.Equal(64_000, draft.ContextWindow);
        Assert.Null(draft.MaxOutputTokens);
        Assert.False(draft.SupportsTools);
        Assert.True(draft.SupportsStreaming);
        Assert.True(draft.Enabled);               // 启用状态保持
    }

    [Fact]
    public async Task 模型能力_编辑行从当前模型取初值_收起即丢弃()
    {
        var (vm, stub) = NewVm();
        var row = await LoadRowAsync(vm);
        vm.ToggleModelEdit(row);
        Assert.True(row.IsEditing);
        Assert.Equal("", row.ContextWindowInput);   // 未声明 = 空框

        row.ContextWindowInput = "乱写";
        vm.ToggleModelEdit(row);                    // 再点 = 收起（不做失焦提交）
        Assert.False(row.IsEditing);
        Assert.Empty(stub.SaveModelCalls);
    }
}
