using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkPocket.Data;

namespace LinkPocket.Modules.Backup;

/// <summary>
/// .lpbackup 文件读写（internal，现状算法平移）。
///
/// <para><b>身份模型</b>：文件夹在备份文件里用导入期临时 key（"f1"、"f2"）表达层级，
/// key 只活在备份文件内、绝不写库——防撞 + 免疫重名/含「&gt;」文件夹名。</para>
///
/// <para><b>范围</b>：只备份活数据（folders / links）；回收站不备份。</para>
///
/// <para><b>完整性</b>：manifest 记录 data.json 的 SHA-256，导入前先验；版本严格等于
/// <see cref="FormatVersion"/> 才收（不做前缀宽松匹配、不做旧格式兼容）。</para>
/// </summary>
internal static class BackupIO
{
    /// <summary>
    /// **本备份格式的版本标识（定稿）**：`lpbackup/2.0`。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 命名口径：`家族/主版本.次版本`。判据按**兼容性**分档，而不是"长得像就算"：
    /// <list type="bullet">
    /// <item><b>次版本（`.N`）</b>= 只增字段 / 只放宽约束 → **向后兼容**，读取端可以照收（判据见 <see cref="Accepts"/>）；</item>
    /// <item><b>主版本（`N.`）</b>= 结构变了（改 key 模型 / 改语义 / 删字段）→ **必须拒绝**并提示升级应用
    /// （拒绝而不是"尽力而为地猜"——猜必然猜错，与零兼容红线一致）。</item>
    /// </list>
    /// 因此 <see cref="Accepts"/> 只接受**同一个主版本且次版本不高于本实现**的包；
    /// 旧的前缀匹配（`StartsWith("2")`）会把 `2abc` / `20.0` / `2.9.9-beta` 一律放进导入器 —— 那是缺陷。
    /// </para>
    /// </remarks>
    public const string FormatVersion = "lpbackup/2.0";

    /// <summary>本格式的缺省文件扩展名（导出对话框 / 文档 / 工具共用一处）。</summary>
    public const string FileExtension = ".lpbackup";

    /// <summary>
    /// 该版本串是否可被本实现读取：主版本相同、次版本 ≤ 本实现的次版本。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="FormatVersion"/> 配对的**唯一**版本判定入口（读取路径只调它，
    /// 绝不在别处再写一遍字符串比较——"版本口味"必须只有一处）。
    /// </remarks>
    public static bool Accepts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var text = version.Trim();

        var slash = text.IndexOf('/');
        if (slash < 0) return false;
        if (!string.Equals(text[..slash], "lpbackup", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = text[(slash + 1)..].Split('.');
        if (parts.Length < 1 || parts.Length > 2) return false;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts.Length > 1 ? parts[1] : "0", out var minor))
            return false;

