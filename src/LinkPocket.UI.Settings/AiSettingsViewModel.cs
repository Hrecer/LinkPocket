using System.Collections.ObjectModel;
using System.ComponentModel;
using LinkPocket.Contracts;
using LinkPocket.I18n;

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
    private int _protocolIndex;
    private string _newModelId = "";
    private string _testStatusKey = "";
    private LocValue _testDetail = LocValue.Empty;
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
            ProtocolIndex = value is null ? 0 : Math.Max(0, Array.IndexOf(ProtocolOrder, value.Info.Protocol));
            TestStatusKey = value is null ? "" : LinkPocket.Views.AiKeyMap.Status(value.Info.Status);
            TestDetail = value?.Info.StatusErrorCode is { } code
                ? LocValue.Of(LinkPocket.Views.AiKeyMap.Error(code))
                : LocValue.Empty;
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

    /// <summary>「API 格式」下拉的显示顺序（**与协议枚举解耦**：下拉按接入格式族排，映射只在这一处做），
    /// 下拉项文案见 <c>ai.settings.apiFormat.*</c>。</summary>
    private static readonly AiProtocol[] ProtocolOrder =
        [AiProtocol.AnthropicMessages, AiProtocol.OpenAiChat, AiProtocol.OpenAiResponses];

    /// <summary>接入格式（= 协议）下拉的选中序号（索引进 <see cref="ProtocolOrder"/>）。
    /// 保存时落成 <see cref="AiProtocol"/>；换服务商时跟随该服务商当前格式。</summary>
    public int ProtocolIndex
    {
        get => _protocolIndex;
        set => Set(ref _protocolIndex, value, nameof(ProtocolIndex));
    }

    public string NewModelId
    {
        get => _newModelId;
        set => Set(ref _newModelId, value, nameof(NewModelId));
    }

    /// <summary>连通性 / 保存结果的状态文案键（空 = 不显示）。</summary>
    public string TestStatusKey
    {
        get => _testStatusKey;
        private set => Set(ref _testStatusKey, value, nameof(TestStatusKey));
    }

    /// <summary>连通性 / 保存结果的明细（含变量的整句 = LocValue：耗时与模型数由这里给、渲染边界取词）。</summary>
    public LocValue TestDetail
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

    /// <summary>进面板对齐：读偏好 + 刷新清单 + 用量读数（含损坏文件如实暴露）。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var preferences = await _assistant.GetPreferencesAsync();
            ModeIndex = (int)preferences.Mode;
            AdvancedTools = preferences.AdvancedToolsEnabled;
            await RefreshProvidersAsync(preferences).ConfigureAwait(true);
            TestStatusKey = "";
            TestDetail = LocValue.Empty;
            await RefreshUsageAsync().ConfigureAwait(true);
        }
        catch (AiException ex)
        {
            TestStatusKey = "ai.err.dataStoreFailed";
            TestDetail = LocValue.Of(LinkPocket.Views.AiKeyMap.Error(ex.Error.Code));
        }
    }

    // ── 近 7 天用量（只读行；数据源 = ai/usage.json 的按天 × 模型汇总）────────────

    private LocValue _usageValue = LocValue.Empty;

    /// <summary>近 7 天用量整句（含变量，渲染边界取词；读数取不到 = 空）。</summary>
    public LocValue UsageValue
    {
        get => _usageValue;
        private set => Set(ref _usageValue, value, nameof(UsageValue));
    }

    public async Task RefreshUsageAsync()
    {
        try
        {
            var summary = await _assistant.GetUsageSummaryAsync(7).ConfigureAwait(true);
            UsageValue = summary.Calls == 0
                ? LocValue.Empty
                : Loc.K("ai.settings.usage.line", summary.Calls, summary.InputTokens, summary.OutputTokens);
        }
        catch (AiException ex)
        {
            TestDetail = LocValue.Of(LinkPocket.Views.AiKeyMap.Error(ex.Error.Code));
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
                info.Id, DisplayNameInput, ProtocolOrder[Math.Clamp(ProtocolIndex, 0, ProtocolOrder.Length - 1)],
                BaseUrlInput, info.Enabled, info.IsLocal));
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
        TestDetail = LocValue.Empty;
        await GuardAsync(async () =>
        {
            var result = await _assistant.TestConnectivityAsync(id).ConfigureAwait(true);
            TestStatusKey = LinkPocket.Views.AiKeyMap.Status(result.Status);
            TestDetail = result.ErrorCode is { } code
                ? Loc.K("ai.settings.test.detailErr", LocValue.Of(LinkPocket.Views.AiKeyMap.Error(code)),
                    result.ElapsedMs)
                : Loc.K("ai.settings.test.detail", result.ElapsedMs, result.ModelCount ?? 0);
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
                row.Model.SupportsTools, row.Model.SupportsStreaming,
                row.Model.InputFormat, row.Model.SupportsJsonSchemaOutput, row.Model.SupportsNativeWebSearch,
                row.Model.SupportsMidConversationSystem, row.Model.Reasoning));
            await RefreshProvidersAsync().ConfigureAwait(true);
            RefreshModels();
        }).ConfigureAwait(true);
    }

    /// <summary>展开 / 收起某模型的能力编辑行（显式保存，不做失焦提交）。</summary>
    public void ToggleModelEdit(AiModelRow row)
    {
        if (row.IsEditing) row.CancelEdit();
        else row.BeginEdit();
    }

    /// <summary>保存某模型的配置（上下文窗口 / 最大输出 / 输入模态 / 能力 / 推理等级 / 工具 / 流式）。
    /// 数字框留空 = 未声明（按保守缺省）；非法值就地报错、不发保存（拿不准就报错，不猜意图）。</summary>
    public async Task SaveModelCapabilitiesAsync(AiModelRow row)
    {
        if (!TryParseCapability(row.ContextWindowInput, out var contextWindow)
            || !TryParseCapability(row.MaxOutputTokensInput, out var maxOutputTokens))
        {
            row.ErrorKey = "ai.settings.model.invalidNumber";
            return;
        }

        row.ErrorKey = "";
        try
        {
            await _assistant.SaveModelAsync(new AiModelDraft(row.ProviderId, row.Model.Id, row.Model.DisplayName,
                row.Model.Enabled, contextWindow, maxOutputTokens, row.SupportsTools, row.SupportsStreaming,
                new AiModelInputFormat(true, row.SupportsImage, row.SupportsVideo, row.SupportsPdf),
                row.SupportsJsonSchemaOutput, row.SupportsNativeWebSearch, row.SupportsMidConversationSystem,
                ToReasoning(row)))
                .ConfigureAwait(true);
            await RefreshProvidersAsync().ConfigureAwait(true);
            RefreshModels();
        }
        catch (AiException ex)
        {
            row.ErrorKey = LinkPocket.Views.AiKeyMap.Error(ex.Error.Code);
        }
    }

    /// <summary>推理等级草稿 → 声明对象：等级与映射都空 = 未声明（不落空壳）。</summary>
    private static AiModelReasoning? ToReasoning(AiModelRow row)
    {
        var map = string.IsNullOrWhiteSpace(row.ReasoningMapInput) ? null : row.ReasoningMapInput;
        return row.ReasoningLevels.Count == 0 && map is null ? null : new AiModelReasoning([.. row.ReasoningLevels], map);
    }

    /// <summary>追加一个推理等级（空值忽略、重名忽略；顺序 = 添加顺序）。</summary>
    public static void AddReasoningLevel(AiModelRow row)
    {
        var level = row.NewReasoningLevel.Trim();
        if (level.Length == 0 || row.ReasoningLevels.Contains(level, StringComparer.Ordinal)) return;
        row.ReasoningLevels.Add(level);
        row.NewReasoningLevel = "";
    }

    /// <summary>删除一个推理等级。</summary>
    public static void RemoveReasoningLevel(AiModelRow row, string level) => row.ReasoningLevels.Remove(level);

    /// <summary>能力数字框解析：空白 = 未声明（null）；正整数 = 声明值；其余一律非法。</summary>
    private static bool TryParseCapability(string input, out int? value)
    {
        value = null;
        var text = input.Trim();
        if (text.Length == 0) return true;
        if (!int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 1)
            return false;
        value = number;
        return true;
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
            TestDetail = LocValue.Of(LinkPocket.Views.AiKeyMap.Error(ex.Error.Code));
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

    /// <summary>预设显示名走文案键（本地化）；用户改过名 / 自建 → 直接显示原名（用户数据）。</summary>
    public bool HasDisplayKey => Info.DisplayNameKey is { Length: > 0 };

    public string DisplayText => Info.DisplayName;
    public bool HasApiKey => Info.HasApiKey;

    public void Update(AiProviderInfo next) => Info = next;
}

