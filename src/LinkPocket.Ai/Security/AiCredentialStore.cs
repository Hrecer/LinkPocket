using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>
/// API Key 凭据库（**唯一实现**）：<c>{数据根}/credentials.json</c>，逐键密文（base64）落盘。
/// 口径（功能书 §4.4）：明文**绝不落盘、绝不进日志与审计正文、不进备份**；
/// 文件损坏 / 版本不符 / 解密失败一律**拒绝覆盖**并如实报错（<c>LP.AI.012</c>）——
/// 不静默清空、不重建、不复用旧值。
/// </summary>
public sealed class AiCredentialStore
{
    /// <summary>文件形状（版本链：加字段不升版本；改结构才升，旧版本一律视为损坏拒绝）。</summary>
    private sealed record FileModel(int Version, Dictionary<string, string> Keys);

    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ISecretCipher _cipher;
    private readonly object _gate = new();

    /// <param name="dataRoot">AI 数据根（宿主传 <c>{程序目录}/ai</c>；测试传临时目录）。</param>
    public AiCredentialStore(string dataRoot, ISecretCipher cipher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(cipher);
        _path = Path.Combine(dataRoot, "credentials.json");
        _cipher = cipher;
    }

    public string FilePath => _path;

    /// <summary>写入 / 替换（返回掩码供界面显示）。空密钥请改用 <see cref="Remove"/>。</summary>
    public string Set(string providerId, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var plain = apiKey.Trim();
        lock (_gate)
        {
            var model = Load();
            model.Keys[providerId] = _cipher.Protect(plain);
            Save(model);
        }
        return Mask(plain);
    }

    /// <summary>删除（不存在 = 幂等成功）。</summary>
    public void Remove(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        lock (_gate)
        {
            var model = Load();
            if (model.Keys.Remove(providerId)) Save(model);
        }
    }

    /// <summary>是否已配置（不触发解密）。</summary>
    public bool Has(string providerId)
    {
        lock (_gate) return Load().Keys.ContainsKey(providerId);
    }

    /// <summary>掩码（前 4 + 后 4；短密钥整体打码）；未配置 → null。解密失败映射 <c>LP.AI.012</c>。</summary>
    public string? MaskedOf(string providerId)
    {
        lock (_gate)
        {
            var model = Load();
            if (!model.Keys.TryGetValue(providerId, out var encrypted)) return null;
            return Mask(Unprotect(providerId, encrypted));
        }
    }

    /// <summary>取明文——**仅供传输层拼 HTTP 头**；绝不上屏、绝不进日志/审计。未配置 → null。</summary>
    public string? TryGetPlaintext(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        lock (_gate)
        {
            var model = Load();
            return model.Keys.TryGetValue(providerId, out var encrypted)
                ? Unprotect(providerId, encrypted)
                : null;
        }
    }

    /// <summary>掩码口径（唯一实现）：≥12 位才露前 4 后 4，否则整体打码。</summary>
    public static string Mask(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        var text = apiKey.Trim();
        return text.Length >= 12 ? $"{text[..4]}…{text[^4..]}" : new string('*', text.Length);
    }

    private FileModel Load()
    {
        var text = AtomicFile.TryReadAllText(_path);
        if (text is null)
            return new FileModel(CurrentVersion, new Dictionary<string, string>(StringComparer.Ordinal));

        FileModel? model;
        try
        {
            model = JsonSerializer.Deserialize<FileModel>(text, JsonOptions);
        }
        catch (JsonException)
        {
            throw Corrupt("credential file is not valid JSON");
        }

        if (model is null || model.Keys is null || model.Version != CurrentVersion)
            throw Corrupt($"credential file version/format mismatch (expected version {CurrentVersion})");
        return model;
    }

    private void Save(FileModel model)
        => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(model, JsonOptions));

    private string Unprotect(string providerId, string encrypted)
    {
        try
        {
            return _cipher.Unprotect(encrypted);
        }
        catch (Exception ex) when (ex is not AiException)
        {
            throw new AiException(AiErrors.Of(
                AiErrors.CredentialStoreFailed,
                "stored credential cannot be decrypted (different Windows user/machine or corrupted file); re-enter the API key",
                details: JsonSerializer.SerializeToElement(new { provider_id = providerId })));
        }
    }

    private AiException Corrupt(string reason)
        => new(AiErrors.Of(
            AiErrors.CredentialStoreFailed,
            $"{reason}; refusing to overwrite the existing file (delete it manually to reset)"));
}
