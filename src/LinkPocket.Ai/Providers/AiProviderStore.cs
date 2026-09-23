using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// 服务商与模型配置存储（**唯一实现**）：<c>{数据根}/providers.json</c>。
/// 口径（功能书 §4.3）：
/// <list type="bullet">
/// <item><b>草稿宽松</b>：半填也能保存（设置页允许边填边存），只拒空 Id；</item>
/// <item><b>准入严格</b>：完整性由投影的 <see cref="AiProviderInfo.Issues"/> 如实列出（空 = 可进运行期使用），不静默兜底；</item>
/// <item>预设模板 = 数据、用户记录 = 覆盖层：删除用户记录即回到出厂模板；</item>
/// <item>文件损坏 / 版本不符 → <b>拒绝覆盖</b>并报 <c>LP.AI.015</c>。</item>
/// </list>
/// 自定义服务商：Id 与预设不撞名即可（与预设同名 = 覆盖该预设）。
/// </summary>
public sealed class AiProviderStore
{
    private sealed record ModelRecord(
        string Id, string DisplayName, bool Enabled, int? ContextWindow, int? MaxOutputTokens,
        bool SupportsTools, bool SupportsStreaming, AiModelSource Source);

    private sealed record ProviderRecord(
        string Id, string DisplayName, AiProtocol Protocol, string BaseUrl, bool Enabled, bool IsLocal,
        List<ModelRecord>? Models);

    /// <summary>最近一次连通性测试结果（ErrorCode = null 表示成功）。</summary>
    private sealed record TestRecord(string? ErrorCode, DateTimeOffset At);

    private sealed record FileModel(int Version, List<ProviderRecord> Providers, Dictionary<string, TestRecord>? Tests);

    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();

