using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkPocket.Data;

namespace LinkPocket.Modules.Backup;

/// <summary>
/// .lpbackup v2 文件读写（internal，现状算法平移）。
///
/// <para><b>身份模型</b>：文件夹在备份文件里用导入期临时 key（"f1"、"f2"）表达层级，
/// key 只活在备份文件内、绝不写库——防撞 + 免疫重名/含「&gt;」文件夹名。</para>
///
/// <para><b>范围</b>：只备份活数据（lists / links）；回收站不备份。</para>
///
/// <para><b>完整性</b>：manifest 记录 data.json 的 SHA-256，导入前先验；版本不符明确拒绝（只认 "2" 前缀）。</para>
/// </summary>
internal static class BackupIO
{
    // —— JSON 模型（与既有格式逐字段一致）——

    internal sealed class BackupManifest
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "2.0";
        [JsonPropertyName("app_version")] public string? AppVersion { get; set; }
        [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        [JsonPropertyName("created_by")] public string CreatedBy { get; set; } = "LinkPocket";

        /// <summary>data.json（UTF-8 字节）的 SHA-256，导入前校验完整性。</summary>
        [JsonPropertyName("data_sha256")] public string? DataSha256 { get; set; }
        [JsonPropertyName("statistics")] public BackupStatistics Statistics { get; set; } = new();
    }

    internal sealed class BackupStatistics
    {
        [JsonPropertyName("total_folders")] public int TotalFolders { get; set; }
        [JsonPropertyName("total_links")] public int TotalLinks { get; set; }
        [JsonPropertyName("total_favicons")] public int TotalFavicons { get; set; }
    }