/// <summary>模型行（启用开关 + 能力编辑行；提示行是机器面标识符）。</summary>
public sealed class AiModelRow : INotifyPropertyChanged
{
    private bool _isEditing;
    private string _contextWindowInput = "";
    private string _maxOutputTokensInput = "";
    private bool _supportsTools;
    private bool _supportsStreaming;
    private bool _supportsImage;
    private bool _supportsVideo;
    private bool _supportsPdf;
    private bool _supportsJsonSchemaOutput;
    private bool _supportsNativeWebSearch;
    private bool _supportsMidConversationSystem;
    private string _reasoningMapInput = "";
    private string _newReasoningLevel = "";
    private string? _errorKey;

    public AiModelRow(string providerId, AiModelInfo model)
    {
        ProviderId = providerId;
        Model = model;
        _supportsTools = model.SupportsTools;
        _supportsStreaming = model.SupportsStreaming;
    }

    public string ProviderId { get; }
    public AiModelInfo Model { get; private set; }
    /// <summary>副行：来源 · 工具调用（上下文窗口由行上的徽标承担，不在副行重复）。</summary>
    public LocValue Hint => Loc.K("ai.settings.model.line",
        LocValue.Of(Model.Source switch
        {
            AiModelSource.Preset => "ai.settings.model.source.preset",
            AiModelSource.Fetched => "ai.settings.model.source.fetched",
            _ => "ai.settings.model.source.manual",
        }),
        LocValue.Of(Model.SupportsTools ? "ai.settings.model.tools.yes" : "ai.settings.model.tools.no"));

