using System.IO;
using System.Windows.Media;
using LinkPocket.Contracts;

namespace LinkPocket.Theming.Fonts;

/// <summary>
/// 字体装载：系统字体候选 + 用户文件导入 + **回退链构造**（方案 §6.2）。
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
public static class FontCatalog
{
    /// <summary>回退链里永远保留的系统字体（CJK 兜底 + 通用兜底）。</summary>
    public static readonly IReadOnlyList<string> FallbackChain = new[] { "Microsoft YaHei UI", "Segoe UI" };

    private const string LogCategory = "app.theme";

    private static readonly ISystemFontSource ProductionSource = new WpfSystemFontSource();
    private static ISystemFontSource? _sourceOverride;

    /// <summary>
    /// 系统字体来源（生产 = <see cref="WpfSystemFontSource"/>）。
    /// </summary>
    /// <remarks>
    /// <b>这是测试隔离缝，不是产品开关</b>：产品的候选列表与"这个族可用吗"的判定都读它；
    /// 测试注入一份固定列表即可让用例不依赖"本机装了什么字体"。
    /// <b>真实枚举路径仍由自动化覆盖</b> —— <c>FontSystemTests</c> 里
    /// <c>系统字体枚举_非空且按显示名排序</c> 直接走生产来源。
    /// 用例改完必须调 <see cref="ResetForTests"/> 复位（否则会漏进下一个用例）。
    /// </remarks>
    public static ISystemFontSource SystemSource
    {
        get => _sourceOverride ?? ProductionSource;
        set
        {
            _sourceOverride = value;
            InvalidateSystemFamilySet();
        }
    }

    /// <summary>导入字体的存放目录（与 db/logs 同目录）。</summary>
    public static string FontDirectory => Path.Combine(AppContext.BaseDirectory, "fonts");

    private static IReadOnlySet<string>? _systemFamilySet;
    private static readonly object SystemFamilySetLock = new();

    /// <summary>系统已装字体的族名集合（与 <see cref="SystemSource"/> 同一事实来源）。</summary>
    public static IReadOnlySet<string> SystemFontFamilies()
    {
        var cached = _systemFamilySet;
        if (cached is not null) return cached;
        lock (SystemFamilySetLock)
        {
            return _systemFamilySet ??= SystemSource.Enumerate()
                .Select(f => f.Family)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void InvalidateSystemFamilySet()
    {
        lock (SystemFamilySetLock)
        {
            _systemFamilySet = null;
        }
    }

    /// <summary>
    /// 系统 + 已导入的**全部**字体候选（已导入的排前面 —— 用户自己放的先看到）。
    /// </summary>
    /// <remarks>
    /// <b>这是"能不能选这个字体"的唯一事实来源</b>：界面候选列表与启动期的可用性判定
    /// （<c>ThemeService</c> 恢复偏好时）都读它，两处不会各有一套判据 ——
    /// 曾经的"启动期只认一张手写白名单"就是第二套判据（会把用户机器上真实存在的字体判成不可用）。
    /// </remarks>
    public static IReadOnlyList<FontChoice> All() =>
        ImportedFonts().Concat(SystemSource.Enumerate()).ToList();

    /// <summary>
    /// 在**后台线程**装载候选列表（首次进「外观」面板 / 展开字体下拉时调用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须异步 + 后台</b>：全量枚举是"与已装字体数量成正比"的开销
    /// （缓存只解决第二次之后；第一次无论如何都要付）。把它付在 UI 线程上 =
    /// 用户每装一批字体就多卡一次，且这类卡顿随机器变差而放大 ——
    /// <b>不能靠"大多数机器上很快"来免责</b>。
    /// </para>
    /// <para>
    /// 失败**如实抛出**（不返回空列表假装"没有字体"）：调用方负责把消息播报给用户。
    /// </para>
    /// </remarks>
    public static Task<IReadOnlyList<FontChoice>> LoadAsync() => Task.Run(All);

    /// <summary>枚举已导入字体（按文件名排序）。</summary>
    /// <remarks>
    /// <para>
    /// <b>必须缓存，且只在导入/删除时失效</b>：每项都要把字体文件交给
    /// <c>Fonts.GetFontFamilies(Uri)</c> 解析族名 —— 那是**打开并解析字体表**的开销（几十~几百毫秒/个）。
    /// 而本方法被候选装载与启动期可用性判定反复调用。
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

    /// <summary>
    /// 测试收尾复位：来源覆盖与两个缓存一起回默认。
    /// </summary>
    /// <remarks>
    /// ⚠️ 必须连**缓存**一起清：只复位 <see cref="SystemSource"/> 而留着族名集合，
    /// 会让下一个用例继续看到上一个用例注入的假列表 —— 表现为"测试结果取决于执行顺序"的偶发红
    /// （同族教训见 <c>ThemeService.ResetForTests</c> 的注释）。
    /// </remarks>
    public static void ResetForTests()
    {
        _sourceOverride = null;
        InvalidateSystemFamilySet();
        InvalidateImportedCache();
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
            throw new ArgumentException("font file path is empty", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("font file does not exist", sourcePath);

        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not (".ttf" or ".otf" or ".ttc"))
            throw new InvalidOperationException($"only .ttf / .otf / .ttc font files are supported (got {ext})");

        // 先验证"它真是字体"再落盘：避免把坏文件留在字体目录里污染后续枚举
        if (!TryResolveFileFamily(sourcePath, out var family))
            throw new InvalidOperationException("the file could not be parsed as a font (it may be corrupt or not a font at all)");

        Directory.CreateDirectory(FontDirectory);
        var target = Path.Combine(FontDirectory, Path.GetFileName(sourcePath));
        // ⚠️ 先写临时文件再原子改名：直接 File.Copy 到最终路径时，磁盘满 / 中断会留下**半截字体文件**——
        //    它既不在下拉里（枚举跳过坏文件）、也删不掉（用户看不到它），只能在字体目录里烂着。
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.Copy(sourcePath, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDeleteTemp(temp);
            LpLog.Error($"font import failed: {sourcePath}", ex, LogCategory);
            throw new InvalidOperationException($"copying the font file failed: {ex.Message}", ex);
        }

        InvalidateImportedCache();   // 导入后立即可见
        LpLog.Info($"Imported font '{family}' -> {target}", LogCategory);
        return new FontChoice(family, $"{Path.GetFileNameWithoutExtension(target)} · {family}", target);
    }

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // 清临时文件尽力而为：删不掉也不能顶替"导入失败"这个原始异常（观测面红线）
            LpLog.Warn($"cleanup of the imported temporary file failed: {path}", ex, LogCategory);
        }
    }

