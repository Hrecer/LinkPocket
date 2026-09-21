using System.IO;
using LinkPocket.Theming;
using LinkPocket.Theming.Fonts;
using LinkPocket.Theming.Preferences;
using LinkPocket.Theming.Themes;
using Material3.Core;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 字体系统与偏好持久化（方案 §6）：回退链、导入/删除、度量自检、原子写与损坏暴露。
/// </summary>
/// <remarks>
/// 这些用例**不碰用户目录**：导入目标目录由 <c>AppContext.BaseDirectory/fonts</c> 决定，
/// 测试只验证"回退链构造"与"坏文件拒绝"这类纯逻辑；真正的文件复制用例放在临时目录里做，
/// 用完自清（CONVENTIONS §5）。
/// </remarks>
[Collection(UiPreferencesCollection.Name)]
public class FontSystemTests : IDisposable
{
    private readonly string _path = UiPreferenceStore.FilePath;
    private readonly string? _backup;

    public FontSystemTests() => _backup = File.Exists(_path) ? File.ReadAllText(_path) : null;

    /// <summary>
    /// 收尾 = **进程内回出厂默认 + 偏好文件回跑前原样**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 顺序不可颠倒：<see cref="ThemeService.ResetForTests"/> 会连偏好文件一起清，
    /// 备份必须在它**之后**写回 —— 反了就等于把"跑前状态"清掉（跑前状态丢失）。
    /// 本类里碰 <c>ThemeService</c> / <c>FontCatalog</c>（都是进程级静态）的用例因此不会漏进下一个用例。
    /// </remarks>
    public void Dispose()
    {
        ThemeService.ResetForTests();
        if (_backup is null) UiPreferenceStore.Clear();
        else File.WriteAllText(_path, _backup);
    }

