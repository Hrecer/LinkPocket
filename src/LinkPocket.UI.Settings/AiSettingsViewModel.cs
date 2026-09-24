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
    private AiModelRow? _modelDialogRow;
    private bool _isModelDialogAdd;
    private string _testStatusKey = "";
    private LocValue _testDetail = LocValue.Empty;
    private int _modeIndex = 1;
    private bool _advancedTools;
    private AiModelChoice? _defaultModelChoice;

    public AiSettingsViewModel(IAiAssistant assistant)
        => _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AiProviderRow> Providers { get; } = [];

    /// <summary>预设服务商（「添加服务商」模板选择器里的卡；**与左列同一批投影**，不另立事实源）。</summary>
    public ObservableCollection<AiProviderRow> PresetProviders { get; } = [];

    public ObservableCollection<AiModelRow> Models { get; } = [];
    public ObservableCollection<AiModelChoice> ModelChoices { get; } = [];

    public AiProviderRow? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!Set(ref _selectedProvider, value, nameof(SelectedProvider))) return;
            IsPickingTemplate = false;   // 选中任一服务商 = 回表单（模板选择器只是右栏的另一种模式）
            CancelModelDialog();         // 模型弹窗里的草稿属于上一个服务商，换人即丢
            DisplayNameInput = value?.Info.DisplayName ?? "";
            BaseUrlInput = value?.Info.BaseUrl ?? "";
            ProtocolIndex = value is null ? 0 : Math.Max(0, Array.IndexOf(ProtocolOrder, value.Info.Protocol));
            TestStatusKey = value is null ? "" : LinkPocket.Views.AiKeyMap.Status(value.Info.Status);
            TestDetail = value?.Info.StatusErrorCode is { } code
                ? LocValue.Of(LinkPocket.Views.AiKeyMap.Error(code))
                : LocValue.Empty;
            RefreshModels();
            Raise(nameof(CanDeleteProvider));
        }
    }

    /// <summary>能否删除：**只有自定义服务商可删**（预设是出厂模板、不是用户数据；改名 / 改地址仍可覆盖）。</summary>
    public bool CanDeleteProvider => SelectedProvider?.Info.Source == AiProviderSource.Custom;

    // ── 「添加服务商」模板选择器（右栏的另一种模式；与表单互斥）──────────────

    private bool _isPickingTemplate;

    /// <summary>右栏是否处于「添加服务商」模板选择器模式（此时表单让位）。</summary>
    public bool IsPickingTemplate
    {
        get => _isPickingTemplate;
        private set
        {
            if (!Set(ref _isPickingTemplate, value, nameof(IsPickingTemplate))) return;
            Raise(nameof(IsProviderFormVisible));
        }
    }

    /// <summary>表单是否展出（模板选择器打开时收起：同一块右栏只画一个）。</summary>
    public bool IsProviderFormVisible => !_isPickingTemplate;

    /// <summary>打开模板选择器（自建服务商从接入格式模板起步，可建多个）。</summary>
    public void BeginAddProvider() => IsPickingTemplate = true;

    /// <summary>返回服务商详情（模板选择器的「返回」）。</summary>
    public void CancelAddProvider() => IsPickingTemplate = false;

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

    // ── 模型元数据弹窗（**行只留一行读数**；配置全部收进弹窗，面板不随模型变长）──────

    /// <summary>弹窗里的模型草稿行（null = 弹窗未开）。</summary>
    public AiModelRow? ModelDialogRow
    {
        get => _modelDialogRow;
        private set
        {
            if (!Set(ref _modelDialogRow, value, nameof(ModelDialogRow))) return;
            Raise(nameof(IsModelDialogOpen));
            Raise(nameof(ModelDialogTitleKey));
        }
    }

    public bool IsModelDialogOpen => _modelDialogRow is not null;

    /// <summary>弹窗是「添加模型」（模型 ID 可填）还是「模型配置」（ID 是既成事实，只读）。</summary>
    public bool IsModelDialogAdd => _isModelDialogAdd;

    public string ModelDialogTitleKey => _isModelDialogAdd ? "ai.settings.model.add" : "ai.settings.model.edit";

    /// <summary>打开「添加模型」弹窗（空草稿：ID 待填，能力按保守缺省）。</summary>
    public void BeginAddModel()
    {
        if (SelectedProvider is null) return;
        _isModelDialogAdd = true;
        ModelDialogRow = AiModelRow.NewDraft(SelectedProvider.Info.Id);
        ModelDialogRow.BeginEdit();
    }

    /// <summary>打开「模型配置」弹窗（草稿从该模型当前能力取初值）。</summary>
    public void BeginEditModel(AiModelRow row)
    {
        _isModelDialogAdd = false;
        ModelDialogRow = row;
        row.BeginEdit();
    }

    /// <summary>关闭弹窗（草稿丢弃，不落库）。</summary>
    public void CancelModelDialog()
    {
        if (_modelDialogRow is null) return;
        _modelDialogRow.CancelEdit();
        ModelDialogRow = null;
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
        PresetProviders.Clear();
        ModelChoices.Clear();

        foreach (var info in await _assistant.ListProvidersAsync().ConfigureAwait(true))
        {
            var row = new AiProviderRow(info);
            Providers.Add(row);
            if (info.Source == AiProviderSource.Preset) PresetProviders.Add(row);
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

    /// <summary>按接入格式新建自定义服务商（可建多个）：Id 由 AI 层生成，建成即选中并进右侧表单。</summary>
    public async Task AddCustomProviderAsync(AiProtocol protocol)
    {
        await GuardAsync(async () =>
        {
            var created = await _assistant.CreateCustomProviderAsync(protocol).ConfigureAwait(true);
            await RefreshProvidersAsync().ConfigureAwait(true);
            SelectedProvider = Providers.FirstOrDefault(p => p.Info.Id == created.Id) ?? SelectedProvider;
        }).ConfigureAwait(true);
    }

    /// <summary>删除**自定义**服务商（连同模型与密钥）；预设不可删（界面按钮已按 <see cref="CanDeleteProvider"/> 收起）。</summary>
    public async Task DeleteProviderAsync()
    {
        if (SelectedProvider is null || !CanDeleteProvider) return;
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

    /// <summary>连通性测试（结果三态 + 耗时；徽标就地更新）。地址空 = 就地提示，不发请求（与拉取模型同口径）。</summary>
    public async Task TestAsync()
    {
        if (SelectedProvider is null) return;
        if (string.IsNullOrWhiteSpace(BaseUrlInput))
        {
            TestStatusKey = "ai.err.invalidInput";
            TestDetail = LocValue.Of("ai.settings.baseUrlRequired");
            return;
        }
        var id = SelectedProvider.Info.Id;
        TestStatusKey = "ai.status.testing";
        TestDetail = LocValue.Empty;
        await GuardAsync(async () =>
        {
            await SaveProviderAsync().ConfigureAwait(true);   // 地址草稿先落地（否则测的是旧地址）
            var result = await _assistant.TestConnectivityAsync(id).ConfigureAwait(true);
            TestStatusKey = LinkPocket.Views.AiKeyMap.Status(result.Status);
            TestDetail = result.ErrorCode is { } code
                ? Loc.K("ai.settings.test.detailErr", LocValue.Of(LinkPocket.Views.AiKeyMap.Error(code)),
                    result.ElapsedMs)
                : Loc.K("ai.settings.test.detail", result.ElapsedMs, result.ModelCount ?? 0);
            await RefreshProvidersAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 拉取模型列表（不支持 → 引导手填；新模型默认未启用）。
    /// **地址是必填的存在前提**：新建的自定义服务商地址为空时，先就地提示而不是发一个不可能成功的请求
    /// （空地址会一路走到 HTTP 层 —— 那层现在也会给稳定错误码，但让用户在源头看到原因更好）。
    /// **地址是输入框草稿**：这里会先把它落盘，否则用户改了地址没保存就点拉取，请求打的是旧地址。
    /// </summary>
    public async Task FetchModelsAsync()
    {
        if (SelectedProvider is null) return;
        var id = SelectedProvider.Info.Id;
        if (string.IsNullOrWhiteSpace(BaseUrlInput))
        {
            TestStatusKey = "ai.err.invalidInput";
            TestDetail = LocValue.Of("ai.settings.baseUrlRequired");
            return;
        }
        await GuardAsync(async () =>
        {
            await SaveProviderAsync().ConfigureAwait(true);   // 显示名 / 地址 / 接入格式的草稿先落地
            await _assistant.RefreshModelsAsync(id);          // 地址非法时上面已就地报错并返回，这里是有效地址
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

    /// <summary>保存弹窗里的模型（添加 = 新模型；配置 = 覆盖既有模型的能力声明）。
    /// 模型 ID 必填；数字框留空 = 未声明（按保守缺省）；非法值就地报错、不发保存（拿不准就报错，不猜意图）。</summary>
    public async Task SaveModelDialogAsync()
    {
        if (ModelDialogRow is not { } row) return;
        var modelId = row.ModelIdInput.Trim();
        if (modelId.Length == 0)
        {
            row.ErrorKey = "ai.settings.model.idRequired";
            return;
        }
        if (!TryParseCapability(row.ContextWindowInput, out var contextWindow)
            || !TryParseCapability(row.MaxOutputTokensInput, out var maxOutputTokens))
        {
            row.ErrorKey = "ai.settings.model.invalidNumber";
            return;
        }

        row.ErrorKey = "";
        try
        {
            await _assistant.SaveModelAsync(new AiModelDraft(row.ProviderId, modelId, modelId,
                row.Model.Enabled, contextWindow, maxOutputTokens, row.SupportsTools, row.SupportsStreaming,
                new AiModelInputFormat(true, row.SupportsImage, row.SupportsVideo, row.SupportsPdf),
                row.SupportsJsonSchemaOutput, row.SupportsNativeWebSearch, row.SupportsMidConversationSystem,
                ToReasoning(row)))
                .ConfigureAwait(true);
            CancelModelDialog();
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

    /// <summary>接入格式的文案键（模板卡上的副行）。</summary>
    public string ProtocolKey => LinkPocket.Views.AiKeyMap.Protocol(Info.Protocol);

    public void Update(AiProviderInfo next) => Info = next;
}

/// <summary>模型行（**一行读数**：启用开关 + 模型 ID + 上下文窗口徽标 + 来源·工具；
/// 配置编辑在弹窗里，行不随模型数量或配置项变长）。</summary>
public sealed class AiModelRow : INotifyPropertyChanged
{
    private string _modelIdInput = "";
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

    /// <summary>「添加模型」弹窗的空草稿行：ID 待填、能力按保守缺省（工具与流式按支持，其余未声明）。</summary>
    public static AiModelRow NewDraft(string providerId)
        => new(providerId, new AiModelInfo("", "", AiModelSource.Manual, Enabled: true,
            ContextWindow: null, MaxOutputTokens: null, SupportsTools: true, SupportsStreaming: true));

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

    /// <summary>弹窗里的模型 ID 草稿（添加时待填、配置时只读展示既有 ID）。</summary>
    public string ModelIdInput
    {
        get => _modelIdInput;
        set => Set(ref _modelIdInput, value, nameof(ModelIdInput));
    }

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

    /// <summary>弹窗打开时初始化草稿（输入从当前模型能力取；数字框留空 = 未声明；显式保存，不做失焦提交）。</summary>
    public void BeginEdit()
    {
        ModelIdInput = Model.Id;
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
    }

    /// <summary>弹窗关闭：草稿丢弃（下次打开重新取初值）。</summary>
    public void CancelEdit() => ErrorKey = "";

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