    internal sealed class BackupFolderData
    {
        [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
        [JsonPropertyName("parent")] public string? Parent { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
        [JsonPropertyName("visit_count")] public int VisitCount { get; set; }
        [JsonPropertyName("last_visited_at")] public string? LastVisitedAt { get; set; }
        [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    internal sealed class BackupLinkData
    {
        [JsonPropertyName("folder")] public string? Folder { get; set; }
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("favicon_url")] public string? FaviconUrl { get; set; }
        [JsonPropertyName("visit_count")] public int VisitCount { get; set; }
        [JsonPropertyName("is_important")] public bool IsImportant { get; set; }
        [JsonPropertyName("last_visited_at")] public string? LastVisitedAt { get; set; }
        [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    internal sealed class BackupData
    {
        [JsonPropertyName("folders")] public List<BackupFolderData> Folders { get; set; } = [];
        [JsonPropertyName("links")] public List<BackupLinkData> Links { get; set; } = [];
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // —— 打包（导出）——

    /// <summary>把全量文件夹/链接打包为 .lpbackup（含引用到的图标缓存文件）。</summary>
    public static async Task PackAsync(
        IReadOnlyList<Folder> folders, IReadOnlyList<Link> links,
        string outputPath, CancellationToken ct)
    {
        var faviconFiles = new ConcurrentDictionary<string, string>();

        // 临时 key：只存在于备份文件内，绝不写库
        var folderKeyById = new Dictionary<string, string>();
        for (var i = 0; i < folders.Count; i++)
            folderKeyById[folders[i].FolderId] = $"f{i + 1}";

        var backupData = new BackupData();
        foreach (var folder in folders)
        {
            backupData.Folders.Add(new BackupFolderData
            {
                Key = folderKeyById[folder.FolderId],
                Parent = folder.ParentId != null ? folderKeyById.GetValueOrDefault(folder.ParentId) : null,
                Name = folder.Name,
                Description = folder.Description,
                SortOrder = folder.SortOrder,
                VisitCount = folder.VisitCount,
                LastVisitedAt = FormatUtcNullable(folder.LastVisitedAt),
                CreatedAt = FormatUtc(folder.CreatedAt),
                UpdatedAt = FormatUtc(folder.UpdatedAt),
            });
        }

        foreach (var link in links)
        {
            backupData.Links.Add(new BackupLinkData
            {
                Folder = link.ListId != null ? folderKeyById.GetValueOrDefault(link.ListId) : null,
                Url = link.Url,
                Title = link.Title,
                Description = link.Description,
                FaviconUrl = link.FaviconUrl,
                VisitCount = link.VisitCount,
                IsImportant = link.IsImportant,
                LastVisitedAt = FormatUtcNullable(link.LastVisitedAt),
                CreatedAt = FormatUtc(link.CreatedAt),
                UpdatedAt = FormatUtc(link.UpdatedAt),
            });

            if (!string.IsNullOrWhiteSpace(link.FaviconUrl))
                CollectFaviconFile(link.FaviconUrl, faviconFiles);
        }

        // data.json 先在内存成型 → 算 SHA-256 → 写 manifest（先 data 后 manifest，哈希才对得上）
        var dataBytes = JsonSerializer.SerializeToUtf8Bytes(backupData, JsonOptions);
        var manifest = new BackupManifest
        {
            DataSha256 = Convert.ToHexString(SHA256.HashData(dataBytes)).ToLowerInvariant(),
            Statistics = new BackupStatistics
            {
                TotalFolders = backupData.Folders.Count,
                TotalLinks = backupData.Links.Count,
                TotalFavicons = faviconFiles.Count,
            },
        };

        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);
        var dataEntry = archive.CreateEntry("data.json");
        await using (var stream = dataEntry.Open())
            await stream.WriteAsync(dataBytes, ct);

        var manifestEntry = archive.CreateEntry("manifest.json");
        await using (var writer = new StreamWriter(manifestEntry.Open()))
            await writer.WriteAsync(JsonSerializer.Serialize(manifest, JsonOptions));

        if (faviconFiles.Count > 0)
        {
            foreach (var kvp in faviconFiles)
            {
                var faviconEntry = archive.CreateEntry($"favicons/{Path.GetFileName(kvp.Value)}");
                await using var sourceStream = File.OpenRead(kvp.Value);
                await using var targetStream = faviconEntry.Open();
                await sourceStream.CopyToAsync(targetStream, ct);
            }
        }
    }

    // —— 读取（导入/检查共用）——

    /// <summary>读取结果：manifest + data.json 字节 + 图标文件字节表。</summary>
    internal sealed class BackupFile
    {
        public BackupManifest Manifest = new();
        public byte[] DataBytes = [];
        public BackupData Data = new();
        public Dictionary<string, byte[]>? Favicons;
        public List<string> Errors = [];
        public bool Valid => Errors.Count == 0;
    }

    /// <summary>读 zip + 版本检查 + SHA-256 校验（不写任何库数据）。</summary>
    public static async Task<BackupFile> ReadAsync(string filePath, CancellationToken ct)
    {
        var file = new BackupFile();

        if (!File.Exists(filePath))
        {
            file.Errors.Add("备份文件不存在");
            return file;
        }

        try
        {
        using (var archive = ZipFile.OpenRead(filePath))
        {
            var manifestEntry = archive.GetEntry("manifest.json");
            var dataEntry = archive.GetEntry("data.json");
            if (manifestEntry == null || dataEntry == null)
            {
                file.Errors.Add("无效的备份文件：缺少 manifest.json 或 data.json");
                return file;
            }

            using (var stream = manifestEntry.Open())
            using (var reader = new StreamReader(stream))
                file.Manifest = JsonSerializer.Deserialize<BackupManifest>(await reader.ReadToEndAsync(ct)) ?? new BackupManifest();

            using (var stream = dataEntry.Open())
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms, ct);
                file.DataBytes = ms.ToArray();
            }

            var faviconEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("favicons/") && !e.FullName.EndsWith("/"))
                .ToList();
            if (faviconEntries.Count > 0)
            {
                file.Favicons = [];
                foreach (var faviconEntry in faviconEntries)
                {
                    await using var stream = faviconEntry.Open();
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, ct);
                    file.Favicons[Path.GetFileName(faviconEntry.FullName)] = ms.ToArray();
                }
            }
        }

        }
        catch (Exception ex)
        {
            file.Errors.Add("备份文件无法读取（可能已损坏）：" + ex.Message);
            return file;
        }

        // 版本检查：只认当前格式，不做任何旧格式兼容
        var version = file.Manifest.Version?.Trim() ?? "";
        if (!version.StartsWith("2"))
        {
            file.Errors.Add($"不支持的备份版本：「{version}」，请升级应用后再试");
            return file;
        }

        // 完整性校验：data.json 必须与 manifest 记录的 SHA-256 一致
        var expected = file.Manifest.DataSha256?.Trim();
        if (string.IsNullOrEmpty(expected))
        {
            file.Errors.Add("备份缺少完整性校验信息（data_sha256），文件可能不完整");
            return file;
        }

        var actual = Convert.ToHexString(SHA256.HashData(file.DataBytes)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            file.Errors.Add("备份完整性校验失败：文件已被修改或损坏");
            return file;
        }

        try
        {
            // SHA-256 通过但 data.json 不是合法 JSON（手工构造/罕见损坏）也必须降级为明确错误，而非内部错误
            file.Data = JsonSerializer.Deserialize<BackupData>(file.DataBytes, JsonOptions) ?? new BackupData();
        }
        catch (Exception ex)
        {
            file.Errors.Add("备份数据解析失败（data.json 不是合法 JSON）：" + ex.Message);
        }
        return file;
    }

