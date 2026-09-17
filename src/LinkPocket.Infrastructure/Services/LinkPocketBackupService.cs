using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Services;

/// <summary>
/// .lpbackup 备份与恢复（当前唯一格式，2026-09-17 定稿）。
///
/// <para><b>身份模型</b>：文件夹在备份文件里用<b>导入期临时 key</b>（"f1"、"f2"）表达层级，
/// key 只活在备份文件内、<b>绝不写库</b>——备份不携带任何真实数据库 ID（防撞），也彻底免疫
/// 同父重名文件夹、名字含「&gt;」等路径身份碰撞问题。</para>
///
/// <para><b>范围</b>：只备份活数据（lists / links）；回收站不备份。</para>
///
/// <para><b>完整性</b>：manifest 记录 data.json 的 SHA-256，导入前先验；版本不符明确拒绝。
/// 不做任何旧格式兼容——旧格式从未发布，遇到即拒绝。</para>
///
/// <para><b>事务</b>：导入整体包一个事务，任何一步失败全部回滚，绝不留半截数据。</para>
/// </summary>
public class LinkPocketBackupService
{
    private readonly LinkPocketDbContext _db;

    public LinkPocketBackupService(LinkPocketDbContext db)
    {
        _db = db;
    }

    #region 数据模型（JSON 序列化）

    public class BackupManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "2.0";

        [JsonPropertyName("app_version")]
        public string? AppVersion { get; set; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        [JsonPropertyName("created_by")]
        public string CreatedBy { get; set; } = "LinkPocket";

        /// <summary>data.json（UTF-8 字节）的 SHA-256，导入前校验完整性。</summary>
        [JsonPropertyName("data_sha256")]
        public string? DataSha256 { get; set; }

        [JsonPropertyName("statistics")]
        public BackupStatistics Statistics { get; set; } = new();
    }

    public class BackupStatistics
    {
        [JsonPropertyName("total_folders")]
        public int TotalFolders { get; set; }

        [JsonPropertyName("total_links")]
        public int TotalLinks { get; set; }

        [JsonPropertyName("total_favicons")]
        public int TotalFavicons { get; set; }
    }