    /// <summary>行内上下文窗口徽标：读数压缩成 K/M（机器面读数，不进文案表）；未声明 → 空（不画空徽标）。</summary>
    public string ContextWindowBadge
        => Model.ContextWindow is { } window ? ShortWindow(window) : "";

    public bool HasContextWindowBadge => Model.ContextWindow is not null;

    /// <summary>徽标的悬浮提示（整句 = LocValue：数值由这里给、渲染边界取词）。</summary>
    public LocValue ContextWindowBadgeTip => Model.ContextWindow is { } window
        ? Loc.K("ai.settings.model.ctx.badgeLabel", LocValue.Literal(ShortWindow(window)))
        : LocValue.Of("ai.settings.model.ctx.unset");

    private static string ShortWindow(int window) => window switch
    {
        >= 1_000_000 => (window / 1_000_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (window / 1_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "K",
        _ => window.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>能力编辑行是否展开（显式保存，不做失焦提交——归属唯一、不怕刷新重建行）。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            Raise(nameof(IsEditing));
            Raise(nameof(EditKey));
        }
    }

    /// <summary>展开 / 收起按钮文案键。</summary>
    public string EditKey => IsEditing ? "ai.diff.collapse" : "ai.settings.model.capabilities";

    public string ContextWindowInput
    {
        get => _contextWindowInput;
        set => Set(ref _contextWindowInput, value, nameof(ContextWindowInput));
    }

    public string MaxOutputTokensInput
    {
        get => _maxOutputTokensInput;
        set => Set(ref _maxOutputTokensInput, value, nameof(MaxOutputTokensInput));
    }

    public bool SupportsTools
    {
        get => _supportsTools;
        set => Set(ref _supportsTools, value, nameof(SupportsTools));
    }

    public bool SupportsStreaming
    {
        get => _supportsStreaming;
        set => Set(ref _supportsStreaming, value, nameof(SupportsStreaming));
    }

    /// <summary>输入模态（文本恒为真，不给开关）。</summary>
    public bool SupportsImage
    {
        get => _supportsImage;
        set => Set(ref _supportsImage, value, nameof(SupportsImage));
    }

    public bool SupportsVideo
    {
        get => _supportsVideo;
        set => Set(ref _supportsVideo, value, nameof(SupportsVideo));
    }

    public bool SupportsPdf
    {
        get => _supportsPdf;
        set => Set(ref _supportsPdf, value, nameof(SupportsPdf));
    }

    public bool SupportsJsonSchemaOutput
    {
        get => _supportsJsonSchemaOutput;
        set => Set(ref _supportsJsonSchemaOutput, value, nameof(SupportsJsonSchemaOutput));
    }

    public bool SupportsNativeWebSearch
    {
        get => _supportsNativeWebSearch;
        set => Set(ref _supportsNativeWebSearch, value, nameof(SupportsNativeWebSearch));
    }

    public bool SupportsMidConversationSystem
    {
        get => _supportsMidConversationSystem;
        set => Set(ref _supportsMidConversationSystem, value, nameof(SupportsMidConversationSystem));
    }

    /// <summary>推理等级映射（机器面 JSON 原文；空 = 未声明）。</summary>
    public string ReasoningMapInput
    {
        get => _reasoningMapInput;
        set => Set(ref _reasoningMapInput, value, nameof(ReasoningMapInput));
    }

    /// <summary>新增推理等级的输入框（点「添加」把非空值追加到 <see cref="ReasoningLevels"/>）。</summary>
    public string NewReasoningLevel
    {
        get => _newReasoningLevel;
        set => Set(ref _newReasoningLevel, value, nameof(NewReasoningLevel));
    }

    /// <summary>推理等级（**有序**；编辑期为草稿，点保存才落库）。</summary>
    public ObservableCollection<string> ReasoningLevels { get; } = [];

    /// <summary>行内校验 / 保存错误的文案键（空 = 无错误）。</summary>
    public string ErrorKey
    {
        get => _errorKey ?? "";
        set
        {
            _errorKey = string.IsNullOrEmpty(value) ? null : value;
            Raise(nameof(ErrorKey));
            Raise(nameof(HasError));
        }
    }

    public bool HasError => _errorKey is not null;

    /// <summary>进入编辑（输入从当前模型能力初始化；数字框留空 = 未声明）。</summary>
    public void BeginEdit()
    {
        ContextWindowInput = Model.ContextWindow?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        MaxOutputTokensInput = Model.MaxOutputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        SupportsTools = Model.SupportsTools;
        SupportsStreaming = Model.SupportsStreaming;
        var input = Model.InputFormat;
        SupportsImage = input?.SupportsImage ?? false;
        SupportsVideo = input?.SupportsVideo ?? false;
        SupportsPdf = input?.SupportsPdf ?? false;
        SupportsJsonSchemaOutput = Model.SupportsJsonSchemaOutput;
        SupportsNativeWebSearch = Model.SupportsNativeWebSearch;
        SupportsMidConversationSystem = Model.SupportsMidConversationSystem;
        ReasoningMapInput = Model.Reasoning?.MapJson ?? "";
        ReasoningLevels.Clear();
        foreach (var level in Model.Reasoning?.Levels ?? []) ReasoningLevels.Add(level);
        NewReasoningLevel = "";
        ErrorKey = "";
        IsEditing = true;
    }

    public void CancelEdit()
    {
        ErrorKey = "";
        IsEditing = false;
    }

    public void Update(AiModelInfo next) => Model = next;

    private bool Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>默认模型选择项（标签是机器面标识符，无需取词）。</summary>
public sealed record AiModelChoice(string ProviderId, string ModelId)
{
    public string Label => $"{ProviderId} / {ModelId}";
}

