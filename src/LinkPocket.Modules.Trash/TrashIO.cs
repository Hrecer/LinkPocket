using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkPocket.Contracts;
using LinkPocket.Data;

namespace LinkPocket.Modules.Trash;

/// <summary>
/// .lptrash 文件读写（**与 .lpbackup 同族条款**——版本门共用 <see cref="FormatVersionGate"/>）。
///
/// <para><b>容器</b>：zip + <c>manifest.json</c>（格式族/版本、SHA-256、计数）+ <c>data.json</c>
/// （单元 + 链接快照，**保留实体原 ID**）。与 lpbackup 的差异：回收站条目"回站"要求身份不变，
/// 故没有临时 key 模型；图标缓存文件暂不随包携带（恢复后再抓，见 <c>UPDATE.md</c> §4.2）。</para>
///
/// <para><b>原子落盘</b>：整包先写同目录临时文件、写完一次性 <c>File.Move</c> 替换目标——
/// 中途失败只会留一个临时文件（清理尽力而为），目标要么是旧的完整包、要么是新的完整包。</para>
/// </summary>
internal static class TrashIO
{
    /// <summary>**本格式的版本标识（定稿）**：`lptrash/1.0`。判据 = <see cref="FormatVersionGate"/>。</summary>
    public const string FormatVersion = "lptrash/1.0";

    /// <summary>本格式的缺省文件扩展名（导出对话框 / 文档 / 工具共用一处）。</summary>
    public const string FileExtension = ".lptrash";

    /// <summary>本实现读得懂的主版本集合（升序）。当前只有 1（唯一主版本 = 定稿形态）。</summary>
    private static readonly int[] ReadableMajors = [1];

    public static bool Accepts(string? version)
        => FormatVersionGate.Accepts(version, "lptrash", ReadableMajors, FormatVersion);

    // —— JSON 模型（字段名 = 表列名；**保留实体原 ID**）——

    internal sealed class TrashManifest
    {
        [JsonPropertyName("version")] public string Version { get; set; } = FormatVersion;

        /// <summary>写出这份包的应用版本（诊断用；绝不写 null）。</summary>
        [JsonPropertyName("app_version")] public string? AppVersion { get; set; }
        [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        [JsonPropertyName("created_by")] public string CreatedBy { get; set; } = "LinkPocket";

        /// <summary>data.json（UTF-8 字节）的 SHA-256，导入前校验完整性。</summary>
        [JsonPropertyName("data_sha256")] public string? DataSha256 { get; set; }
        [JsonPropertyName("statistics")] public TrashStatistics Statistics { get; set; } = new();
    }

    internal sealed class TrashStatistics
    {
        [JsonPropertyName("total_units")] public int TotalUnits { get; set; }
        [JsonPropertyName("total_links")] public int TotalLinks { get; set; }
    }

