using LinkPocket.Contracts;
using LinkPocket.UI.Settings;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 设置页「AI 服务」VM 金标准：模型元数据弹窗的界面编辑、服务商删除权限、模板选择器模式
/// （功能书 §4.2 / §4.5 / §4.9）。
/// <para>
/// 定稿形态是「**模型行只留一行读数 + 配置全收进弹窗**」——模型配置不再就地展开，
/// 因此本组用例一律走 <c>BeginEditModel</c>（或 <c>BeginAddModel</c>）+
/// <c>SaveModelDialogAsync</c> / <c>CancelModelDialog</c> 这一对入口。
/// </para>
/// </summary>
public class AiSettingsViewModelTests
{
    /// <summary>两条服务商：一条自建（可删，带手填模型）+ 一条预设（不可删，带出厂模型）。</summary>
    private static (AiSettingsViewModel Vm, StubAiAssistant Stub) NewVm()
    {
        var stub = new StubAiAssistant();
        stub.Providers.Add(new AiProviderInfo("gw", "gw", AiProtocol.OpenAiChat, "https://gw.test/v1",
            AiProviderSource.Custom, false, true, true, "sk-1234", AiProviderStatus.Verified, null, null, null,
            [new AiModelInfo("m1", "m1", AiModelSource.Manual, Enabled: true,
                ContextWindow: null, MaxOutputTokens: null, SupportsTools: true, SupportsStreaming: true)]));
        stub.Providers.Add(new AiProviderInfo("deepseek", "DeepSeek", AiProtocol.AnthropicMessages,
            "https://api.deepseek.com/anthropic", AiProviderSource.Preset, false, false, false, null,
            AiProviderStatus.NotConfigured, null, null, null,
            [new AiModelInfo("deepseek-flash", "deepseek-flash", AiModelSource.Preset, Enabled: true,
                ContextWindow: 128_000, MaxOutputTokens: null, SupportsTools: true, SupportsStreaming: true)],
            null, "ai.provider.deepseek"));
        return (new AiSettingsViewModel(stub), stub);
    }

    /// <summary>进面板对齐后取选中服务商的模型行（默认选中左列第一项 = 自建那条）。</summary>
    private static async Task<AiModelRow> LoadRowAsync(AiSettingsViewModel vm)
    {
        await vm.LoadAsync();
        return vm.Models.Single();
    }

    private static AiProviderRow RowOf(AiSettingsViewModel vm, string providerId)
        => vm.Providers.Single(p => p.Info.Id == providerId);

    // ── 模型元数据弹窗（配置项的唯一落点）────────────────────────────

    [Fact]
    public async Task 模型能力_非法数字_弹窗内报错且不保存()
    {
        var (vm, stub) = NewVm();
        var row = await LoadRowAsync(vm);
        vm.BeginEditModel(row);
        row.ContextWindowInput = "0";            // 显式 0 = 越界（留空才是"未声明"）
        row.MaxOutputTokensInput = "4096";

        await vm.SaveModelDialogAsync();

        Assert.Empty(stub.SaveModelCalls);
        Assert.Equal("ai.settings.model.invalidNumber", row.ErrorKey);
        Assert.True(vm.IsModelDialogOpen);        // 报错留在弹窗里，改完再存
    }

    [Fact]
    public async Task 模型能力_合法保存_草稿携带能力字段且存成即关弹窗()
    {
        var (vm, stub) = NewVm();
        var row = await LoadRowAsync(vm);
        vm.BeginEditModel(row);
        row.ContextWindowInput = "64000";
        row.MaxOutputTokensInput = "";            // 留空 = 未声明
        row.SupportsTools = false;
        row.SupportsStreaming = true;

        await vm.SaveModelDialogAsync();

        var draft = Assert.Single(stub.SaveModelCalls);
        Assert.Equal("gw", draft.ProviderId);
        Assert.Equal("m1", draft.Id);
        Assert.Equal(64_000, draft.ContextWindow);
        Assert.Null(draft.MaxOutputTokens);
        Assert.False(draft.SupportsTools);
        Assert.True(draft.SupportsStreaming);
        Assert.True(draft.Enabled);               // 启用状态保持
        Assert.False(vm.IsModelDialogOpen);
    }

