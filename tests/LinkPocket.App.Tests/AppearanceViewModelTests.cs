using System.IO;
using System.Linq;
using System.Windows.Media;
using LinkPocket.Theming;
using LinkPocket.Theming.Fonts;
using LinkPocket.Theming.Preferences;
using LinkPocket.Theming.Themes;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 「外观」面板视图模型（T5）：主题卡 / 自选配色槽 / 字体 / 选择投影 / 恢复默认。
/// </summary>
/// <remarks>
/// <para>
/// <b>测试边界（重要，本文件刻意做成这样）</b>：
/// 这些用例**只断言可观测结果**（集合内容、能力位、颜色值、选中态、播报文案），
/// 并且**刻意把"改进程级全局"的动作压到最少**——<see cref="ThemeService"/> 是进程级单例、
/// 偏好是进程级文件，多个用例各自读改写它们就会互相踩（表现为测试宿主挂住/结果随顺序变）。
/// 因此：
/// </para>
/// <list type="bullet">
/// <item>多数用例只构造 VM 与纯函数（<see cref="PaletteSolver"/> / <see cref="ThemeValidator"/> /
/// <see cref="ColorMath"/>），**不应用主题、不写偏好**；</item>
/// <item>只有 3 个"必须验证落盘/应用"的用例才碰全局，且各自在 <see cref="Dispose"/> 里复位；</item>
/// <item><b>绝不触发系统字体枚举</b>（<c>EnsureFontsLoaded</c> → <c>Fonts.SystemFontFamilies</c>）：
/// 那条路径会拉起 WPF 字体缓存服务等进程级副作用，在测试宿主里会挂住不退
/// （实测：一旦触发，宿主 20s+ 不退出，CI 看起来"卡死"）。字体**候选列表**由 SmartProbe 在真实窗口覆盖。</item>
/// </list>
/// </remarks>
[Collection(UiPreferencesCollection.Name)]
public class AppearanceViewModelTests : IDisposable
{
    private readonly string _path = UiPreferenceStore.FilePath;
    private readonly string? _backup;

    public AppearanceViewModelTests()
    {
        _backup = File.Exists(_path) ? File.ReadAllText(_path) : null;
    }

    public void Dispose()
    {
        // 复位到"用例开始前"的状态（文件原样还回去；不存在就删掉）
        if (_backup is null) UiPreferenceStore.Clear();
        else File.WriteAllText(_path, _backup);
    }

    private static AppearanceViewModel NewVm() => new();

    /// <summary>测试用字体选项（**不枚举系统字体**，直接构造）。</summary>
    private static FontOptionViewModel FontOption(string family) => new(new FontChoice(family, family));

    // ── 主题卡 ───────────────────────────────────────────────────────

    [Fact]
    public void 主题卡_11张_出厂默认排第一且带默认能力位()
    {
        var vm = NewVm();
        Assert.Equal(11, vm.ThemeCards.Count);
        Assert.True(vm.ThemeCards[0].IsDefault, "出厂默认必须排第一");
        Assert.Equal(ThemeCatalog.DefaultId, vm.ThemeCards[0].Id);
        Assert.Single(vm.ThemeCards, c => c.IsDefault);
    }

    [Fact]
    public void 主题卡_每张都有身份色圆点与派生示意条()
    {
        // 预设身份色只有 1–2 个，主题卡会用该主题自己的色调板补足到 4 个
        // （"主题就是这些颜色"，不足 4 个时面板会显得像"缺了几个色"）
        var vm = NewVm();
        Assert.All(vm.ThemeCards, c => Assert.True(c.Swatches.Count >= 4, $"{c.Name} 身份色圆点不足：{c.Swatches.Count}"));
        Assert.All(vm.ThemeCards, c => Assert.False(string.IsNullOrWhiteSpace(c.Summary), $"{c.Name} 缺派生摘要"));
    }

    [Fact]
    public void 选择投影_默认只有出厂默认卡被选中()
    {
        var vm = NewVm();
        Assert.True(vm.ThemeCards[0].IsSelected);
        Assert.Single(vm.ThemeCards, c => c.IsSelected);
    }

    // ── 色槽（纯 VM 逻辑，不碰全局）─────────────────────────────────

    [Fact]
    public void 色槽_默认等于出厂默认身份色数_上下限为4与5()
    {
        var vm = NewVm();
        Assert.Equal(ThemeCatalog.Default.Palette.Count, vm.SlotCount);
        Assert.InRange(vm.SlotCount, AppearanceViewModel.MinSlots, AppearanceViewModel.MaxSlots);
        Assert.False(vm.CanAddSlot);
        Assert.True(vm.CanRemoveSlot);

        vm.RemoveSlot();
        Assert.Equal(4, vm.SlotCount);
        Assert.True(vm.CanAddSlot);
        Assert.False(vm.CanRemoveSlot);

        vm.AddSlot();
        Assert.Equal(5, vm.SlotCount);
    }