    /// <summary>
    /// **碰不到别的安装**的机器化判据：偏好文件永远只落在**本进程自己的安装目录**里。
    /// </summary>
    /// <remarks>
    /// 不变量：测试或探针跑完，进程内主题状态回到出厂默认、偏好文件回到跑前原样，
    /// 并且**碰不到别的安装**。这条把最后半句变成可执行断言：<c>UiPreferenceStore.FilePath</c>
    /// 必须由 <see cref="AppContext.BaseDirectory"/> 拼出（= 当前这份安装/构建输出的目录）——
    /// 一旦有人把它改成"用户目录 / 固定绝对路径 / 上一级目录"，测试与探针就会去改别人（或真实用户）的那份。
    /// </remarks>
    [Fact]
    public void 偏好文件_永远只写自己这一份安装的目录_不可能改到别的安装()
    {
        var installDir = Path.GetFullPath(AppContext.BaseDirectory);
        var filePath = Path.GetFullPath(UiPreferenceStore.FilePath);

        Assert.True(filePath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase),
            $"偏好文件必须落在本进程的安装目录里：file={filePath} install={installDir}");
        Assert.Equal("ui-preferences.json", Path.GetFileName(filePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 回退链_无主族时仍返回完整链_绝不为空(string? primary)
    {
        // 空 FontFamily 会让 WPF 静默回落默认字体（症状 = "换字体没反应"），属于静默失败。
        var value = FontCatalog.BuildTokenValue(primary);
        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.Contains("Microsoft YaHei UI", value, StringComparison.Ordinal);
        Assert.Contains("Segoe UI", value, StringComparison.Ordinal);
    }

    [Fact]
    public void 回退链_主族不在链里重复追加()
    {
        var value = FontCatalog.BuildTokenValue("Microsoft YaHei UI");
        Assert.Equal("Microsoft YaHei UI, Segoe UI", value);
    }

    [Fact]
    public void 回退链_自定义主族排在首位_CJK兜底在后()
    {
        // 这是"导入拉丁字体不会让中文变方块"的机制保证：WPF 逐字形回退到雅黑。
        var value = FontCatalog.BuildTokenValue("Cascadia Code");
        Assert.StartsWith("Cascadia Code,", value, StringComparison.Ordinal);
        Assert.Contains("Microsoft YaHei UI", value, StringComparison.Ordinal);
    }

    [Fact]
    public void 字体族构造_可被WPF解析()
    {
        var family = FontCatalog.BuildFontFamily("Cascadia Code");
        Assert.NotNull(family);
        Assert.False(string.IsNullOrWhiteSpace(family.Source));
    }

    [Fact]
    public void 导入到应用再到重启_外观从偏好恢复()
    {
        // N1 的端到端判据：**导入 → 应用 → 重启保持**。
        // "重启"在这里 = 丢掉进程内的主题/字体状态，只留偏好文件，再走 App.OnStartup 的那条恢复入口
        // （ThemeService.ApplyFromPreferences）—— 不是重新读一遍内存状态，那样测不出持久化。
        var source = PickRealFontFile();
        Assert.NotNull(source);   // 系统字体目录里必然有可导入的字体文件（见下方 PickRealFontFile）
        var path = UiPreferenceStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        var temp = Path.Combine(Path.GetTempPath(), "lp-font-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        FontChoice? imported = null;
        try
        {
            // ① 真实导入（走 FontCatalog.Import：解析族名 → 复制到 {BaseDirectory}/fonts/）。
            // ⚠️ 先改成**唯一文件名**再导：直接用 arial.ttf 会让用例在同一目录里反复覆盖，
            //    而 Windows 对"正被 WPF 打开的字体文件"不给覆写（实测 "无法在使用用户映射区域打开的文件上执行"）。
            var staged = Path.Combine(temp, "lp-import-probe-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(source!));
            File.Copy(source!, staged);
            imported = FontCatalog.Import(staged);
            Assert.True(imported.IsImported);
            Assert.Contains(FontCatalog.ImportedFonts(), f => f.Family == imported.Family);

            // ② 应用「导入字体 + 内置预设主题」并落盘
            ThemeService.ResetForTests();
            ThemeService.ApplyById("uji-matcha");
            ThemeService.ApplyFonts(imported.Family, FontCatalog.DefaultMonoFamily);
            ThemeService.SaveCurrentPreferences();

            // ③ 重启：只清进程内状态（**偏好文件保留** —— 这正是 ResetInMemoryForRestartTests 的用途），
            //    再走 App.OnStartup 的那条恢复入口
            ThemeService.ResetInMemoryForRestartTests();
            var (fellBack, reason) = ThemeService.ApplyFromPreferences();

            Assert.False(fellBack, $"不该回退（reason={reason}）");
            Assert.Equal("uji-matcha", ThemeService.Current.Id);
            Assert.Equal(imported.Family, ThemeService.CurrentUiFont);
            Assert.Equal(FontCatalog.DefaultMonoFamily, ThemeService.CurrentMonoFont);
        }
        finally
        {
            // ⚠️ **不删导入的字体文件**：WPF 的字体缓存把 `Fonts.GetFontFamilies(uri)` 解析过的文件
            //    用内存映射持有到进程退出（实测：删除抛 UnauthorizedAccessException
            //    "Access to the path … is denied"）。这是平台行为，不是产品缺陷 ——
            //    真实用户"导入 → 应用 →（不重启就）删除"时同样会遇到，届时 FontCatalog.Remove 会抛
            //    并把原因写进状态行（观测面纪律：暴露而不是静默失败）。
            //    用例留下的是一份无害的字体副本（名字带随机后缀），下一次运行用的是新名字，不会互相干扰。
            FontCatalog.ResetForTests();
            if (backup is null) UiPreferenceStore.Clear();
            else File.WriteAllText(path, backup);
            try { Directory.Delete(temp, recursive: true); } catch { /* 测试自清尽力而为 */ }
        }
    }

    /// <summary>
    /// 从系统字体目录里挑一个真实的 <c>.ttf/.otf/.ttc</c> 供导入用例使用。
    /// </summary>
    /// <remarks>
    /// 不用"测试自造一个字体文件"：手写字体二进制没有意义，而"非字体文件被拒绝"已由别的用例覆盖。
    /// 这里要的是**真实字体文件**走通"解析族名 → 复制 → 重启后仍可用"整条链路。
    /// </remarks>
    private static string? PickRealFontFile()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!Directory.Exists(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir)
            .FirstOrDefault(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc");
    }

    [Fact]
    public void 导入_非字体文件被明确拒绝_不静默跳过()
    {
        // 观测面口径：导入失败必须暴露（不写偏好、不静默跳过）。
        var temp = Path.Combine(Path.GetTempPath(), "lp-font-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var fake = Path.Combine(temp, "not-a-font.ttf");
            File.WriteAllText(fake, "这不是字体");
            var ex = Assert.Throws<InvalidOperationException>(() => FontCatalog.Import(fake));
            Assert.Contains("could not be parsed", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* 测试自清尽力而为 */ }
        }
    }

    [Fact]
    public void 导入_扩展名不符被明确拒绝()
    {
        var temp = Path.Combine(Path.GetTempPath(), "lp-font-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var txt = Path.Combine(temp, "readme.txt");
            File.WriteAllText(txt, "text");
            var ex = Assert.Throws<InvalidOperationException>(() => FontCatalog.Import(txt));
            Assert.Contains("only .ttf / .otf / .ttc", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* 测试自清尽力而为 */ }
        }
    }

    [Fact]
    public void 系统字体枚举_非空且按显示名排序()
    {
        // ⚠️ 这条**直接走生产字体来源**（WpfSystemFontSource → Fonts.SystemFontFamilies 全量枚举）：
        //    "真机枚举能出结果、且排好序"这件事必须有自动化覆盖，不能只测注入的假列表。
        var fonts = FontCatalog.SystemSource.Enumerate();
        Assert.NotEmpty(fonts);
        var names = fonts.Select(f => f.DisplayName).ToList();
        var sorted = names.OrderBy(n => n, StringComparer.CurrentCulture).ToList();
        Assert.Equal(sorted, names);
        Assert.All(fonts, f => Assert.False(string.IsNullOrWhiteSpace(f.Family)));
        Assert.All(fonts, f => Assert.False(f.IsImported));   // 系统来源不含导入项
    }

    [Fact]
    public void 回退链字体_在真实系统字体集合里可用()
    {
        // 回退链（雅黑 / Segoe UI）与默认等宽字体必须**一定存在**：
        // 若机器上真的没有，回退链就是一句空话 —— 用真实枚举把它钉住。
        var families = FontCatalog.SystemFontFamilies();
        Assert.Contains(FontCatalog.DefaultUiFamily, families);
        Assert.Contains(FontCatalog.DefaultMonoFamily, families);
        Assert.All(FontCatalog.FallbackChain, f => Assert.Contains(f, families));
    }

    [Fact]
    public void 候选装载_已导入字体排在系统字体之前()
    {
        // 用户自己放进来的字体要**先看到**（他刚导入完就要在下拉里找到它）——排序口径被断言。
        // 本机可能已经有导入字体（此前用例留下的），故这里把系统来源换成假列表做隔离，
        // 断言的是**分组顺序**（导入在前、系统在后），不是"总条数等于几"。
        try
        {
            FontCatalog.SystemSource = new FakeSystemFontSource(
                new FontChoice("Zzz System Font", "Zzz 系统字体"),
                new FontChoice("Aaa System Font", "Aaa 系统字体"));

            var all = FontCatalog.All();
            var importedCount = FontCatalog.ImportedFonts().Count;

            Assert.Equal(importedCount + 2, all.Count);
            // 前 importedCount 项 = 导入字体；其后 = 系统字体（分组顺序）
            Assert.All(all.Take(importedCount), f => Assert.True(f.IsImported));
            Assert.All(all.Skip(importedCount), f => Assert.False(f.IsImported));
            Assert.Contains(all, f => f.Family == "Aaa System Font");
        }
        finally
        {
            FontCatalog.ResetForTests();
        }
    }

    /// <summary>注入用的假系统字体来源（隔离缝；产品侧见 <c>FontCatalog.SystemSource</c> 注释）。</summary>
    private sealed class FakeSystemFontSource(params FontChoice[] fonts) : ISystemFontSource
    {
        /// <summary>枚举次数（用例可断言"只枚举一次"这类缓存不变量）。</summary>
        public int EnumerateCalls { get; private set; }

        public IReadOnlyList<FontChoice> Enumerate()
        {
            EnumerateCalls++;
            return fonts;
        }
    }

    [Fact]
    public void 度量自检_默认字体自身必须通过()
    {
        // 自检是"相对默认字体"的比较——拿默认字体比自己必然应**完全相等**（否则阈值本身就是错的）。
        var verdict = FontMetricsProbe.Inspect(FontCatalog.BuildTokenValue(FontCatalog.DefaultUiFamily), "LinkPocket 书签管理 0123");
        Assert.True(verdict.Ok, verdict.Code);
        Assert.Equal(0.0, verdict.WidthDelta, 6);   // 宽度相对偏差 = 0
        Assert.Equal(1.0, verdict.HeightRatio, 6);  // 行高倍率 = 1
    }

    [Fact]
    public void 度量自检_极端字体族名回落默认_不抛()
    {
        // 不存在的字体族会回落到回退链；自检必须仍能给出结论，不许抛（面板要实时调它）。
        var verdict = FontMetricsProbe.Inspect("__NoSuchFontFamily__", "LinkPocket Bookmarks 0123");
        Assert.False(string.IsNullOrWhiteSpace(verdict.Candidate.Text));
        Assert.True(verdict.Baseline.Width > 0);
    }

    [Fact]
    public void 偏好_往返一致()
    {
        var path = UiPreferenceStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            var prefs = new UiPreferences
            {
                Theme = new ThemePreference { Id = "uji-matcha" },
                Fonts = new FontPreference { Ui = "Microsoft YaHei UI", Mono = "Consolas" },
            };
            UiPreferenceStore.Save(prefs);
            var loaded = UiPreferenceStore.Load(out var failed);
            Assert.False(failed);
            Assert.Equal("uji-matcha", loaded.Theme.Id);
            Assert.Equal("Microsoft YaHei UI", loaded.Fonts.Ui);
            Assert.Equal("Consolas", loaded.Fonts.Mono);
            Assert.Equal(UiPreferences.CurrentVersion, loaded.Version);
        }
        finally
        {
            if (backup is null) UiPreferenceStore.Clear();
            else File.WriteAllText(path, backup);
        }
    }

    [Fact]
    public void 偏好_损坏文件回退默认并如实报告_不静默()
    {
        var path = UiPreferenceStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            File.WriteAllText(path, "{ 这不是合法 JSON");
            var loaded = UiPreferenceStore.Load(out var failed);
            Assert.True(failed, "损坏文件必须如实标记失败（观测面纪律）");
            Assert.Equal(UiPreferences.Default.Theme.Id, loaded.Theme.Id);
        }
        finally
        {
            if (backup is null) UiPreferenceStore.Clear();
            else File.WriteAllText(path, backup);
        }
    }

    [Fact]
    public void 偏好_版本不符回退默认并如实报告()
    {
        var path = UiPreferenceStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            File.WriteAllText(path, "{\"Version\":999,\"Theme\":{\"Id\":\"uji-matcha\"}}");
            var loaded = UiPreferenceStore.Load(out var failed);
            Assert.True(failed, "版本不符必须如实标记失败（零兼容：不做迁移）");
            Assert.Null(loaded.Theme.Id);
        }
        finally
        {
            if (backup is null) UiPreferenceStore.Clear();
            else File.WriteAllText(path, backup);
        }
    }

    [Fact]
    public void 偏好_文件不存在即默认_不算失败()
    {
        var path = UiPreferenceStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            UiPreferenceStore.Clear();
            var loaded = UiPreferenceStore.Load(out var failed);
            Assert.False(failed);
            Assert.Equal(FontCatalog.DefaultUiFamily, loaded.Fonts.Ui);
        }
        finally
        {
            if (backup is not null) File.WriteAllText(path, backup);
        }
    }