    [Fact]
    public async Task 模型弹窗_从当前模型取初值_取消即丢弃草稿()
    {
        var (vm, stub) = NewVm();
        var row = await LoadRowAsync(vm);
        vm.BeginEditModel(row);
        Assert.True(vm.IsModelDialogOpen);
        Assert.False(vm.IsModelDialogAdd);          // 配置弹窗：模型 ID 是既成事实、只读展示
        Assert.Equal("ai.settings.model.edit", vm.ModelDialogTitleKey);
        Assert.Equal("", row.ContextWindowInput);   // 未声明 = 空框

        row.ContextWindowInput = "乱写";
        vm.CancelModelDialog();
        Assert.False(vm.IsModelDialogOpen);
        Assert.Empty(stub.SaveModelCalls);

        vm.BeginEditModel(row);                     // 再开 = 重新取初值（草稿不留）
        Assert.Equal("", row.ContextWindowInput);
    }

    [Fact]
    public async Task 模型弹窗_添加时模型ID必填_缺ID就地报错不发保存()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        vm.BeginAddModel();

        Assert.True(vm.IsModelDialogAdd);           // 添加弹窗：模型 ID 可填
        Assert.Equal("ai.settings.model.add", vm.ModelDialogTitleKey);
        Assert.Equal("", vm.ModelDialogRow!.ModelIdInput);   // 空草稿：ID 待填

        await vm.SaveModelDialogAsync();
        Assert.Empty(stub.SaveModelCalls);
        Assert.Equal("ai.settings.model.idRequired", vm.ModelDialogRow!.ErrorKey);

        vm.ModelDialogRow!.ModelIdInput = " m2 ";   // ID 两端空白裁掉再用
        await vm.SaveModelDialogAsync();
        var draft = Assert.Single(stub.SaveModelCalls);
        Assert.Equal("m2", draft.Id);
        Assert.Equal("gw", draft.ProviderId);
    }

    // ── 服务商删除权限（预设 = 出厂模板，不是用户数据）──────────────

    [Fact]
    public async Task 服务商删除_预设不可删_自定义可删()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();

        // 模板选择器的数据源 = 左列同一批投影里 Source == Preset 的那些（不另立事实源）
        Assert.Equal(new[] { "deepseek" }, vm.PresetProviders.Select(p => p.Info.Id));

        vm.SelectedProvider = RowOf(vm, "deepseek");
        Assert.False(vm.CanDeleteProvider);         // 删除键按它收起 → 界面不摆会失败的按钮
        await vm.DeleteProviderAsync();
        Assert.Empty(stub.DeleteProviderCalls);     // VM 这一层就不发删除（数据层另有 LP.AI.016 兜底）

        vm.SelectedProvider = RowOf(vm, "gw");
        Assert.True(vm.CanDeleteProvider);
        await vm.DeleteProviderAsync();
        Assert.Equal(new[] { "gw" }, stub.DeleteProviderCalls);
    }

    // ── 「添加服务商」模板选择器（右栏的另一种模式）────────────────

    [Fact]
    public async Task 添加服务商_模板选择器与详情表单互斥_返回或选预设即回表单()
    {
        var (vm, _) = NewVm();
        await vm.LoadAsync();
        Assert.True(vm.IsProviderFormVisible);      // 默认 = 选中项的详情表单

        vm.BeginAddProvider();
        Assert.True(vm.IsPickingTemplate);
        Assert.False(vm.IsProviderFormVisible);     // 右栏同一时刻只画一个

        vm.CancelAddProvider();
        Assert.False(vm.IsPickingTemplate);
        Assert.True(vm.IsProviderFormVisible);

        vm.BeginAddProvider();
        vm.SelectedProvider = RowOf(vm, "deepseek");   // 点预设卡 = 选中该预设 → 回表单
        Assert.False(vm.IsPickingTemplate);
        Assert.True(vm.IsProviderFormVisible);
    }
}