        var (selfMajor, selfMinor) = SelfVersion();
        return major == selfMajor && minor <= selfMinor;
    }

    private static (int Major, int Minor) SelfVersion()
    {
        var parts = FormatVersion[(FormatVersion.IndexOf('/') + 1)..].Split('.');
        return (int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 0);
    }

    // —— JSON 模型（与既有格式逐字段一致）——

    internal sealed class BackupManifest
    {
        /// <summary>格式版本标识（定稿 = <see cref="FormatVersion"/>）。</summary>
        [JsonPropertyName("version")] public string Version { get; set; } = FormatVersion;

        /// <summary>写出这份备份的**应用版本**（诊断用；导出时必填，绝不写 null——历史实现恒为 null 是缺陷）。</summary>
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

    /// <summary>
    /// 把全量文件夹/链接打包为 .lpbackup（含引用到的图标缓存文件）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>原子落盘</b>：整包先写到**同目录临时文件**
    /// （`*.lpbackup.tmp-<guid>`），全部写完再 `File.Move(..., overwrite: true)` 一次性替换目标。
    /// 因此"打包中途失败 / 被取消 / 断电"只会留下一个临时文件，**目标备份要么是旧的完整包、要么是新的完整包**，
    /// 绝不会出现半截包（旧实现直接往目标路径写 zip：一旦失败，用户的旧备份已经被上层删掉、
    /// 新备份又是坏的 = 两次丢失）。
    /// </para>
    /// <para>
    /// 临时文件与目标**同目录**是硬要求：跨卷 `File.Move` 会退化成"复制 + 删除"，那就不再原子。
    /// </para>
    /// </remarks>
    public static async Task PackAsync(
        IReadOnlyList<Folder> folders, IReadOnlyList<Link> links,
        string outputPath, CancellationToken ct)
    {
        var tempPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await WriteArchiveAsync(folders, links, tempPath, ct);

            // 同卷原子替换：目标已存在时也是一次性换掉，不存在时等同改名
            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);   // 失败不留垃圾（尽力而为；删不掉也不能顶替原始异常）
            throw;
        }
    }

    /// <summary>把整包写进 <paramref name="path"/>（调用方负责原子替换与清理）。</summary>
    private static async Task WriteArchiveAsync(
        IReadOnlyList<Folder> folders, IReadOnlyList<Link> links,
        string path, CancellationToken ct)
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
            Version = FormatVersion,
            AppVersion = AppVersionOfThisBuild(),
            DataSha256 = Convert.ToHexString(SHA256.HashData(dataBytes)).ToLowerInvariant(),
            Statistics = new BackupStatistics
            {
                TotalFolders = backupData.Folders.Count,
                TotalLinks = backupData.Links.Count,
                TotalFavicons = faviconFiles.Count,
            },
        };

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
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

    /// <summary>写这份备份的应用版本（程序集信息版本；拿不到时回落程序集版本，绝不写 null）。</summary>
    private static string AppVersionOfThisBuild()
    {
        var asm = typeof(BackupIO).Assembly;
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

    // —— 外部输入的规模上限（**必须先看 zip 头声明的解压后大小，再决定要不要读**）——
    //
    // 备份文件是**外部输入**，没有上限就等于把进程的内存
    // 交给文件说话 —— 一个几 MB 的包可以声明解压出几个 GB（zip 炸弹），`CopyToAsync(MemoryStream)`
    // 会一路吃内存直到 OOM。上限取"本应用的库不可能超过的量级"：10 万条书签的 data.json 约 30–40MB，
    // 512MB 是 10 倍余量；图标单文件 8MB（远超任何真实 favicon）、条目 4096（远超真实引用数）。
    private const long MaxDataJsonBytes = 512L * 1024 * 1024;
    private const int MaxFaviconEntries = 4096;
    private const long MaxFaviconBytes = 8L * 1024 * 1024;
    private const long MaxManifestBytes = 1024 * 1024;

    /// <summary>读 zip + 版本检查 + SHA-256 校验（不写任何库数据）。</summary>
    public static async Task<BackupFile> ReadAsync(string filePath, CancellationToken ct)
    {
        var file = new BackupFile();

        if (!File.Exists(filePath))
        {
            file.Errors.Add("backup file does not exist");
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
                file.Errors.Add("invalid backup file: manifest.json or data.json is missing");
                return file;
            }

            // 先看声明尺寸（zip 头里的 UncompressedLength），超限**直接拒绝、一个字节都不读**
            if (manifestEntry.Length > MaxManifestBytes)
            {
                file.Errors.Add($"abnormal backup file: manifest.json declares {manifestEntry.Length} bytes (limit {MaxManifestBytes})");
                return file;
            }
            if (dataEntry.Length > MaxDataJsonBytes)
            {
                file.Errors.Add($"backup file too large: data.json declares {dataEntry.Length / (1024 * 1024)}MB (limit {MaxDataJsonBytes / (1024 * 1024)}MB)");
                return file;
            }

            var faviconEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("favicons/") && !e.FullName.EndsWith("/"))
                .ToList();
            if (faviconEntries.Count > MaxFaviconEntries)
            {
                file.Errors.Add($"abnormal backup file: {faviconEntries.Count} favicon entries (limit {MaxFaviconEntries})");
                return file;
            }
            var oversizedFavicon = faviconEntries.FirstOrDefault(e => e.Length > MaxFaviconBytes);
            if (oversizedFavicon != null)
            {
                file.Errors.Add($"abnormal backup file: favicon {oversizedFavicon.FullName} declares {oversizedFavicon.Length} bytes"
                                + $"(limit {MaxFaviconBytes})");
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
        catch (OperationCanceledException)
        {
            // 取消**不是**"文件损坏"：原样上抛，让引擎把它包成 LP.ENG.003（取消语义）
            throw;
        }
        catch (OutOfMemoryException ex)
        {
            // 内存不足也**不是**"文件损坏"：如实说清，别让用户去反复检查一个没问题的文件
            file.Errors.Add("out of memory while reading the backup file (it is abnormally large); confirm this is a backup exported by this app:" + ex.Message);
            return file;
        }
        catch (Exception ex)
        {
            file.Errors.Add("backup file unreadable (it may be corrupt):" + ex.Message);
            return file;
        }

        // 版本检查：只认本格式的兼容版本（**严格判定**，不做前缀宽松匹配、不做旧格式兼容）
        var version = file.Manifest.Version?.Trim() ?? "";
        if (!Accepts(version))
        {
            file.Errors.Add($"unsupported backup version '{version}' (this app supports {FormatVersion}); upgrade the app and retry");
            return file;
        }

        // 完整性校验：data.json 必须与 manifest 记录的 SHA-256 一致
        var expected = file.Manifest.DataSha256?.Trim();
        if (string.IsNullOrEmpty(expected))
        {
            file.Errors.Add("the backup has no integrity checksum (data_sha256), the file may be incomplete");
            return file;
        }

        var actual = Convert.ToHexString(SHA256.HashData(file.DataBytes)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            file.Errors.Add("backup integrity check failed: the file was modified or is corrupt");
            return file;
        }

        try
        {
            // SHA-256 通过但 data.json 不是合法 JSON（手工构造/罕见损坏）也必须降级为明确错误，而非内部错误
            file.Data = JsonSerializer.Deserialize<BackupData>(file.DataBytes, JsonOptions) ?? new BackupData();
        }
        catch (Exception ex)
        {
            file.Errors.Add("backup data parse failed (data.json is not valid JSON):" + ex.Message);
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

    /// <summary>
    /// 恢复图标缓存文件：包内有对应文件且**本地那份与包内不一致**时才写入；写不进去照抛。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要比对而不是"存在就跳过"</b>：本地缓存可能属于
    /// **另一次导入留下的旧文件**（同一个 favicon URL → 同一个哈希文件名），也可能上次写到一半被打断。
    /// 旧实现只看"文件在不在"，于是"备份里的图标"和"库里那条链接指向的图标"可能不是同一个东西——
    /// 用户看到的是错的图标且没有任何提示。
    /// </para>
    /// <para>
    /// <b>读不到 / 写不进一律抛出</b>（调用方 = 导入处理器，负责把失败转成命令错误并**整体回滚**）：
    /// 静默吞异常会让"图标没恢复"这件事无声发生（本仓观测面红线）。
    /// </para>
    /// <para>
    /// 返回 = 库里该链接的 <c>favicon_url</c>：包内有该图标 → 归一后的缓存地址；包内没有 → **原地址照抄**
    /// （绝不把用户数据改写成别的东西——图标缺失不是数据损坏，如实保留用户原来的值）。
    /// </para>
    /// </remarks>
    public static async Task<string?> RestoreFaviconFileAsync(
        string? originalUrl, Dictionary<string, byte[]>? faviconData, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(originalUrl)) return originalUrl;

        var resolvedUrl = ResolveFaviconUrl(originalUrl);
        if (faviconData is null) return originalUrl;

        var cacheFilePath = GetCacheFilePath(resolvedUrl);
        var fileName = Path.GetFileName(cacheFilePath);
        if (!faviconData.TryGetValue(fileName, out var bytes)) return originalUrl;

        if (File.Exists(cacheFilePath))
        {
            var existing = await File.ReadAllBytesAsync(cacheFilePath, ct);
            if (existing.AsSpan().SequenceEqual(bytes)) return resolvedUrl;   // 本地那份与包内一致 → 无需重写
        }

        Directory.CreateDirectory(CacheDirectory);
        await File.WriteAllBytesAsync(cacheFilePath, bytes, ct);
        return resolvedUrl;
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

    // —— 时间戳（统一 UTC 序列化；**解析失败必须暴露，不许伪造**）——
    //
    // 旧实现的 `ParseDateTime` 在解析失败时**静默回落 DateTime.UtcNow**
    // —— 那是**伪造数据**：一个非法/被篡改的时间戳会被伪装成"刚刚创建/刚刚更新"，用户在界面上看到的是假时间，
    // 且没有任何提示（本仓红线：不猜意图、不静默兜底）。
    // 现行口径分两种，绝不混用：
    //   ① **字段缺失**（备份格式允许省略；旧备份与手写包都可能没有）→ 由调用方显式给一个当时时间（见 TryParseUtc）；
    //   ② **字段存在但解析不了**（损坏 / 篡改 / 协议不符）→ 返回 false，由导入器**整包拒绝**并说清是哪一项。

    internal static string FormatUtc(DateTime value)
        => ToUtc(value).ToString("yyyy-MM-ddTHH:mm:ssZ");

    internal static string? FormatUtcNullable(DateTime? value) => value == null ? null : FormatUtc(value.Value);

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// 解析备份里的时间戳：<c>false</c> = 字段**有值但解析不了**（调用方必须如实拒绝，不许回落当前时间）。
    /// </summary>
    /// <remarks>空 / 缺省视为"没有值"，返回 true 且 <paramref name="utc"/> 为 null —— 那是"字段缺失"这一种情形。</remarks>
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