    [Fact]
    public void 色槽_下限4_减不到3()
    {
        var vm = NewVm();
        while (vm.CanRemoveSlot) vm.RemoveSlot();
        vm.RemoveSlot();
        Assert.Equal(AppearanceViewModel.MinSlots, vm.SlotCount);
    }

    [Fact]
    public void 改色槽_即时生效且走唯一来源()
    {
        var vm = NewVm();
        var target = Color.FromRgb(0x12, 0x34, 0x56);
        vm.SetSlotColor(0, target);

        Assert.Equal(target, vm.Slots[0].Color);
        Assert.Equal("#123456", vm.Slots[0].Hex);
    }

    [Fact]
    public void 改色槽_越界索引_无副作用()
    {
        var vm = NewVm();
        var before = vm.Slots.Select(s => s.Color).ToList();
        vm.SetSlotColor(-1, Colors.Red);
        vm.SetSlotColor(99, Colors.Red);
        Assert.Equal(before, vm.Slots.Select(s => s.Color).ToList());
    }

    // ── 诊断（纯函数）───────────────────────────────────────────────

    [Fact]
    public void 诊断_全浅配色提示无深色_但不是阻断()
    {
        var vm = NewVm();
        foreach (var i in Enumerable.Range(0, 4))
            vm.SetSlotColor(i, Color.FromRgb((byte)(0xE8 + i), (byte)(0xE8 + i), (byte)(0xE8 + i)));
        vm.RefreshDraftDiagnostics();

        Assert.True(vm.HasDiagnostics);
        Assert.Contains("没有深色", vm.Diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("✗", vm.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void 诊断_合法配色无诊断()
    {
        // 用出厂默认的身份色（保证合法）→ 应无任何诊断
        var vm = NewVm();
        var palette = ThemeCatalog.Default.Palette;
        for (var i = 0; i < vm.SlotCount && i < palette.Count; i++)
        {
            var v = palette[i].ToInt();
            vm.SetSlotColor(i, Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)));
        }
        vm.RefreshDraftDiagnostics();
        Assert.False(vm.HasDiagnostics, vm.Diagnostics);
    }

    // ── 字体逻辑 ────────────────────────────────────────────────────

    [Fact]
    public void 未装载字体候选时_面板状态可用且不崩()
    {
        // 候选为空是**合法状态**（惰性：用户没展开下拉就不该付全量枚举的钱）
        var vm = NewVm();
        Assert.Empty(vm.UiFonts);
        Assert.Empty(vm.MonoFonts);
        Assert.Equal(11, vm.ThemeCards.Count);
    }

    [Fact]
    public async Task 字体候选_界面与等宽都有候选_且投影当前字体()
    {
        // ⚠️ 这条用例**走真实系统字体枚举**（生产字体来源），断言"枚举 + 候选 + 投影"整条链路真的成立。
        //    它曾经因为"据说会吊住测试宿主"被换成两个弱用例（只断言"能往集合里塞假项"），
        //    等于把"枚举路径有没有坏"的覆盖让给了探针 —— 而探针只在 UI 改动时才跑。
        //    现在枚举在后台线程（FontCatalog.LoadAsync）+ 实例内缓存，这条覆盖必须留着。
        ThemeService.ResetForTests();
        var vm = NewVm();

        await vm.EnsureFontsLoadedAsync();

        Assert.NotEmpty(vm.UiFonts);
        Assert.NotEmpty(vm.MonoFonts);
        Assert.Equal(vm.UiFonts.Count, vm.MonoFonts.Count);   // 两个下拉共用同一份候选

        // 投影：当前已应用字体必须在候选里被选中（否则下拉看起来"没生效"）
        Assert.NotNull(vm.SelectedUiFont);
        Assert.Equal(ThemeService.CurrentUiFont, vm.SelectedUiFont!.Family, ignoreCase: true);
        Assert.NotNull(vm.SelectedMonoFont);
        Assert.Equal(ThemeService.CurrentMonoFont, vm.SelectedMonoFont!.Family, ignoreCase: true);

        // 默认字体一定在候选里（回退链承诺它存在）
        Assert.Contains(vm.UiFonts, f => f.Family == FontCatalog.DefaultUiFamily);
    }

    [Fact]
    public async Task 字体候选_二次装载命中缓存_不重复枚举()
    {
        // 缓存是"进一次页面枚举一次"这条性能缺陷的解药，必须被钉住。
        ThemeService.ResetForTests();
        var source = new CountingSystemFontSource(new FontChoice("Fake UI", "Fake UI"));
        try
        {
            FontCatalog.SystemSource = source;
            var vm = NewVm();
            await vm.EnsureFontsLoadedAsync();
            await vm.EnsureFontsLoadedAsync();          // 幂等：不该再枚举
            Assert.Equal(1, source.EnumerateCalls);
            Assert.Contains(vm.UiFonts, f => f.Family == "Fake UI");
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public async Task 导入字体失败_状态行给出用户可见原因_且不写偏好()
    {
        // N1 要求"导入失败的用户可见反馈"：失败必须写进 Status（面板显示在状态行上），
        // 而不是只写日志（那样用户点了「导入字体…」什么都没发生）。
        ThemeService.ResetForTests();
        var vm = NewVm();
        var temp = Path.Combine(Path.GetTempPath(), "lp-font-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var fake = Path.Combine(temp, "not-a-font.ttf");
            File.WriteAllText(fake, "这不是字体");

            var ok = await vm.ImportFontAsync(fake);

            Assert.False(ok);
            Assert.Contains("导入失败", vm.Status, StringComparison.Ordinal);
            Assert.False(File.Exists(UiPreferenceStore.FilePath), "导入失败不得写偏好");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* 测试自清尽力而为 */ }
        }
    }

    /// <summary>会数枚举次数的假来源（钉住"缓存生效、不重复枚举"）。</summary>
    private sealed class CountingSystemFontSource(params FontChoice[] fonts) : ISystemFontSource
    {
        public int EnumerateCalls { get; private set; }

        public IReadOnlyList<FontChoice> Enumerate()
        {
            EnumerateCalls++;
            return fonts;
        }
    }

    [Fact]
    public async Task 删除系统字体_明确拒绝并播报()
    {
        var vm = NewVm();
        await vm.DeleteFontAsync(FontOption("Microsoft YaHei UI"));   // 无文件路径 = 系统字体
        Assert.Contains("系统字体不可删除", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void 字体自检_默认字体无告警()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.InspectFont(FontOption(FontCatalog.DefaultUiFamily)));
    }

    // ── 必须碰全局的 3 个用例（应用/落盘），各自靠 Dispose 复位 ──────

    [Fact]
    public void 应用主题卡_选中投影切到该卡且只有一张选中()
    {
        ThemeService.ResetForTests();
        var vm = NewVm();
        var matcha = vm.ThemeCards.First(c => c.Id == "uji-matcha");
        vm.ApplyThemeCard(matcha);

        Assert.Equal("uji-matcha", vm.SelectedThemeId);
        Assert.True(matcha.IsSelected);
        Assert.Single(vm.ThemeCards, c => c.IsSelected);
        Assert.Contains("宇治抹茶", vm.Status, StringComparison.Ordinal);
        Assert.Equal("uji-matcha", ThemeService.Current.Id);

        var prefs = UiPreferenceStore.Load(out var failed);
        Assert.False(failed);
        Assert.Equal("uji-matcha", prefs.Theme.Id);
    }

    [Fact]
    public void 应用自选配色_生效并落盘为自选()
    {
        ThemeService.ResetForTests();
        var vm = NewVm();
        var palette = ThemeCatalog.Default.Palette;
        for (var i = 0; i < vm.SlotCount && i < palette.Count; i++)
        {
            var v = palette[i].ToInt();
            vm.SetSlotColor(i, Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)));
        }
        vm.ApplyDraft();

        Assert.Equal("user-custom", vm.SelectedThemeId);
        Assert.Contains("已应用自选配色", vm.Status, StringComparison.Ordinal);

        var prefs = UiPreferenceStore.Load(out var failed);
        Assert.False(failed);
        Assert.True(prefs.Theme.IsCustom, "自选配色必须以 Colors 形式落盘");
        Assert.Equal(vm.SlotCount, prefs.Theme.Colors!.Count);
    }