    /// <summary>
    /// 删除一个**用户导入**的字体文件（系统字体不可删——它不在本应用目录里，永远删不到）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>事实（回答"删除字体是删系统的吗"）</b>：只删 <see cref="FontDirectory"/> 里那份**副本**，
    /// 全仓唯一 <c>File.Delete</c> 就在本方法；没有任何路径能删到 <c>C:\Windows\Fonts</c>。
    /// </para>
    /// <para>
    /// <b>文件已不在时照常作废缓存并如实返回 false</b>：旧实现在文件不存在时直接 no-op，
    /// 而调用方照样播报"已删除"—— 用户下次进面板它却还在（缓存没失效），自相矛盾。
    /// </para>
    /// </remarks>
    /// <returns>true = 真的删掉了磁盘文件；false = 文件本来就不在（缓存已作废，界面会跟着变）。</returns>
    public static bool Remove(FontChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (choice.FilePath is null)
            throw new InvalidOperationException("system fonts cannot be deleted");

        var deleted = false;
        if (File.Exists(choice.FilePath))
        {
            File.Delete(choice.FilePath);   // 删不掉照抛（由 VM 播报原因，不静默）
            deleted = true;
        }
        InvalidateImportedCache();          // ⚠️ 放在 if 之外：文件不在也要让界面刷新
        LpLog.Info($"deleted imported font: {choice.FilePath} (file {(deleted ? "removed" : "never existed")})", LogCategory);
        return deleted;
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

    /// <summary>默认界面字体族：<b>中文界面</b>（= 改造前 <c>AppFont</c> 的第一段）。</summary>
    public const string ZhDefaultUiFamily = "Microsoft YaHei UI";

    /// <summary>默认界面字体族：<b>英文界面</b>。</summary>
    public const string EnDefaultUiFamily = "Segoe UI";

    /// <summary>
    /// 按界面语言给默认字体族（决策 5）：中文 = <see cref="ZhDefaultUiFamily"/>、英文 = <see cref="EnDefaultUiFamily"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么参数是语言码字符串</b>：本层（Theming）不引 <c>LinkPocket.I18n</c>，
    /// 也不该引——语言语义归 I18n，这里只认"哪个默认族"这一个事实。
    /// 取值域见 <c>Contracts.LocalePreference.CodeZh</c> / <c>CodeEn</c>；
    /// 认不出来（<c>null</c> / 未知码）走中文，与"出厂缺省 = 简体中文"同一条口径。
    /// </para>
    /// <para>
    /// <b>回退链两个方向都兜得住</b>（<see cref="BuildTokenValue"/>）：主族是 Segoe UI 时，
    /// 缺 CJK 字形仍会落到链上的雅黑；主族是雅黑时，缺的拉丁字形本来就由系统兜底。
    /// </para>
    /// </remarks>
    public static string DefaultUiFamily(string? languageCode)
        => IsEnglish(languageCode) ? EnDefaultUiFamily : ZhDefaultUiFamily;

    /// <summary>这个语言码是不是英文（只认 <c>en</c> / <c>en-*</c>；其余一律走中文默认族）。</summary>
    private static bool IsEnglish(string? languageCode)
        => !string.IsNullOrWhiteSpace(languageCode)
           && languageCode.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase);

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
                LpLog.Warn($"font file could not be parsed ({path}): {ex.Message}", category: LogCategory);
            return false;
        }
    }
}