    /// <param name="dataRoot">AI 数据根（宿主传 <c>{程序目录}/ai</c>；测试传临时目录）。</param>
    public AiProviderStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _path = Path.Combine(dataRoot, "providers.json");
    }

    public string FilePath => _path;

    /// <summary>服务商清单：预设模板 ⊕ 用户覆盖层（预设按目录顺序在前，自定义在后）。</summary>
    public IReadOnlyList<AiProviderInfo> List(AiCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        lock (_gate)
        {
            var file = Load();
            var list = new List<AiProviderInfo>();
            foreach (var template in AiProviderCatalog.Templates)
                list.Add(Project(template, RecordOf(file, template.Id), file.Tests, credentials));
            foreach (var record in file.Providers.Where(p => !AiProviderCatalog.IsPreset(p.Id)))
                list.Add(Project(null, record, file.Tests, credentials));
            return list;
        }
    }

    /// <summary>保存服务商草稿（宽松：半填照存；空 Id 拒绝）。返回保存后的投影。</summary>
    public AiProviderInfo SaveProvider(AiProviderDraft draft, AiCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Id);
        lock (_gate)
        {
            var file = Load();
            var existing = RecordOf(file, draft.Id);
            var template = AiProviderCatalog.Find(draft.Id);
            // 空名字 = "用缺省名"（不视为改名）：回落既有记录名 → 模板英文名 → Id
            var name = string.IsNullOrWhiteSpace(draft.DisplayName)
                ? existing?.DisplayName ?? template?.DisplayName ?? draft.Id.Trim()
                : draft.DisplayName.Trim();
            var merged = new ProviderRecord(
                draft.Id.Trim(),
                name,
                draft.Protocol,
                draft.BaseUrl?.Trim() ?? "",
                draft.Enabled,
                draft.IsLocal,
                existing?.Models);
            Put(file, merged);
            Save(file);
            return Project(template, merged, file.Tests, credentials);
        }
    }

    /// <summary>删除：预设 → 回到出厂模板（连同测试记录）；自定义 → 整体移除。凭据由界面显式删除。</summary>
    public void DeleteProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        lock (_gate)
        {
            var file = Load();
            var removed = file.Providers.RemoveAll(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
            var testRemoved = file.Tests?.Remove(providerId) ?? false;
            if (removed > 0 || testRemoved) Save(file);
        }
    }

    /// <summary>新增 / 更新一个模型（更新保留原来源；新模型来源 = 手填）。</summary>
    public AiProviderInfo SaveModel(AiModelDraft draft, AiCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Id);
        lock (_gate)
        {
            var file = Load();
            var template = AiProviderCatalog.Find(draft.ProviderId);
            var record = RecordOf(file, draft.ProviderId);
            if (template is null && record is null)
                throw UnknownProvider(draft.ProviderId);

            var models = MaterializeModels(template, record);
            var index = models.FindIndex(m => string.Equals(m.Id, draft.Id, StringComparison.Ordinal));
            var source = index >= 0 ? models[index].Source : AiModelSource.Manual;
            var updated = new ModelRecord(
                draft.Id.Trim(),
                string.IsNullOrWhiteSpace(draft.DisplayName) ? draft.Id.Trim() : draft.DisplayName.Trim(),
                draft.Enabled, draft.ContextWindow, draft.MaxOutputTokens,
                draft.SupportsTools, draft.SupportsStreaming, source);
            if (index >= 0) models[index] = updated;
            else models.Add(updated);

            var target = record ?? FromTemplate(template!);
            Put(file, target with { Models = models });
            Save(file);
            return Project(template, RecordOf(file, draft.ProviderId)!, file.Tests, credentials);
        }
    }

    /// <summary>并入「拉取模型列表」的结果：新 ID 落为 fetched 且**默认未启用**（用户勾选后再启用）。</summary>
    public AiProviderInfo MergeFetchedModels(string providerId, IReadOnlyList<string> modelIds, AiCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        lock (_gate)
        {
            var file = Load();
            var template = AiProviderCatalog.Find(providerId);
            var record = RecordOf(file, providerId);
            if (template is null && record is null)
                throw UnknownProvider(providerId);

            var models = MaterializeModels(template, record);
            foreach (var id in modelIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()))
            {
                if (models.Any(m => string.Equals(m.Id, id, StringComparison.Ordinal))) continue;
                models.Add(new ModelRecord(id, id, Enabled: false, null, null,
                    SupportsTools: true, SupportsStreaming: true, AiModelSource.Fetched));
            }

            Put(file, (record ?? FromTemplate(template!)) with { Models = models });
            Save(file);
            return Project(template, RecordOf(file, providerId)!, file.Tests, credentials);
        }
    }

    /// <summary>记录最近一次连通性测试结果（<paramref name="errorCode"/> = null 表示成功）——徽标数据来源。</summary>
    public AiProviderInfo SetLastTestResult(string providerId, string? errorCode, AiCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        lock (_gate)
        {
            var file = Load();
            var record = RecordOf(file, providerId);
            var template = AiProviderCatalog.Find(providerId);
            if (template is null && record is null)
                throw UnknownProvider(providerId);

            var tests = file.Tests ?? new Dictionary<string, TestRecord>(StringComparer.Ordinal);
            tests[providerId] = new TestRecord(errorCode, DateTimeOffset.UtcNow);
            Save(file with { Tests = tests });
            return Project(template, RecordOf(file, providerId), tests, credentials);
        }
    }

    /// <summary>清掉最近一次连通性测试记录（密钥变更后徽标回到"已配置未验证"）。</summary>
    public AiProviderInfo ClearLastTestResult(string providerId, AiCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        lock (_gate)
        {
            var file = Load();
            var template = AiProviderCatalog.Find(providerId);
            var record = RecordOf(file, providerId);
            if (template is null && record is null)
                throw UnknownProvider(providerId);

            if (file.Tests?.Remove(providerId) == true) Save(file);
            return Project(template, record, file.Tests, credentials);
        }
    }

    // ── 内部 ────────────────────────────────────────────────

    private static ProviderRecord FromTemplate(AiProviderTemplate template)
        => new(template.Id, template.DisplayName, template.Protocol, template.BaseUrl,
            Enabled: true, template.IsLocal, Models: null);

    private static ProviderRecord? RecordOf(FileModel file, string providerId)
        => file.Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));

    private static void Put(FileModel file, ProviderRecord record)
    {
        var index = file.Providers.FindIndex(p => string.Equals(p.Id, record.Id, StringComparison.Ordinal));
        if (index >= 0) file.Providers[index] = record;
        else file.Providers.Add(record);
    }

    private static List<ModelRecord> MaterializeModels(AiProviderTemplate? template, ProviderRecord? record)
    {
        if (record?.Models is { } owned) return [.. owned];
        if (template is null) return [];
        return [.. template.PresetModelIds.Select(id =>
            new ModelRecord(id, id, Enabled: true, null, null, SupportsTools: true, SupportsStreaming: true,
                AiModelSource.Preset))];
    }

    private static AiProviderInfo Project(AiProviderTemplate? template, ProviderRecord? record,
        IReadOnlyDictionary<string, TestRecord>? tests, AiCredentialStore credentials)
    {
        var id = record?.Id ?? template!.Id;
        // 显示名：记录名优先（自建 / 改名都是用户数据）；无记录名则回落模板英文名。
        // 改名 = 记录名 ≠ 模板英文名 → 不再走文案键（否则会把用户输入当成需要取词的界面文案）。
        var renamed = record is not null && template is not null
            && !string.IsNullOrWhiteSpace(record.DisplayName)
            && !string.Equals(record.DisplayName, template.DisplayName, StringComparison.Ordinal);
        var displayName = !string.IsNullOrWhiteSpace(record?.DisplayName)
            ? record!.DisplayName
            : template?.DisplayName ?? id;
        var displayNameKey = template is not null && !renamed ? template.DisplayNameKey : null;
        var protocol = record?.Protocol ?? template!.Protocol;
        var baseUrl = record?.BaseUrl ?? template!.BaseUrl;
        var isLocal = record?.IsLocal ?? template!.IsLocal;

        var models = (record?.Models is { } owned
                ? owned.Select(ToInfo)
                : (template?.PresetModelIds ?? []).Select(mid =>
                    new AiModelInfo(mid, mid, AiModelSource.Preset, Enabled: true, null, null, true, true)))
            .ToArray();

        string? masked = null;
        string? credentialError = null;
        try
        {
            masked = credentials.MaskedOf(id);
        }
        catch (AiException ex)
        {
            credentialError = ex.Error.Code;   // 单条凭据解不开不拖垮整个列表：如实投影成该服务商的失败态
        }

        var issues = new List<AiConfigIssue>();
        if (string.IsNullOrWhiteSpace(displayName)) issues.Add(new("display_name", AiConfigIssueCodes.Required));
        if (!IsHttpUrl(baseUrl)) issues.Add(new("base_url", AiConfigIssueCodes.InvalidUrl));
        if (!isLocal && masked is null && credentialError is null) issues.Add(new("api_key", AiConfigIssueCodes.ApiKeyMissing));
        if (models.All(m => !m.Enabled)) issues.Add(new("models", AiConfigIssueCodes.NoEnabledModel));

        // 凭据存在性：解不开也算"已配置"（如实：文件里有，只是不可用；失败原因由 Status/StatusErrorCode 说明）
        var hasKey = masked is not null || credentialError is not null;
        var (status, statusError) = ResolveStatus(issues, credentialError, tests, id);
        return new AiProviderInfo(
            id, displayName, protocol, baseUrl,
            AiProviderCatalog.IsPreset(id) ? AiProviderSource.Preset : AiProviderSource.Custom,
            isLocal, record?.Enabled ?? true, hasKey, masked, status, statusError,
            template?.ApiKeyManagementUrl, template?.DocsUrl, models, issues, displayNameKey);
    }

    private static (AiProviderStatus Status, string? ErrorCode) ResolveStatus(
        List<AiConfigIssue> issues, string? credentialError, IReadOnlyDictionary<string, TestRecord>? tests, string id)
    {
        if (issues.Count > 0) return (AiProviderStatus.NotConfigured, null);
        if (credentialError is not null) return (AiProviderStatus.Failed, credentialError);
        if (tests is not null && tests.TryGetValue(id, out var test))
            return test.ErrorCode is null
                ? (AiProviderStatus.Verified, null)
                : (AiProviderStatus.Failed, test.ErrorCode);
        return (AiProviderStatus.Configured, null);
    }

    private static AiModelInfo ToInfo(ModelRecord m)
        => new(m.Id, m.DisplayName, m.Source, m.Enabled, m.ContextWindow, m.MaxOutputTokens,
            m.SupportsTools, m.SupportsStreaming);

    private static bool IsHttpUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static AiException UnknownProvider(string providerId)
        => new(AiErrors.Of(
            AiErrors.ProviderNotConfigured,
            "unknown provider: the provider must be a preset template or an already saved custom provider",
            details: JsonSerializer.SerializeToElement(new { provider_id = providerId })));

    private FileModel Load()
    {
        var text = AtomicFile.TryReadAllText(_path);
        if (text is null)
            return new FileModel(CurrentVersion, [], null);

        FileModel? model;
        try
        {
            model = JsonSerializer.Deserialize<FileModel>(text, JsonOptions);
        }
        catch (JsonException)
        {
            throw Corrupt("provider config is not valid JSON");
        }

        if (model is null || model.Providers is null || model.Version != CurrentVersion)
            throw Corrupt($"provider config version/format mismatch (expected version {CurrentVersion})");
        return model;
    }

    private void Save(FileModel model)
        => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(model, JsonOptions));

    private static AiException Corrupt(string reason)
        => new(AiErrors.Of(
            AiErrors.AiDataStoreFailed,
            $"{reason}; refusing to overwrite the existing file (delete it manually to reset)"));
}
