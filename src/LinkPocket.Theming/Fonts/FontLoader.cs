using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using LinkPocket.Contracts;

namespace LinkPocket.Theming.Fonts;

/// <summary>一个可选字体族（系统已装 或 用户导入）。</summary>
/// <param name="Family">WPF 字体族名（令牌链的第一段）。</param>
/// <param name="DisplayName">界面上显示的名字（导入字体为"文件名 · 族名"）。</param>
/// <param name="FilePath">导入字体的文件绝对路径；<c>null</c> = 系统已装字体。</param>
public sealed record FontChoice(string Family, string DisplayName, string? FilePath = null)
{
    /// <summary>是否来自用户导入的文件（可删除）。</summary>
    public bool IsImported => FilePath is not null;
}

/// <summary>
/// 字体装载：系统字体枚举 + 用户文件导入 + **回退链构造**（方案 §6.2）。
/// </summary>
/// <remarks>
/// <para>
/// <b>回退链为什么必要</b>：用户导入的常常是只有拉丁字形的字体（如 Cascadia Code）。
/// 令牌值写成 <c>导入族名, Microsoft YaHei UI, Segoe UI</c> —— WPF 逐字形回退，
/// 拉丁用所选字体、CJK 自动落到雅黑。若只写一个族名，中文会显示成方块。
/// </para>
/// <para>
/// <b>导入位置 = <c>{BaseDirectory}/fonts/</c></b>：与 db / favicons / logs 同目录。
/// 本仓纪律"绝不清理 bin"（用户数据在 bin 里）使这里成为持久位置。
/// </para>
/// <para>
/// <b>失败必须暴露</b>（观测面纪律）：非字体文件 / 损坏文件 → 明确报错并**不写偏好**，
/// 绝不"静默跳过"或"写入一个指向坏文件的偏好"。
/// </para>
/// </remarks>
public static class FontLoader
{
    /// <summary>回退链里永远保留的系统字体（CJK 兜底 + 通用兜底）。</summary>
    public static readonly IReadOnlyList<string> FallbackChain = new[] { "Microsoft YaHei UI", "Segoe UI" };

    private const string LogCategory = "app.theme";

    /// <summary>导入字体的存放目录（与 db/logs 同目录）。</summary>
    public static string FontDirectory => Path.Combine(AppContext.BaseDirectory, "fonts");

    /// <summary>
    /// 枚举系统已装字体（按显示名排序；只返回可与 WPF 解析的项）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>必须缓存</b>：<c>System.Windows.Media.Fonts.SystemFontFamilies</c> 是一次**全量字体枚举**
    /// （内部走 GDI/COM 字体表），在装有大量字体的机器上是**秒级**开销。
    /// 实测教训：不缓存时，「外观」面板每构建一次 VM 就枚举一次（16 个单测 + 探针多次进入该页），
    /// 累计把测试与探针拖到几分钟 —— 真实用户每进一次「外观」页也会卡同样的时长。
    /// </para>
    /// <para>
    /// 缓存在**进程生命周期内是安全的**：系统字体集合不会在应用运行期间变化；
    /// 用户新装的字体要重启应用才可见（与绝大多数桌面应用一致，属可接受边界）。
    /// 导入字体走另一条路（<see cref="ImportedFonts"/>，每次读目录 → 导入/删除即时可见）。
    /// </para>
    /// <para>用 <see cref="Lazy{T}"/> 保证并行首次访问时只枚举一次。</para>
    /// </remarks>
    public static IReadOnlyList<FontChoice> SystemFonts() => SystemFontsCache.Value;