    internal sealed class TrashFolderData
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("parent_id")] public string? ParentId { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("origin_folder_id")] public string? OriginFolderId { get; set; }
        [JsonPropertyName("origin_parent_folder_id")] public string? OriginParentFolderId { get; set; }
        [JsonPropertyName("origin_path")] public string? OriginPath { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
        [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
        [JsonPropertyName("last_visited_at")] public string? LastVisitedAt { get; set; }
        [JsonPropertyName("visit_count")] public int VisitCount { get; set; }
        [JsonPropertyName("deleted_at")] public string DeletedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    internal sealed class TrashLinkData
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("favicon_url")] public string? FaviconUrl { get; set; }
        [JsonPropertyName("trash_folder_id")] public string? TrashFolderId { get; set; }
        [JsonPropertyName("origin_folder_id")] public string? OriginFolderId { get; set; }
        [JsonPropertyName("origin_path")] public string? OriginPath { get; set; }
        [JsonPropertyName("last_visited_at")] public string? LastVisitedAt { get; set; }
        [JsonPropertyName("visit_count")] public int VisitCount { get; set; }
        [JsonPropertyName("is_important")] public bool IsImportant { get; set; }
        [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        [JsonPropertyName("deleted_at")] public string DeletedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    internal sealed class TrashData
    {
        [JsonPropertyName("folders")] public List<TrashFolderData> Folders { get; set; } = [];
        [JsonPropertyName("links")] public List<TrashLinkData> Links { get; set; } = [];
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // —— 打包（导出）——

    /// <summary>把回收站两表打包为 .lptrash（临时文件 + 原子替换）。</summary>
    public static async Task PackAsync(
        IReadOnlyList<TrashedFolder> folders, IReadOnlyList<TrashedLink> links,
        string outputPath, CancellationToken ct)
    {
        var tempPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await WriteArchiveAsync(folders, links, tempPath, ct);
            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);   // 失败不留垃圾（尽力而为；删不掉也不能顶替原始异常）
            throw;
        }
    }

    private static async Task WriteArchiveAsync(
        IReadOnlyList<TrashedFolder> folders, IReadOnlyList<TrashedLink> links,
        string path, CancellationToken ct)
    {
        var data = new TrashData();
        foreach (var f in folders)
        {
            data.Folders.Add(new TrashFolderData
            {
                Id = f.TrashFolderId,
                ParentId = f.ParentTrashFolderId,
                Name = f.Name,
                OriginFolderId = f.OriginFolderId,
                OriginParentFolderId = f.OriginParentFolderId,
                OriginPath = f.OriginPath,
                Description = f.Description,
                SortOrder = f.SortOrder,
                CreatedAt = FormatUtcNullable(f.CreatedAt),
                LastVisitedAt = FormatUtcNullable(f.LastVisitedAt),
                VisitCount = f.VisitCount,
                DeletedAt = FormatUtc(f.DeletedAt),
            });
        }

        foreach (var l in links)
        {
            data.Links.Add(new TrashLinkData
            {
                Id = l.LinkId,
                Url = l.Url,
                Title = l.Title,
                Description = l.Description,
                FaviconUrl = l.FaviconUrl,
                TrashFolderId = l.TrashFolderId,
                OriginFolderId = l.OriginListId,
                OriginPath = l.OriginPath,
                LastVisitedAt = FormatUtcNullable(l.LastVisitedAt),
                VisitCount = l.VisitCount,
                IsImportant = l.IsImportant,
                CreatedAt = FormatUtc(l.CreatedAt),
                UpdatedAt = FormatUtc(l.UpdatedAt),
                DeletedAt = FormatUtc(l.DeletedAt),
            });
        }

        // data.json 先在内存成型 → 算 SHA-256 → 写 manifest（先 data 后 manifest，哈希才对得上）
        var dataBytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        var manifest = new TrashManifest
        {
            Version = FormatVersion,
            AppVersion = AppVersionOfThisBuild(),
            DataSha256 = Convert.ToHexString(SHA256.HashData(dataBytes)).ToLowerInvariant(),
            Statistics = new TrashStatistics
            {
                TotalUnits = data.Folders.Count,
                TotalLinks = data.Links.Count,
            },
        };

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var dataEntry = archive.CreateEntry("data.json");
        await using (var stream = dataEntry.Open())
            await stream.WriteAsync(dataBytes, ct);

        var manifestEntry = archive.CreateEntry("manifest.json");
        await using (var writer = new StreamWriter(manifestEntry.Open()))
            await writer.WriteAsync(JsonSerializer.Serialize(manifest, JsonOptions));
    }

    /// <summary>写这份包的应用版本（程序集信息版本；与 backup 的 manifest.app_version 同一口径，绝不写 null）。</summary>
    private static string AppVersionOfThisBuild()
    {
        var asm = typeof(TrashIO).Assembly;
        var info = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? asm.GetName().Version?.ToString() ?? "unknown" : info;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清临时文件尽力而为：删不掉也不能顶替"打包失败"这个原始异常
        }
    }

    // —— 读取（导入共用）——

    /// <summary>读取结果：manifest + data.json 字节 + 解析后的数据 + 错误清单。</summary>
    internal sealed class TrashFile
    {
        public TrashManifest Manifest = new();
        public byte[] DataBytes = [];
        public TrashData Data = new();
        public List<string> Errors = [];
        public bool Valid => Errors.Count == 0;
    }