    // —— 图标缓存文件搬运（与既有实现同口径；目录约定与 FaviconCache 一致）——

    private static readonly string CacheDirectory = Path.Combine(AppContext.BaseDirectory, "favicons");

    private static void CollectFaviconFile(string faviconUrl, ConcurrentDictionary<string, string> faviconFiles)
    {
        try
        {
            var resolvedUrl = ResolveFaviconUrl(faviconUrl);
            if (string.IsNullOrWhiteSpace(resolvedUrl)) return;

            var cacheFilePath = GetCacheFilePath(resolvedUrl);
            if (!File.Exists(cacheFilePath)) return;

            faviconFiles.TryAdd(resolvedUrl, cacheFilePath);
        }
        catch
        {
            // 图标缺失不阻断备份
        }
    }

    /// <summary>恢复图标缓存文件（已存在则跳过；包内没有对应文件时保留原地址）。</summary>
    public static async Task<string?> RestoreFaviconFileAsync(string? originalUrl, Dictionary<string, byte[]>? faviconData, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(originalUrl)) return originalUrl;

        try
        {
            var resolvedUrl = ResolveFaviconUrl(originalUrl);
            var cacheFilePath = GetCacheFilePath(resolvedUrl);

            if (File.Exists(cacheFilePath)) return resolvedUrl;
            if (faviconData == null) return originalUrl;

            var fileName = Path.GetFileName(cacheFilePath);
            if (!faviconData.TryGetValue(fileName, out var bytes)) return originalUrl;

            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(cacheFilePath, bytes, ct);
            return resolvedUrl;
        }
        catch
        {
            return originalUrl;
        }
    }

    internal static string ResolveFaviconUrl(string? originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl)) return string.Empty;

        var ext = GetExtensionFromUrl(originalUrl).ToLowerInvariant();
        if (ext == ".svg")
        {
            try
            {
                var uri = new Uri(originalUrl);
                return $"{uri.Scheme}://{uri.Host}/favicon.ico";
            }
            catch
            {
                return originalUrl;
            }
        }

        return originalUrl;
    }

    private static string GetCacheFilePath(string faviconUrl)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(faviconUrl)));
        var ext = GetExtensionFromUrl(faviconUrl);
        return Path.Combine(CacheDirectory, $"{hash}{ext}");
    }

    private static string GetExtensionFromUrl(string url)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (ext is ".png" or ".jpg" or ".jpeg" or ".ico" or ".gif" or ".bmp" or ".webp" or ".svg")
                return ext == ".jpeg" ? ".jpg" : ext;
        }
        catch
        {
            // 非法地址回落 .ico
        }

        return ".ico";
    }

    // —— 时间戳（与既有实现一致：统一 UTC 序列化 / 显式按 UTC 解析）——

    internal static string FormatUtc(DateTime value)
        => ToUtc(value).ToString("yyyy-MM-ddTHH:mm:ssZ");

    internal static string? FormatUtcNullable(DateTime? value) => value == null ? null : FormatUtc(value.Value);

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    internal static DateTime ParseDateTime(string dateTimeStr)
        => DateTimeOffset.TryParse(dateTimeStr,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dto)
            ? dto.UtcDateTime
            : DateTime.UtcNow;

    internal static DateTime? ParseNullableDateTime(string? dateTimeStr)
        => string.IsNullOrWhiteSpace(dateTimeStr)
            ? null
            : DateTimeOffset.TryParse(dateTimeStr,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var dto)
                ? dto.UtcDateTime
                : null;
}