    private static readonly Lazy<IReadOnlyList<FontChoice>> SystemFontsCache = new(() =>
    {
        var list = new List<FontChoice>();
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            // 一个字体族有多个本地化名（如 "Microsoft YaHei UI" / "微软雅黑 UI"）——取第一个作为显示名
            var name = family.FamilyNames.Values.FirstOrDefault() ?? family.Source;
            list.Add(new FontChoice(family.Source, name));
        }
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));
        return list;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>系统字体**族名**集合（"这个族可用吗"的快速判定；同样走缓存）。</summary>
    public static IReadOnlySet<string> SystemFontFamilies() => SystemFamilySet.Value;

    private static readonly Lazy<IReadOnlySet<string>> SystemFamilySet = new(
        () => SystemFonts().Select(f => f.Family).ToHashSet(StringComparer.OrdinalIgnoreCase),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 枚举已导入字体（按文件名排序）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>必须缓存，且只在导入/删除时失效</b>：每项都要把字体文件交给
    /// <c>Fonts.GetFontFamilies(Uri)</c> 解析族名 —— 那是**打开并解析字体表**的开销（几十~几百毫秒/个）。
    /// 而本方法被「外观」面板的每次 VM 构建 / 每次进入页面调用，实测能把面板拖成秒级卡顿。
    /// </para>
    /// <para>
    /// 失效时机 = <see cref="Import"/> / <see cref="Remove"/>（唯一会改变该目录内容的两处），
    /// 故"导入后立即可见、删除后立即消失"，与用户的因果直觉一致。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<FontChoice> ImportedFonts() => ImportedCache;

    /// <summary>可失效的导入字体缓存（<see cref="Lazy{T}"/> 不支持重置，故用字段 + 锁）。</summary>
    private static IReadOnlyList<FontChoice>? _importedCache;
    private static readonly object ImportedCacheLock = new();

    private static IReadOnlyList<FontChoice> ImportedCache
    {
        get
        {
            var cached = _importedCache;
            if (cached is not null) return cached;
            lock (ImportedCacheLock)
            {
                return _importedCache ??= ScanImportedFonts();
            }
        }
    }

    private static void InvalidateImportedCache()
    {
        lock (ImportedCacheLock)
        {
            _importedCache = null;
        }
    }

    private static IReadOnlyList<FontChoice> ScanImportedFonts()
    {
        var dir = FontDirectory;
        if (!Directory.Exists(dir)) return Array.Empty<FontChoice>();

        var list = new List<FontChoice>();
        foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".ttf" or ".otf" or ".ttc")) continue;
            // 枚举路径：解析失败**静默跳过该文件**（把它留在目录里由用户处置）。
            // 不在这里写日志：枚举会被反复调用，坏文件会造成日志洪泛（观测面噪音）。
            // 真正需要报错的是"用户主动导入"那条路径（见 Import），那里会明确抛。
            if (TryResolveFileFamily(file!, out var family, logFailure: false))
                list.Add(new FontChoice(family, $"{Path.GetFileNameWithoutExtension(file)} · {family}", file));
        }
        return list;
    }

    /// <summary>
    /// 导入一个字体文件：复制进 <see cref="FontDirectory"/> → 解析族名。
    /// </summary>
    /// <returns>导入成功后的 <see cref="FontChoice"/>。</returns>
    /// <exception cref="InvalidOperationException">不是字体 / 损坏 / 无法复制 —— 明确报错，不静默跳过。</exception>
    public static FontChoice Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("字体文件路径为空", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("字体文件不存在", sourcePath);

        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not (".ttf" or ".otf" or ".ttc"))
            throw new InvalidOperationException($"只支持 .ttf / .otf / .ttc 字体文件（实际：{ext}）");

        // 先验证"它真是字体"再落盘：避免把坏文件留在字体目录里污染后续枚举
        if (!TryResolveFileFamily(sourcePath, out var family))
            throw new InvalidOperationException("无法解析该文件为字体（文件可能已损坏或不是字体）");

        Directory.CreateDirectory(FontDirectory);
        var target = Path.Combine(FontDirectory, Path.GetFileName(sourcePath));
        try
        {
            File.Copy(sourcePath, target, overwrite: true);
        }
        catch (Exception ex)
        {
            LpLog.Error($"导入字体失败：{sourcePath}", ex, LogCategory);
            throw new InvalidOperationException($"复制字体文件失败：{ex.Message}", ex);
        }

        InvalidateImportedCache();   // 导入后立即可见
        LpLog.Info($"已导入字体「{family}」→ {target}", LogCategory);
        return new FontChoice(family, $"{Path.GetFileNameWithoutExtension(target)} · {family}", target);
    }

    /// <summary>删除一个已导入字体文件（系统字体不可删）。</summary>
    public static void Remove(FontChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (choice.FilePath is null)
            throw new InvalidOperationException("系统字体不可删除");
        if (File.Exists(choice.FilePath))
        {
            File.Delete(choice.FilePath);
            InvalidateImportedCache();   // 删除后立即消失
            LpLog.Info($"已删除导入字体：{choice.FilePath}", LogCategory);
        }
    }

    /// <summary>
    /// 构造字体令牌值（回退链）：<c>主族, Microsoft YaHei UI, Segoe UI</c>。
    /// </summary>
    /// <remarks>
    /// 逐个去重（主族就是雅黑时不再重复追加），且**永不返回空串**——
    /// 空 <c>FontFamily</c> 会让 WPF 回落到默认字体，症状是"换字体没反应"，属于静默失败。
    /// </remarks>
    public static string BuildTokenValue(string? primaryFamily)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(primaryFamily)) parts.Add(primaryFamily.Trim());
        foreach (var f in FallbackChain)
            if (!parts.Contains(f, StringComparer.OrdinalIgnoreCase))
                parts.Add(f);
        return string.Join(", ", parts);
    }

    /// <summary>构造 WPF <see cref="FontFamily"/>（令牌值 → 对象）。</summary>
    public static FontFamily BuildFontFamily(string? primaryFamily) =>
        new(BuildTokenValue(primaryFamily));

    /// <summary>默认界面字体族（= 改造前 <c>AppFont</c> 的第一段）。</summary>
    public const string DefaultUiFamily = "Microsoft YaHei UI";

    /// <summary>默认等宽字体族。</summary>
    public const string DefaultMonoFamily = "Consolas";

    /// <summary>解析文件型字体的族名（失败返回 false，不抛）。</summary>
    private static bool TryResolveFileFamily(string path, out string family, bool logFailure = true)
    {
        family = string.Empty;
        try
        {
            var families = System.Windows.Media.Fonts.GetFontFamilies(new Uri(path, UriKind.Absolute));
            var first = families.FirstOrDefault();
            if (first is null) return false;
            family = first.Source;
            return !string.IsNullOrWhiteSpace(family);
        }
        catch (Exception ex)
        {
            // 交给调用方决定语义（导入路径 = 报错并留痕；枚举路径 = 静默跳过该文件，避免日志洪泛）
            if (logFailure)
                LpLog.Warn($"字体文件无法解析（{path}）：{ex.Message}", category: LogCategory);
            return false;
        }
    }
}