    // —— 外部输入的规模上限（先看 zip 头声明的解压后大小，再决定要不要读；与 lpbackup 同口径）——
    private const long MaxDataJsonBytes = 512L * 1024 * 1024;
    private const long MaxManifestBytes = 1024 * 1024;

    /// <summary>读 zip + 版本检查 + SHA-256 校验（不写任何库数据）。</summary>
    public static async Task<TrashFile> ReadAsync(string filePath, CancellationToken ct)
    {
        var file = new TrashFile();

        if (!File.Exists(filePath))
        {
            file.Errors.Add("file does not exist");
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
                    file.Errors.Add("invalid trash file: manifest.json or data.json is missing");
                    return file;
                }

                if (manifestEntry.Length > MaxManifestBytes)
                {
                    file.Errors.Add($"abnormal trash file: manifest.json declares {manifestEntry.Length} bytes (limit {MaxManifestBytes})");
                    return file;
                }
                if (dataEntry.Length > MaxDataJsonBytes)
                {
                    file.Errors.Add($"trash file too large: data.json declares {dataEntry.Length / (1024 * 1024)}MB (limit {MaxDataJsonBytes / (1024 * 1024)}MB)");
                    return file;
                }

                using (var stream = manifestEntry.Open())
                using (var reader = new StreamReader(stream))
                    file.Manifest = JsonSerializer.Deserialize<TrashManifest>(await reader.ReadToEndAsync(ct)) ?? new TrashManifest();

                using (var stream = dataEntry.Open())
                using (var ms = new MemoryStream())
                {
                    await stream.CopyToAsync(ms, ct);
                    file.DataBytes = ms.ToArray();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消**不是**"文件损坏"：原样上抛，让引擎把它包成 LP.ENG.003（取消语义）
            throw;
        }
        catch (OutOfMemoryException ex)
        {
            file.Errors.Add("out of memory while reading the trash file (it is abnormally large); confirm this file was exported by this app:" + ex.Message);
            return file;
        }
        catch (Exception ex)
        {
            file.Errors.Add("trash file unreadable (it may be corrupt):" + ex.Message);
            return file;
        }

        // 版本检查：只认同族的兼容版本（**严格判定**，不做前缀宽松匹配）
        var version = file.Manifest.Version?.Trim() ?? "";
        if (!Accepts(version))
        {
            file.Errors.Add($"unsupported trash format version '{version}' (this app supports {FormatVersion}); upgrade the app and retry");
            return file;
        }

        // 完整性校验：data.json 必须与 manifest 记录的 SHA-256 一致
        var expected = file.Manifest.DataSha256?.Trim();
        if (string.IsNullOrEmpty(expected))
        {
            file.Errors.Add("the trash file has no integrity checksum (data_sha256), the file may be incomplete");
            return file;
        }
        var actual = Convert.ToHexString(SHA256.HashData(file.DataBytes)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            file.Errors.Add("trash file integrity check failed: the file was modified or is corrupt");
            return file;
        }

        try
        {
            file.Data = JsonSerializer.Deserialize<TrashData>(file.DataBytes, JsonOptions) ?? new TrashData();
        }
        catch (Exception ex)
        {
            file.Errors.Add("trash data parse failed (data.json is not valid JSON):" + ex.Message);
        }
        return file;
    }

    // —— 时间戳（统一 UTC 序列化；解析失败必须暴露，不许伪造；与 lpbackup 同口径）——

    internal static string FormatUtc(DateTime value)
        => ToUtc(value).ToString("yyyy-MM-ddTHH:mm:ssZ");

    internal static string? FormatUtcNullable(DateTime? value) => value == null ? null : FormatUtc(value.Value);

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>解析包里的时间戳：<c>false</c> = 字段**有值但解析不了**（调用方必须如实拒绝，不许回落当前时间）。</summary>
    internal static bool TryParseUtc(string? text, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        if (DateTimeOffset.TryParse(text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }
        return false;
    }
}