    [Fact]
    public void 无临时残留_原子写不留tmp()
    {
        var path = UiPreferenceStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            UiPreferenceStore.Save(UiPreferences.Default);
            Assert.False(File.Exists(path + ".tmp"), "原子写必须清掉 .tmp");
        }
        finally
        {
            if (backup is null) UiPreferenceStore.Clear();
            else File.WriteAllText(path, backup);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }
}

/// <summary>
/// 主题校验与诊断（方案 §5.3 末段）：拒绝该拒的、提示该提示的。
/// </summary>
/// <remarks>不碰偏好文件 → 不需要挂 <c>UiPreferencesCollection</c>（只有真正共享那个文件的类才串行）。</remarks>
public class ThemeValidatorTests
{
    private static ThemeDefinition Custom(params int[] rgb) => new()
    {
        Id = "t",
        Source = ThemeSource.UserDefined,
        Palette = rgb.Select(v => Argb.FromInt(unchecked((int)(0xFF000000u | (uint)v)))).ToArray(),
        NeutralHueOverride = ThemeDefinition.ReferenceNeutralHue,
    };

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(1)]
    public void 色数不是4或5_拒绝级(int count)
    {
        var issues = ThemeValidator.Validate(Custom(Enumerable.Range(0, count).Select(i => 0x405060 + i * 0x101010).ToArray()));
        Assert.True(ThemeValidator.HasErrors(issues), $"{count} 色应当被拒绝");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void 色数4或5_无阻断(int count)
    {
        var issues = ThemeValidator.Validate(Custom(Enumerable.Range(0, count).Select(i => 0x405060 + i * 0x101010).ToArray()));
        Assert.False(ThemeValidator.HasErrors(issues));
    }

    [Fact]
    public void 全部很浅_提示无深色_但不是错误()
    {
        // 派生管线会按规则自动加深出强调色（T40 档），所以只提示、不拒绝。
        var issues = ThemeValidator.Validate(Custom(0xE8E8E8, 0xEDEDED, 0xF0F0F0, 0xF5F5F5));
        Assert.False(ThemeValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Code == "no-dark");
    }

    [Fact]
    public void 全部很深_提示无浅色_说明背景由色相派生()
    {
        var issues = ThemeValidator.Validate(Custom(0x202020, 0x252525, 0x2A2A2A, 0x303030));
        Assert.False(ThemeValidator.HasErrors(issues));
        var noLight = issues.First(i => i.Code == "no-light");
        Assert.Contains("background is derived", noLight.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 两色几乎一样_提示可去掉一个()
    {
        var issues = ThemeValidator.Validate(Custom(0x6A567C, 0x6B577D, 0x201F22, 0xF2EEF5));
        Assert.Contains(issues, i => i.Code == "near-duplicate");
    }

    [Fact]
    public void 出厂默认主题_无任何诊断()
    {
        Assert.Empty(ThemeValidator.Validate(ThemeCatalog.Default));
    }

    [Fact]
    public void 全部内置预设_均无阻断级问题()
    {
        // 预设身份色是 2–3 个（标定收敛的结果，见 ThemeCatalog.Presets 注释），
        // 故这里**跳过"色数"这条规则**——它是给用户自选配色（4/5 色槽）定的门槛。
        foreach (var theme in ThemeCatalog.All)
        {
            var issues = ThemeValidator.Validate(theme)
                .Where(i => i.Code != "palette-size")
                .ToList();
            Assert.False(ThemeValidator.HasErrors(issues),
                $"{theme.Id} 有阻断级问题：{string.Join("；", issues.Where(i => i.Severity == ThemeIssueSeverity.Error).Select(i => i.Message))}");
        }
    }

    [Fact]
    public void 出厂默认主题_身份色为5个_满足用户自选口径()
    {
        // 出厂默认是"用户可再编辑"的主题（5 个身份色）——它与预设是两条不同路径。
        Assert.Equal(5, ThemeCatalog.Default.Palette.Count);
        Assert.False(ThemeValidator.HasErrors(ThemeValidator.Validate(ThemeCatalog.Default)));
    }

    [Theory]
    [InlineData("#6A567C", true)]
    [InlineData("6A567C", true)]
    [InlineData("#abc", true)]
    [InlineData("#GGGGGG", false)]
    [InlineData("#12345", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HEX解析_非法一律false不猜(string? text, bool ok)
    {
        Assert.Equal(ok, ThemeValidator.TryParseHex(text, out _));
    }

    [Fact]
    public void HEX格式化_往返一致()
    {
        Assert.True(ThemeValidator.TryParseHex("#6A567C", out var c));
        Assert.Equal("#6A567C", ThemeValidator.ToHex(c));
    }
}
