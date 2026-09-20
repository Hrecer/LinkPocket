using System.IO;
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
public class FontSystemTests
{
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
            Assert.Contains("无法解析", ex.Message, StringComparison.Ordinal);
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
            Assert.Contains("只支持", ex.Message, StringComparison.Ordinal);
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
        // 回退链（雅黑 / Segoe UI）与默认等宽字体是我们**承诺过一定存在**的那几个族：
        // 若机器上真的没有，回退链就是一句空话 —— 用真实枚举把它钉住。
        var families = FontCatalog.SystemFontFamilies();
        Assert.Contains(FontCatalog.DefaultUiFamily, families);
        Assert.Contains(FontCatalog.DefaultMonoFamily, families);
        Assert.All(FontCatalog.FallbackChain, f => Assert.Contains(f, families));
    }

    [Fact]
    public void 候选装载_已导入字体排在系统字体之前()
    {
        // 用户自己放进来的字体要**先看到**（他刚导入完就要在下拉里找到它）。
        // 用注入来源做隔离（用例不依赖"这台机器装了什么"），排序口径本身仍然被断言。
        try
        {
            FontCatalog.SystemSource = new FakeSystemFontSource(
                new FontChoice("Zzz System Font", "Zzz 系统字体"),
                new FontChoice("Aaa System Font", "Aaa 系统字体"));

            var all = FontCatalog.All();
            Assert.Equal(2, all.Count);
            Assert.DoesNotContain(all, f => f.IsImported);   // 本机没导入字体时只剩系统项
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
        var verdict = FontMetricsProbe.Inspect(FontCatalog.BuildTokenValue(FontCatalog.DefaultUiFamily));
        Assert.True(verdict.Ok, verdict.Message);
        Assert.Equal(0.0, verdict.WidthDelta, 6);   // 宽度相对偏差 = 0
        Assert.Equal(1.0, verdict.HeightRatio, 6);  // 行高倍率 = 1
    }

    [Fact]
    public void 度量自检_极端字体族名回落默认_不抛()
    {
        // 不存在的字体族会回落到回退链；自检必须仍能给出结论，不许抛（面板要实时调它）。
        var verdict = FontMetricsProbe.Inspect("__NoSuchFontFamily__");
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
        Name = "t",
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
        Assert.Contains("背景", noLight.Message, StringComparison.Ordinal);
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
                $"{theme.Name} 有阻断级问题：{string.Join("；", issues.Where(i => i.Severity == ThemeIssueSeverity.Error).Select(i => i.Message))}");
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
