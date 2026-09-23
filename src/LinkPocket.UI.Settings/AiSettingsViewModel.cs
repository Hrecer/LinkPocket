using System.Collections.ObjectModel;
using System.ComponentModel;
using LinkPocket.Contracts;

namespace LinkPocket.UI.Settings;

/// <summary>
/// 设置页「AI 服务」VM：服务商 / 密钥 / 模型 / 连通性 / 助手偏好。
/// 文案一律存键（视图取词）；密钥只写不读（明文永不进界面，界面只拿掩码）。
/// </summary>
public sealed class AiSettingsViewModel : INotifyPropertyChanged
{
    private readonly IAiAssistant _assistant;

    private AiProviderRow? _selectedProvider;
    private string _displayNameInput = "";
    private string _baseUrlInput = "";
    private string _newModelId = "";
    private string _testStatusKey = "";
    private string _testDetail = "";
    private int _modeIndex = 1;
    private bool _advancedTools;
    private AiModelChoice? _defaultModelChoice;

    public AiSettingsViewModel(IAiAssistant assistant)
        => _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AiProviderRow> Providers { get; } = [];
    public ObservableCollection<AiModelRow> Models { get; } = [];
    public ObservableCollection<AiModelChoice> ModelChoices { get; } = [];

    public AiProviderRow? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!Set(ref _selectedProvider, value, nameof(SelectedProvider))) return;
            DisplayNameInput = value?.Info.DisplayName ?? "";
            BaseUrlInput = value?.Info.BaseUrl ?? "";
            TestStatusKey = value is null ? "" : LinkPocket.Views.AiKeyMap.Status(value.Info.Status);
            TestDetail = value?.Info.StatusErrorCode is { } code ? LinkPocket.Views.AiKeyMap.Error(code) : "";
            RefreshModels();
        }
    }

    public string DisplayNameInput
    {
        get => _displayNameInput;
        set => Set(ref _displayNameInput, value, nameof(DisplayNameInput));
    }

    public string BaseUrlInput
    {
        get => _baseUrlInput;
        set => Set(ref _baseUrlInput, value, nameof(BaseUrlInput));
    }

    public string NewModelId
    {
        get => _newModelId;
        set => Set(ref _newModelId, value, nameof(NewModelId));
    }

    public string TestStatusKey
    {
        get => _testStatusKey;
        private set => Set(ref _testStatusKey, value, nameof(TestStatusKey));
    }

    public string TestDetail
    {
        get => _testDetail;
        private set => Set(ref _testDetail, value, nameof(TestDetail));
    }

    /// <summary>服务商状态徽标（选中项为空 → 不显示）。</summary>
    public bool HasSelection => SelectedProvider is not null;

    /// <summary>API Key 掩码（未配置 → 空）。</summary>
    public string ApiKeyMasked => SelectedProvider?.Info.ApiKeyMasked ?? "";

    public int ModeIndex
    {
        get => _modeIndex;
        set => Set(ref _modeIndex, value, nameof(ModeIndex));
    }

    public bool AdvancedTools
    {
        get => _advancedTools;
        set => Set(ref _advancedTools, value, nameof(AdvancedTools));
    }

    public AiModelChoice? DefaultModelChoice
    {
        get => _defaultModelChoice;
        set => Set(ref _defaultModelChoice, value, nameof(DefaultModelChoice));
    }

    /// <summary>进面板对齐：读偏好 + 刷新清单（含损坏文件如实暴露）。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var preferences = await _assistant.GetPreferencesAsync();
            ModeIndex = (int)preferences.Mode;
            AdvancedTools = preferences.AdvancedToolsEnabled;
            await RefreshProvidersAsync(preferences).ConfigureAwait(true);
            TestStatusKey = "";
            TestDetail = "";
        }
        catch (AiException ex)
        {
            TestStatusKey = "ai.err.dataStoreFailed";
            TestDetail = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    private async Task RefreshProvidersAsync(AiPreferences? preferences = null)
    {
        preferences ??= await _assistant.GetPreferencesAsync().ConfigureAwait(true);
        var keepId = SelectedProvider?.Info.Id;
        Providers.Clear();
        ModelChoices.Clear();

        foreach (var info in await _assistant.ListProvidersAsync().ConfigureAwait(true))
        {
            Providers.Add(new AiProviderRow(info));
            foreach (var model in info.Models.Where(m => m.Enabled))
                ModelChoices.Add(new AiModelChoice(info.Id, model.Id));
        }

        SelectedProvider = Providers.FirstOrDefault(p => p.Info.Id == keepId) ?? Providers.FirstOrDefault();
        DefaultModelChoice = ModelChoices.FirstOrDefault(c =>
            c.ProviderId == preferences.ProviderId && c.ModelId == preferences.ModelId);
        Raise(nameof(ApiKeyMasked));
        Raise(nameof(HasSelection));
    }

    private void RefreshModels()
    {
        Models.Clear();
        if (SelectedProvider is null) return;
        foreach (var model in SelectedProvider.Info.Models)
            Models.Add(new AiModelRow(SelectedProvider.Info.Id, model));
    }

    /// <summary>保存服务商（草稿宽松：半填也能存；完整性问题由状态徽标如实列出）。</summary>
    public async Task SaveProviderAsync()
    {
        if (SelectedProvider is null) return;
        var info = SelectedProvider.Info;
        await GuardAsync(async () =>
        {
            await _assistant.SaveProviderAsync(new AiProviderDraft(
                info.Id, DisplayNameInput, info.Protocol, BaseUrlInput, info.Enabled, info.IsLocal));
            await RefreshProvidersAsync().ConfigureAwait(true);
            Raise(nameof(ApiKeyMasked));
        }).ConfigureAwait(true);
    }

    /// <summary>删除服务商（预设回出厂模板、自定义整体移除；密钥一并删除）。</summary>
    public async Task DeleteProviderAsync()
    {
        if (SelectedProvider is null) return;
        var id = SelectedProvider.Info.Id;
        await GuardAsync(async () =>
        {
            await _assistant.DeleteProviderAsync(id);
            await RefreshProvidersAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    public async Task SaveKeyAsync(string apiKey)
    {
        if (SelectedProvider is null || string.IsNullOrWhiteSpace(apiKey)) return;
        var id = SelectedProvider.Info.Id;
        await GuardAsync(async () =>
        {
            await _assistant.SetApiKeyAsync(id, apiKey);
            await RefreshProvidersAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    public async Task RemoveKeyAsync()
    {
        if (SelectedProvider is null) return;
        var id = SelectedProvider.Info.Id;
        await GuardAsync(async () =>
        {
            await _assistant.RemoveApiKeyAsync(id);
            await RefreshProvidersAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>连通性测试（结果三态 + 耗时；徽标就地更新）。</summary>
    public async Task TestAsync()
    {
        if (SelectedProvider is null) return;
        var id = SelectedProvider.Info.Id;
        TestStatusKey = "ai.status.testing";
        TestDetail = "";
        await GuardAsync(async () =>
        {
            var result = await _assistant.TestConnectivityAsync(id).ConfigureAwait(true);
            TestStatusKey = LinkPocket.Views.AiKeyMap.Status(result.Status);
            TestDetail = result.ErrorCode is { } code
                ? $"{LinkPocket.Views.AiKeyMap.Error(code)} · {result.ElapsedMs} ms"
                : $"{result.ElapsedMs} ms · {result.ModelCount ?? 0} models";
            await RefreshProvidersAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>拉取模型列表（不支持 → 引导手填；新模型默认未启用）。</summary>
    public async Task FetchModelsAsync()
    {
        if (SelectedProvider is null) return;
        var id = SelectedProvider.Info.Id;
        await GuardAsync(async () =>
        {
            await _assistant.RefreshModelsAsync(id);
            await RefreshProvidersAsync().ConfigureAwait(true);
            RefreshModels();
        }).ConfigureAwait(true);
    }

    /// <summary>手填模型（新增默认启用，来源 = 手填）。</summary>
    public async Task AddModelAsync()
    {
        if (SelectedProvider is null || string.IsNullOrWhiteSpace(NewModelId)) return;
        var providerId = SelectedProvider.Info.Id;
        var modelId = NewModelId.Trim();
        await GuardAsync(async () =>
        {
            await _assistant.SaveModelAsync(new AiModelDraft(providerId, modelId, modelId, Enabled: true,
                ContextWindow: null, MaxOutputTokens: null, SupportsTools: true, SupportsStreaming: true));
            NewModelId = "";
            await RefreshProvidersAsync().ConfigureAwait(true);
            RefreshModels();
        }).ConfigureAwait(true);
    }

    /// <summary>启用 / 停用一个模型（能力声明保持现值）。</summary>
    public async Task ToggleModelAsync(AiModelRow row)
    {
        await GuardAsync(async () =>
        {
            await _assistant.SaveModelAsync(new AiModelDraft(row.ProviderId, row.Model.Id, row.Model.DisplayName,
                !row.Model.Enabled, row.Model.ContextWindow, row.Model.MaxOutputTokens,
                row.Model.SupportsTools, row.Model.SupportsStreaming));
            await RefreshProvidersAsync().ConfigureAwait(true);
            RefreshModels();
        }).ConfigureAwait(true);
    }

    /// <summary>保存助手偏好（默认模型 / 模式 / 高级工具）。</summary>
    public async Task SavePreferencesAsync()
    {
        await GuardAsync(async () =>
        {
            var current = await _assistant.GetPreferencesAsync();
            await _assistant.SavePreferencesAsync(current with
            {
                ProviderId = DefaultModelChoice?.ProviderId,
                ModelId = DefaultModelChoice?.ModelId,
                Mode = (AiMode)ModeIndex,
                AdvancedToolsEnabled = AdvancedTools,
            });
        }).ConfigureAwait(true);
    }

    /// <summary>清空全部 AI 数据（会话 / 台账 / 大结果；审计面不受影响）。</summary>
    public async Task ClearSessionsAsync()
    {
        await GuardAsync(async () =>
        {
            foreach (var session in await _assistant.ListSessionsAsync().ConfigureAwait(true))
                await _assistant.DeleteSessionAsync(session.SessionId).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (AiException ex)
        {
            TestStatusKey = "ai.err.generic";
            TestDetail = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    private bool Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>服务商行（状态徽标走键；显示名走 DisplayNameKey 或用户数据）。</summary>
public sealed class AiProviderRow(AiProviderInfo info)
{
    public AiProviderInfo Info { get; private set; } = info;
    public string StatusKey => LinkPocket.Views.AiKeyMap.Status(Info.Status);
    public string? DisplayKey => Info.DisplayNameKey;
    public string DisplayText => Info.DisplayName;
    public bool HasApiKey => Info.HasApiKey;

    public void Update(AiProviderInfo next) => Info = next;
}

/// <summary>模型行（启用开关 + 能力机器面标识）。</summary>
public sealed class AiModelRow(string providerId, AiModelInfo model)
{
    public string ProviderId { get; } = providerId;
    public AiModelInfo Model { get; private set; } = model;
    public string Hint => $"{Model.Source} · {(Model.SupportsTools ? "tools" : "no-tools")} · ctx {Model.ContextWindow ?? 0}";

    public void Update(AiModelInfo next) => Model = next;
}

/// <summary>默认模型选择项（标签是机器面标识符，无需取词）。</summary>
public sealed record AiModelChoice(string ProviderId, string ModelId)
{
    public string Label => $"{ProviderId} / {ModelId}";
}