    [Fact]
    public async Task 恢复默认外观_主题字体选择全回默认_且清偏好()
    {
        ThemeService.ResetForTests();
        ThemeService.ApplyById("sakura-panna", null);
        var vm = NewVm();
        Assert.Equal("sakura-panna", vm.SelectedThemeId);   // 入口对齐：显示当前已应用的主题

        await vm.ResetToDefaultAsync();

        Assert.Equal("已恢复默认外观", vm.Status);
        Assert.Equal(ThemeCatalog.DefaultId, vm.SelectedThemeId);
        Assert.Equal(ThemeCatalog.DefaultId, ThemeService.Current.Id);
        Assert.Equal(FontCatalog.DefaultUiFamily, ThemeService.CurrentUiFont);
        Assert.Equal(ThemeCatalog.Default.Palette.Count, vm.SlotCount);
        Assert.True(vm.ThemeCards.First(c => c.IsDefault).IsSelected);
        Assert.False(File.Exists(_path), "恢复默认外观必须清掉偏好文件");
    }

    [Fact]
    public void 应用字体_生效并落盘()
    {
        ThemeService.ResetForTests();
        var vm = NewVm();
        vm.SelectedUiFont = FontOption(FontCatalog.DefaultUiFamily);
        vm.SelectedMonoFont = FontOption(FontCatalog.DefaultMonoFamily);
        vm.ApplyFonts();

        Assert.Equal(FontCatalog.DefaultUiFamily, ThemeService.CurrentUiFont);
        var prefs = UiPreferenceStore.Load(out _);
        Assert.Equal(FontCatalog.DefaultUiFamily, prefs.Fonts.Ui);
    }
}