    public class BackupFolderData
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("parent")]
        public string? Parent { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("sort_order")]
        public int SortOrder { get; set; }

        [JsonPropertyName("visit_count")]
        public int VisitCount { get; set; }

        [JsonPropertyName("last_visited_at")]
        public string? LastVisitedAt { get; set; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    public class BackupLinkData
    {
        [JsonPropertyName("folder")]
        public string? Folder { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("favicon_url")]
        public string? FaviconUrl { get; set; }

        [JsonPropertyName("visit_count")]
        public int VisitCount { get; set; }

        [JsonPropertyName("is_important")]
        public bool IsImportant { get; set; }

        [JsonPropertyName("last_visited_at")]
        public string? LastVisitedAt { get; set; } = null;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    public class BackupData
    {
        [JsonPropertyName("folders")]
        public List<BackupFolderData> Folders { get; set; } = new();

        [JsonPropertyName("links")]
        public List<BackupLinkData> Links { get; set; } = new();
    }

    #endregion

    #region 导入期中间模型

    private class ImportFolder
    {
        public string Key = string.Empty;
        public string? ParentKey;
        public string Name = string.Empty;
        public string? Description;
        public int SortOrder;
        public int VisitCount;
        public DateTime? LastVisitedAt;
        public DateTime CreatedAt;
        public DateTime UpdatedAt;
    }

    private class ImportLink
    {
        public string? FolderKey;
        public string Url = string.Empty;
        public string? Title;
        public string? Description;
        public string? FaviconUrl;
        public int VisitCount;
        public bool IsImportant;
        public DateTime? LastVisitedAt;
        public DateTime CreatedAt;
        public DateTime UpdatedAt;
    }

    private class ImportContent
    {
        public List<ImportFolder> Folders { get; set; } = new();
        public List<ImportLink> Links { get; set; } = new();
    }

    #endregion

    #region 导出功能

    public async Task ExportAsync(string outputPath, IProgress<(string message, int current, int total)>? progress = null)
    {
        progress?.Report(("正在准备导出...", 0, 1));

        var faviconFiles = new ConcurrentDictionary<string, string>();

        var allFolders = await _db.Folders.OrderBy(f => f.CreatedAt).ThenBy(f => f.FolderId).ToListAsync();
        var allLinks = await _db.Links.OrderBy(l => l.CreatedAt).ThenBy(l => l.LinkId).ToListAsync();

        // 临时 key：只存在于备份文件内，绝不写库（防撞 + 免疫重名/含 ">" 文件夹名）
        var folderKeyById = new Dictionary<string, string>();
        for (var i = 0; i < allFolders.Count; i++)
            folderKeyById[allFolders[i].FolderId] = $"f{i + 1}";

        var backupData = new BackupData();

        progress?.Report(("正在处理文件夹...", 0, allFolders.Count));
        foreach (var folder in allFolders)
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
                UpdatedAt = FormatUtc(folder.UpdatedAt)
            });
        }

        progress?.Report(("正在处理书签...", 0, allLinks.Count));
        foreach (var link in allLinks)
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
                UpdatedAt = FormatUtc(link.UpdatedAt)
            });

            if (!string.IsNullOrWhiteSpace(link.FaviconUrl))
                CollectFaviconFile(link.FaviconUrl, faviconFiles);
        }

        // data.json 先在内存成型 → 算 SHA-256 → 写 manifest（先 data 后 manifest，哈希才对得上）
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        progress?.Report(($"正在打包文件 (图标: {faviconFiles.Count} 个)...", 0, 1));

        var dataBytes = JsonSerializer.SerializeToUtf8Bytes(backupData, jsonOptions);

        var manifest = new BackupManifest
        {
            DataSha256 = Convert.ToHexString(SHA256.HashData(dataBytes)).ToLowerInvariant(),
            Statistics = new BackupStatistics
            {
                TotalFolders = backupData.Folders.Count,
                TotalLinks = backupData.Links.Count,
                TotalFavicons = faviconFiles.Count
            }
        };

        using (var archive = System.IO.Compression.ZipFile.Open(outputPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var dataEntry = archive.CreateEntry("data.json");
            using (var stream = dataEntry.Open())
            {
                await stream.WriteAsync(dataBytes);
            }

            var manifestEntry = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open()))
            {
                await writer.WriteAsync(JsonSerializer.Serialize(manifest, jsonOptions));
            }

            if (faviconFiles.Count > 0)
            {
                var faviconIndex = 0;
                foreach (var kvp in faviconFiles)
                {
                    var fileName = Path.GetFileName(kvp.Value);
                    var faviconEntry = archive.CreateEntry($"favicons/{fileName}");

                    using (var sourceStream = File.OpenRead(kvp.Value))
                    using (var targetStream = faviconEntry.Open())
                    {
                        await sourceStream.CopyToAsync(targetStream);
                    }

                    faviconIndex++;
                    if (faviconIndex % 5 == 0 || faviconIndex == faviconFiles.Count)
                    {
                        progress?.Report(($"正在打包图标 ({faviconIndex}/{faviconFiles.Count})...", faviconIndex, faviconFiles.Count));
                    }
                }
            }
        }

        var total = manifest.Statistics.TotalFolders + manifest.Statistics.TotalLinks;
        progress?.Report(("导出完成！", total, total));
    }

    private static void CollectFaviconFile(string faviconUrl, ConcurrentDictionary<string, string> faviconFiles)
    {
        try
        {
            var resolvedUrl = FaviconStore.ResolveFaviconUrl(faviconUrl);
            if (string.IsNullOrWhiteSpace(resolvedUrl)) return;

            var cacheFilePath = FaviconStore.GetCacheFilePath(resolvedUrl);
            if (!File.Exists(cacheFilePath)) return;

            faviconFiles.TryAdd(resolvedUrl, cacheFilePath);
        }
        catch
        {
            // 图标缺失不阻断备份
        }
    }

    #endregion

    #region 导入功能

    public async Task<ImportResult> ImportAsync(string filePath, IProgress<(string message, int current, int total)>? progress = null)
    {
        var result = new ImportResult();

        if (!File.Exists(filePath))
        {
            result.Errors.Add("备份文件不存在");
            return result;
        }

        progress?.Report(("正在读取备份文件...", 0, 1));

        BackupManifest? manifest;
        byte[] dataBytes;
        Dictionary<string, byte[]>? faviconDataDict;

        using (var archive = System.IO.Compression.ZipFile.OpenRead(filePath))
        {
            var manifestEntry = archive.GetEntry("manifest.json");
            var dataEntry = archive.GetEntry("data.json");
            if (manifestEntry == null || dataEntry == null)
            {
                result.Errors.Add("无效的备份文件：缺少 manifest.json 或 data.json");
                return result;
            }

            using (var stream = manifestEntry.Open())
            using (var reader = new StreamReader(stream))
            {
                var json = await reader.ReadToEndAsync();
                manifest = JsonSerializer.Deserialize<BackupManifest>(json);
            }

            using (var stream = dataEntry.Open())
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                dataBytes = ms.ToArray();
            }

            var faviconEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("favicons/") && !e.FullName.EndsWith("/"))
                .ToList();
            if (faviconEntries.Count > 0)
            {
                faviconDataDict = new Dictionary<string, byte[]>();
                foreach (var faviconEntry in faviconEntries)
                {
                    using (var stream = faviconEntry.Open())
                    using (var ms = new MemoryStream())
                    {
                        await stream.CopyToAsync(ms);
                        faviconDataDict[Path.GetFileName(faviconEntry.FullName)] = ms.ToArray();
                    }
                }
            }
            else
            {
                faviconDataDict = null;
            }
        }

        // 版本检查：只认当前格式，不做任何旧格式兼容
        var version = manifest?.Version?.Trim() ?? "";
        if (!version.StartsWith("2"))
        {
            result.Errors.Add($"不支持的备份版本：「{version}」，请升级应用后再试");
            return result;
        }

        // 完整性校验：data.json 必须与 manifest 记录的 SHA-256 一致
        var expected = manifest!.DataSha256?.Trim();
        if (string.IsNullOrEmpty(expected))
        {
            result.Errors.Add("备份缺少完整性校验信息（data_sha256），文件可能不完整");
            return result;
        }

        progress?.Report(("正在校验备份完整性...", 0, 1));
        var actual = Convert.ToHexString(SHA256.HashData(dataBytes)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add("备份完整性校验失败：文件已被修改或损坏");
            return result;
        }

        var backupData = JsonSerializer.Deserialize<BackupData>(dataBytes);
        if (backupData == null)
        {
            result.Errors.Add("无法解析备份数据");
            return result;
        }

        var content = new ImportContent();

        foreach (var f in backupData.Folders ?? new List<BackupFolderData>())
        {
            content.Folders.Add(new ImportFolder
            {
                Key = f.Key,
                ParentKey = string.IsNullOrEmpty(f.Parent) ? null : f.Parent,
                Name = f.Name,
                Description = f.Description,
                SortOrder = f.SortOrder,
                VisitCount = f.VisitCount,
                LastVisitedAt = ParseNullableDateTime(f.LastVisitedAt),
                CreatedAt = ParseDateTime(f.CreatedAt),
                UpdatedAt = ParseDateTime(f.UpdatedAt)
            });
        }

        foreach (var l in backupData.Links ?? new List<BackupLinkData>())
        {
            content.Links.Add(new ImportLink
            {
                FolderKey = string.IsNullOrEmpty(l.Folder) ? null : l.Folder,
                Url = l.Url,
                Title = l.Title,
                Description = l.Description,
                FaviconUrl = l.FaviconUrl,
                VisitCount = l.VisitCount,
                IsImportant = l.IsImportant,
                LastVisitedAt = ParseNullableDateTime(l.LastVisitedAt),
                CreatedAt = ParseDateTime(l.CreatedAt),
                UpdatedAt = ParseDateTime(l.UpdatedAt)
            });
        }

        try
        {
            await InsertContentAsync(content, faviconDataDict, progress, result);
        }
        catch
        {
            // 事务已随 dispose 回滚，不留半截数据；异常交由上层提示
            throw;
        }

        progress?.Report((
            $"导入完成！文件夹 {result.FoldersCreated}，书签 {result.LinksCreated}",
            result.TotalItems, result.TotalItems));

        return result;
    }

    /// <summary>落库：整体一个事务，父先于子（按 key 深度拓扑排序），任何一步失败全部回滚。</summary>
    private async Task InsertContentAsync(
        ImportContent content,
        Dictionary<string, byte[]>? faviconDataDict,
        IProgress<(string message, int current, int total)>? progress,
        ImportResult result)
    {
        // —— 深度计算（带循环防护）：父先于子 ——
        var folderByKey = content.Folders.ToDictionary(f => f.Key);
        var depthMemo = new Dictionary<string, int>();

        int DepthOf(string? key)
        {
            if (key == null) return -1;
            var depth = 0;
            var cur = key;
            while (cur != null)
            {
                if (depthMemo.TryGetValue(cur, out var memo)) { depth += memo; break; }
                if (depth > content.Folders.Count + 1)
                    throw new Exception("备份文件的文件夹层级存在循环引用，无法导入");
                if (!folderByKey.TryGetValue(cur, out var node)) break;
                cur = node.ParentKey;
                depth++;
            }
            return depth;
        }

        var sortedFolders = content.Folders
            .Select((f, index) => (f, index, depth: DepthOf(f.ParentKey) + 1))
            .OrderBy(t => t.depth)
            .ThenBy(t => t.index)
            .ToList();

        var totalItems = content.Folders.Count + content.Links.Count;
        var processed = 0;

        await using var tx = await _db.Database.BeginTransactionAsync();

        progress?.Report(($"正在导入文件夹 (0/{sortedFolders.Count})...", 0, totalItems));

        var keyToFolderId = new Dictionary<string, string>();
        foreach (var (f, index, _) in sortedFolders)
        {
            var folder = new Folder
            {
                Name = f.Name,
                Description = f.Description,
                ParentId = f.ParentKey != null && keyToFolderId.TryGetValue(f.ParentKey, out var parentId)
                    ? parentId : null,
                LinkCount = 0,
                SortOrder = f.SortOrder,
                VisitCount = f.VisitCount,
                LastVisitedAt = f.LastVisitedAt,
                CreatedAt = f.CreatedAt,
                UpdatedAt = f.UpdatedAt
            };

            _db.Folders.Add(folder);
            keyToFolderId[f.Key] = folder.FolderId;

            result.FoldersCreated++;
            processed++;
            progress?.Report(($"正在导入文件夹: {f.Name} ({processed}/{totalItems})", processed, totalItems));
        }

        await _db.SaveChangesAsync();

        progress?.Report(($"正在导入书签 (0/{content.Links.Count})...", processed, totalItems));

        foreach (var l in content.Links)
        {
            string? resolvedFaviconUrl = null;
            if (!string.IsNullOrWhiteSpace(l.FaviconUrl))
                resolvedFaviconUrl = await RestoreFaviconFile(l.FaviconUrl, faviconDataDict);

            var link = new Link
            {
                Url = l.Url,
                Title = l.Title,
                Description = l.Description,
                FaviconUrl = resolvedFaviconUrl,
                ListId = l.FolderKey != null && keyToFolderId.TryGetValue(l.FolderKey, out var listId)
                    ? listId : null,
                VisitCount = l.VisitCount,
                IsImportant = l.IsImportant,
                LastVisitedAt = l.LastVisitedAt,
                CreatedAt = l.CreatedAt,
                UpdatedAt = l.UpdatedAt
            };

            _db.Links.Add(link);
            result.LinksCreated++;
            processed++;
            if (processed % 10 == 0 || processed == totalItems)
                progress?.Report(($"正在导入书签: {l.Title ?? l.Url} ({processed}/{totalItems})", processed, totalItems));
        }

        await _db.SaveChangesAsync();

        // 直接子链接计数刷新（与既有实现同口径）
        foreach (var folder in _db.Folders)
        {
            folder.UpdateLinkCount(_db);
        }
        await _db.SaveChangesAsync();

        await tx.CommitAsync();
    }

    private static async Task<string?> RestoreFaviconFile(string originalUrl, Dictionary<string, byte[]>? faviconDataDict)
    {
        if (string.IsNullOrWhiteSpace(originalUrl))
            return originalUrl;

        try
        {
            var resolvedUrl = FaviconStore.ResolveFaviconUrl(originalUrl);
            var cacheFilePath = FaviconStore.GetCacheFilePath(resolvedUrl);

            if (File.Exists(cacheFilePath)) return resolvedUrl;

            if (faviconDataDict == null) return originalUrl;

            var fileName = Path.GetFileName(cacheFilePath);
            if (!faviconDataDict.TryGetValue(fileName, out var bytes)) return originalUrl;

            Directory.CreateDirectory(FaviconStore.CacheDirectory);
            await File.WriteAllBytesAsync(cacheFilePath, bytes);

            return resolvedUrl;
        }
        catch
        {
            return originalUrl;
        }
    }

    /// <summary>统一按 UTC 序列化时间戳，避免 Kind=Local 的值被误标为 Z。</summary>
    private static string FormatUtc(DateTime value) => ToUtc(value).ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string? FormatUtcNullable(DateTime? value) => value == null ? null : FormatUtc(value.Value);

    /// <summary>把任意 Kind 的时间归一到 UTC（数据库中持久化的都是 UTC 时间）。</summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>解析备份中的时间戳：显式按 UTC 解释，避免被转成本地时间。</summary>
    private static DateTime ParseDateTime(string dateTimeStr)
    {
        if (DateTimeOffset.TryParse(dateTimeStr,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dto))
            return dto.UtcDateTime;
        return DateTime.UtcNow;
    }

    private static DateTime? ParseNullableDateTime(string? dateTimeStr)
    {
        if (string.IsNullOrWhiteSpace(dateTimeStr)) return null;
        if (DateTimeOffset.TryParse(dateTimeStr,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dto))
            return dto.UtcDateTime;
        return null;
    }

    #endregion

    #region 结果模型

    public class ImportResult
    {
        public int FoldersCreated { get; set; }
        public int LinksCreated { get; set; }
        public List<string> Errors { get; set; } = new();
        public int TotalItems => FoldersCreated + LinksCreated;
        public bool Success => Errors.Count == 0;
    }

    #endregion
}
