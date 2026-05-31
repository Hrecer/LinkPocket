using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Services;

public class LinkPocketBackupService
{
    private readonly LinkPocketDbContext _db;
    private static readonly string _faviconCacheDir = Path.Combine(AppContext.BaseDirectory, "favicons");

    public LinkPocketBackupService(LinkPocketDbContext db)
    {
        _db = db;
    }

    #region 数据模型（用于JSON序列化）

    public class BackupManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        [JsonPropertyName("created_by")]
        public string CreatedBy { get; set; } = "LinkPocket";

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
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("parent_path")]
        public string? ParentPath { get; set; } = null;

        [JsonPropertyName("sort_order")]
        public int SortOrder { get; set; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    public class BackupLinkData
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("favicon_url")]
        public string? FaviconUrl { get; set; }

        [JsonPropertyName("folder_path")]
        public string? FolderPath { get; set; } = null;

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

    #region 导出功能

    public async Task ExportAsync(string outputPath, IProgress<(string message, int current, int total)>? progress = null)
    {
        progress?.Report(("正在准备导出...", 0, 1));

        var manifest = new BackupManifest();
        var backupData = new BackupData();
        var faviconFiles = new ConcurrentDictionary<string, string>();

        var allFolders = await _db.Folders.OrderBy(f => f.Name).ToListAsync();
        var allLinks = await _db.Links.ToListAsync();

        progress?.Report(("正在处理文件夹...", 0, allFolders.Count));

        int folderIndex = 0;
        foreach (var folder in allFolders)
        {
            var folderPath = BuildFolderPath(folder.FolderId, allFolders);

            backupData.Folders.Add(new BackupFolderData
            {
                Name = folder.Name,
                Description = folder.Description,
                ParentPath = GetParentFolderPath(folder, allFolders),
                SortOrder = folder.SortOrder,
                CreatedAt = folder.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                UpdatedAt = folder.UpdatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")
            });

            folderIndex++;
            progress?.Report(($"正在处理文件夹: {folder.Name}", folderIndex, allFolders.Count));
        }

        manifest.Statistics.TotalFolders = backupData.Folders.Count;

        progress?.Report(($"正在处理书签 (0/{allLinks.Count})...", 0, allLinks.Count));

        int linkIndex = 0;
        foreach (var link in allLinks)
        {
            var folderPath = link.ListId != null ? BuildFolderPath(link.ListId, allFolders) : null;

            var backupLink = new BackupLinkData
            {
                Url = link.Url,
                Title = link.Title,
                Description = link.Description,
                FaviconUrl = link.FaviconUrl,
                FolderPath = folderPath,
                VisitCount = link.VisitCount,
                IsImportant = link.IsImportant,
                LastVisitedAt = link.LastVisitedAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                CreatedAt = link.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                UpdatedAt = link.UpdatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };

            backupData.Links.Add(backupLink);

            if (!string.IsNullOrWhiteSpace(link.FaviconUrl))
            {
                CollectFaviconFile(link.FaviconUrl, faviconFiles);
            }

            linkIndex++;
            if (linkIndex % 10 == 0 || linkIndex == allLinks.Count)
            {
                progress?.Report(($"正在处理书签 ({linkIndex}/{allLinks.Count})...", linkIndex, allLinks.Count));
            }
        }

        manifest.Statistics.TotalLinks = backupData.Links.Count;
        manifest.Statistics.TotalFavicons = faviconFiles.Count;

        progress?.Report(($"正在打包文件 (图标: {faviconFiles.Count} 个)...", 0, 1));

        using (var archive = System.IO.Compression.ZipFile.Open(outputPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            var manifestEntry = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open()))
            {
                await writer.WriteAsync(JsonSerializer.Serialize(manifest, jsonOptions));
            }

            var dataEntry = archive.CreateEntry("data.json");
            using (var writer = new StreamWriter(dataEntry.Open()))
            {
                await writer.WriteAsync(JsonSerializer.Serialize(backupData, jsonOptions));
            }

            if (faviconFiles.Count > 0)
            {
                var faviconsDir = archive.CreateEntry("favicons/");
                int faviconIndex = 0;
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

        progress?.Report(("导出完成！", manifest.Statistics.TotalFolders + manifest.Statistics.TotalLinks, manifest.Statistics.TotalFolders + manifest.Statistics.TotalLinks));
    }

    private static string BuildFolderPath(string folderId, List<Folder> allFolders)
    {
        var pathParts = new List<string>();
        var currentId = folderId;

        for (int i = 0; i < 20 && !string.IsNullOrEmpty(currentId); i++)
        {
            var folder = allFolders.FirstOrDefault(f => f.FolderId == currentId);
            if (folder == null) break;

            pathParts.Insert(0, folder.Name ?? "未命名文件夹");
            currentId = folder.ParentId;
        }

        return string.Join(" > ", pathParts);
    }

    private static string? GetParentFolderPath(Folder folder, List<Folder> allFolders)
    {
        if (string.IsNullOrEmpty(folder.ParentId)) return null;

        return BuildFolderPath(folder.ParentId, allFolders);
    }

    private static void CollectFaviconFile(string faviconUrl, ConcurrentDictionary<string, string> faviconFiles)
    {
        try
        {
            var resolvedUrl = FaviconService.ResolveFaviconUrl(faviconUrl);
            if (string.IsNullOrWhiteSpace(resolvedUrl)) return;

            var cacheFilePath = GetCacheFilePath(resolvedUrl);
            if (!File.Exists(cacheFilePath)) return;

            var fileName = Path.GetFileName(cacheFilePath);
            if (!faviconFiles.ContainsKey(resolvedUrl))
            {
                faviconFiles.TryAdd(resolvedUrl, cacheFilePath);
            }
        }
        catch
        {
        }
    }

    private static string GetCacheFilePath(string faviconUrl)
    {
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(faviconUrl)));
        var ext = GetExtensionFromUrl(faviconUrl);
        return Path.Combine(_faviconCacheDir, $"{hash}{ext}");
    }

    private static string GetExtensionFromUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path).ToLower();
            return ext is ".png" or ".jpg" or ".jpeg" or ".ico" or ".gif" or ".bmp" or ".webp" or ".svg"
                ? ext == ".jpeg" ? ".jpg" : ext
                : ".ico";
        }
        catch
        {
            return ".ico";
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

        BackupManifest? manifest = null;
        BackupData? backupData = null;
        Dictionary<string, byte[]>? faviconDataDict = null;

        using (var archive = System.IO.Compression.ZipFile.OpenRead(filePath))
        {
            var manifestEntry = archive.GetEntry("manifest.json");
            if (manifestEntry == null)
            {
                result.Errors.Add("无效的备份文件：缺少 manifest.json");
                return result;
            }

            using (var stream = manifestEntry.Open())
            using (var reader = new StreamReader(stream))
            {
                var json = await reader.ReadToEndAsync();
                manifest = JsonSerializer.Deserialize<BackupManifest>(json);
            }

            var dataEntry = archive.GetEntry("data.json");
            if (dataEntry == null)
            {
                result.Errors.Add("无效的备份文件：缺少 data.json");
                return result;
            }

            using (var stream = dataEntry.Open())
            using (var reader = new StreamReader(stream))
            {
                var json = await reader.ReadToEndAsync();
                backupData = JsonSerializer.Deserialize<BackupData>(json);
            }

            var faviconsEntry = archive.GetEntry("favicons/");
            if (faviconsEntry != null)
            {
                faviconDataDict = new Dictionary<string, byte[]>();
                var faviconEntries = archive.Entries.Where(e => e.FullName.StartsWith("favicons/") && !e.FullName.EndsWith("/"));

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
        }

        if (backupData == null)
        {
            result.Errors.Add("无法解析备份数据");
            return result;
        }

        progress?.Report(("验证数据完整性...", 0, 1));

        if (backupData.Folders == null) backupData.Folders = new();
        if (backupData.Links == null) backupData.Links = new();

        var totalItems = backupData.Folders.Count + backupData.Links.Count;
        var processedItems = 0;

        var pathToFolderIdMap = new Dictionary<string, string>();

        var sortedFolders = backupData.Folders
            .OrderBy(f => string.IsNullOrWhiteSpace(f.ParentPath) ? 0 : f.ParentPath.Count(c => c == '>') + 1)
            .ThenBy(f => f.ParentPath ?? "")
            .ToList();

        progress?.Report(($"正在导入文件夹 (0/{sortedFolders.Count})...", 0, sortedFolders.Count));

        for (int i = 0; i < sortedFolders.Count; i++)
        {
            var folderData = sortedFolders[i];

            string? parentId = null;
            if (!string.IsNullOrWhiteSpace(folderData.ParentPath))
            {
                pathToFolderIdMap.TryGetValue(folderData.ParentPath, out parentId);
            }

            var folder = new Folder
            {
                Name = folderData.Name,
                Description = folderData.Description,
                ParentId = parentId,
                LinkCount = 0,
                SortOrder = folderData.SortOrder,
                CreatedAt = ParseDateTime(folderData.CreatedAt),
                UpdatedAt = ParseDateTime(folderData.UpdatedAt)
            };

            _db.Folders.Add(folder);
            await _db.SaveChangesAsync();

            var folderPath = BuildImportFolderPath(folderData, pathToFolderIdMap);
            pathToFolderIdMap[folderPath] = folder.FolderId;

            result.FoldersCreated++;
            processedItems++;
            progress?.Report(($"正在导入文件夹: {folderData.Name} ({i + 1}/{sortedFolders.Count})", processedItems, totalItems));
        }

        progress?.Report(($"正在导入书签 (0/{backupData.Links.Count})...", processedItems, totalItems));

        for (int i = 0; i < backupData.Links.Count; i++)
        {
            var linkData = backupData.Links[i];

            string? listId = null;
            if (!string.IsNullOrWhiteSpace(linkData.FolderPath))
            {
                pathToFolderIdMap.TryGetValue(linkData.FolderPath, out listId);
            }

            string? resolvedFaviconUrl = null;
            if (!string.IsNullOrWhiteSpace(linkData.FaviconUrl))
            {
                resolvedFaviconUrl = await RestoreFaviconFile(linkData.FaviconUrl, faviconDataDict);
            }

            var link = new Link
            {
                Url = linkData.Url,
                Title = linkData.Title,
                Description = linkData.Description,
                FaviconUrl = resolvedFaviconUrl,
                ListId = listId,
                VisitCount = linkData.VisitCount,
                IsImportant = linkData.IsImportant,
                LastVisitedAt = ParseNullableDateTime(linkData.LastVisitedAt),
                CreatedAt = ParseDateTime(linkData.CreatedAt),
                UpdatedAt = ParseDateTime(linkData.UpdatedAt)
            };

            _db.Links.Add(link);

            result.LinksCreated++;
            processedItems++;
            if (processedItems % 10 == 0 || processedItems == totalItems)
            {
                progress?.Report(($"正在导入书签: {linkData.Title ?? linkData.Url} ({result.LinksCreated}/{backupData.Links.Count})", processedItems, totalItems));
            }
        }

        await _db.SaveChangesAsync();

        foreach (var folder in _db.Folders)
        {
            folder.UpdateLinkCount(_db);
        }
        await _db.SaveChangesAsync();

        progress?.Report(("导入完成！", processedItems, totalItems));

        return result;
    }

    private static string BuildImportFolderPath(BackupFolderData folderData, Dictionary<string, string> pathToFolderIdMap)
    {
        if (string.IsNullOrWhiteSpace(folderData.ParentPath))
            return folderData.Name;

        return $"{folderData.ParentPath} > {folderData.Name}";
    }

    private static async Task<string?> RestoreFaviconFile(string originalUrl, Dictionary<string, byte[]>? faviconDataDict)
    {
        if (faviconDataDict == null || string.IsNullOrWhiteSpace(originalUrl))
            return originalUrl;

        try
        {
            var resolvedUrl = FaviconService.ResolveFaviconUrl(originalUrl);
            var cacheFilePath = GetCacheFilePath(resolvedUrl);

            if (File.Exists(cacheFilePath)) return resolvedUrl;

            var fileName = Path.GetFileName(cacheFilePath);
            if (!faviconDataDict.ContainsKey(fileName)) return originalUrl;

            Directory.CreateDirectory(_faviconCacheDir);
            await File.WriteAllBytesAsync(cacheFilePath, faviconDataDict[fileName]);

            return resolvedUrl;
        }
        catch
        {
            return originalUrl;
        }
    }

    private static DateTime ParseDateTime(string dateTimeStr)
    {
        if (DateTime.TryParse(dateTimeStr, out var dt))
            return dt;
        return DateTime.UtcNow;
    }

    private static DateTime? ParseNullableDateTime(string? dateTimeStr)
    {
        if (string.IsNullOrWhiteSpace(dateTimeStr)) return null;
        if (DateTime.TryParse(dateTimeStr, out var dt))
            return dt;
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
