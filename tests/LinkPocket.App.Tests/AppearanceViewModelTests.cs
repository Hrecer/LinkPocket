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
        // 收尾 = **进程内复位 + 偏好文件还原**，顺序不能反（用户令 2026-09-20："任何测试/探针跑完，
        // 进程内主题状态回到出厂默认、偏好文件回到跑前原样"）：
        //   ① `ThemeService.ResetForTests()` 会连偏好文件一起清 —— 它负责"进程内回默认"；
        //   ② 只有在这个**之后**才能把备份写回去，否则刚还回去的"跑前状态"立刻被 ① 清掉。
        ThemeService.ResetForTests();

        // ② 复位到"用例开始前"的文件状态（原样还回去；不存在就保持不存在）
        if (_backup is null) UiPreferenceStore.Clear();
        else File.WriteAllText(_path, _backup);
    }

    private static AppearanceViewModel NewVm() => new();

    /// <summary>测试用字体选项（**不枚举系统字体**，直接构造）。</summary>
    private static FontOptionViewModel FontOption(string family) => new(new FontChoice(family, family));

    // ── 主题卡 ───────────────────────────────────────────────────────

    [Fact]
    public void 主题卡_12张_出厂默认排第一_最后一张是自选颜色卡()
    {
        var vm = NewVm();
        Assert.Equal(12, vm.ThemeCards.Count);          // 11 套目录主题 + 第 12 张「自选颜色」
        Assert.True(vm.ThemeCards[0].IsDefault, "出厂默认必须排第一");
        Assert.Equal(ThemeCatalog.DefaultId, vm.ThemeCards[0].Id);
        Assert.Single(vm.ThemeCards, c => c.IsDefault);

        // 最后一张 = 「自选颜色」卡：**不是目录里的主题**（ThemeCatalog 仍是 11 套）
        var last = vm.ThemeCards[^1];
        Assert.True(last.IsCustom, "最后一张必须是自选颜色卡（IsCustom）");
        Assert.Equal("user-custom", last.Id);
        Assert.Equal("自选颜色", last.Name);
        Assert.Null(last.Definition);                    // 没有目录定义：点它 = 应用调色台的草稿
        Assert.Equal(11, ThemeCatalog.All.Count);        // 目录本身没有多出一套"自选颜色"

        // 默认**全空**：没有任何自选配色 → 卡面走空态占位，不显示任何颜色
        Assert.Empty(last.Swatches);
        Assert.True(last.IsEmpty, "自选颜色卡默认必须是空态（卡面画 4 个空心占位圆）");
    }

    [Fact]
    public void 主题卡_每张都有身份色圆点与派生示意条()
    {
        // 预设身份色只有 1–2 个，主题卡会用该主题自己的色调板补足到 4 个
        // （"主题就是这些颜色"，不足 4 个时面板会显得像"缺了几个色"）
        var vm = NewVm();
        var presets = vm.ThemeCards.Where(c => !c.IsCustom).ToList();
        Assert.Equal(11, presets.Count);
        Assert.All(presets, c => Assert.True(c.Swatches.Count >= 4, $"{c.Name} 身份色圆点不足：{c.Swatches.Count}"));
        Assert.All(presets, c => Assert.False(string.IsNullOrWhiteSpace(c.Summary), $"{c.Name} 缺派生摘要"));
        Assert.All(presets, c => Assert.False(c.IsEmpty, $"{c.Name} 不该是空态"));

        // 自选颜色卡反过来：没有配色时**一个色点都不显示**（占位态），也没有派生示意条
        var custom = vm.ThemeCards[^1];
        Assert.Empty(custom.Swatches);
        Assert.False(custom.ShowDerivedStrip);
    }

    [Fact]
    public void 选择投影_默认只有出厂默认卡被选中()
    {
        var vm = NewVm();
        Assert.True(vm.ThemeCards[0].IsSelected);
        Assert.Single(vm.ThemeCards, c => c.IsSelected);
        Assert.False(vm.ThemeCards[^1].IsSelected, "自选颜色卡默认不该亮着");
    }

    [Fact]
    public void 色槽_预设只读_不再把当前主题自动载入色槽()
    {
        // 用户令 2026-09-20："默认的颜色是绝对不能改的，但是我们可以多出一个按钮，
        // 我们可以把默认的某个主题作为我们自选色的方案"。
        // 旧行为（已废）= 进面板就把当前主题的颜色倒进色槽 —— 看起来像"预设可以被就地改"，
        // 而且与"自选是另一份配色"完全对不上。新语义：预设只读，色槽保持用户自己的草稿（从没有过 = 空）。
        ThemeService.ResetForTests();
        try
        {
            ThemeService.ApplyById("uji-matcha", null);
            var vm = NewVm();

            Assert.Equal("宇治抹茶", vm.AppliedThemeName);
            Assert.Equal(AppearanceViewModel.MinSlots, vm.SlotCount);
            Assert.All(vm.Slots, s => Assert.True(s.IsEmpty, "预设生效时色槽不该被自动填色"));
            Assert.All(vm.Slots, s => Assert.Equal(string.Empty, s.Hex));
            Assert.False(vm.IsCustomActive, "没载入更没应用：当前生效的仍是那个预设");
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 起点_以当前主题为起点_把颜色复制进色槽_且预设定义逐项未变()
    {
        // 「以当前主题为起点」= **复制**（不是引用、不是就地改）：这是"默认主题不可改"的硬判据 ——
        // 复制完再怎么编辑，ThemeCatalog 里那套预设的 Palette 必须逐项不变。
        ThemeService.ResetForTests();
        try
        {
            ThemeService.ApplyById("uji-matcha", null);
            var matcha = ThemeCatalog.Find("uji-matcha")!;
            var before = matcha.Palette.ToList();

            var vm = NewVm();
            Assert.Equal("以「宇治抹茶」为起点", vm.StartFromCurrentThemeLabel);

            vm.StartFromCurrentTheme();

            var expected = PaletteSolver.EditableSlots(matcha);
            Assert.Equal(expected.Count, vm.SlotCount);
            Assert.Equal(expected.Count, vm.Slots.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                var v = expected[i].ToInt();
                var hex = $"#{(v >> 16) & 0xFF:X2}{(v >> 8) & 0xFF:X2}{v & 0xFF:X2}";
                Assert.Equal(hex, vm.Slots[i].Hex);
                Assert.True(vm.Slots[i].HasColor);
            }

            Assert.True(vm.IsDraftEditing, "复制进来的只是草稿：必须显示「编辑中（未应用）」");
            Assert.False(vm.IsCustomActive, "起点不应用 —— 当前生效的仍是那个预设");

            // 硬判据：预设定义逐项未变（复制而非引用）
            Assert.Equal(before, matcha.Palette);
            Assert.Equal(before, ThemeCatalog.Find("uji-matcha")!.Palette);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 起点_切预设卡不清空草稿()
    {
        // 调色台是用户自己的草稿：切主题（预设只读、单击即应用）**不许**把它冲掉。
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            var first = Color.FromRgb(0x11, 0x22, 0x33);
            var second = Color.FromRgb(0x44, 0x55, 0x66);
            vm.SetSlotColor(0, first);
            vm.SetSlotColor(1, second);

            vm.ApplyThemeCard(vm.ThemeCards.First(c => c.Id == "uji-matcha"));

            Assert.Equal(first, vm.Slots[0].Color);
            Assert.Equal(second, vm.Slots[1].Color);
            Assert.Equal(2, vm.Slots.Count(s => s.HasColor));
            Assert.True(vm.IsDraftEditing, "草稿还是「未应用」的那份");
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 自选颜色卡_有配色时显示色点_空槽时显示未选()
    {
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            var custom = vm.ThemeCards[^1];
            Assert.True(custom.IsEmpty);

            vm.SetSlotColor(0, Color.FromRgb(0x11, 0x22, 0x33));

            Assert.False(custom.IsEmpty);
            Assert.Single(custom.Swatches);                     // 卡面只显示**已选**的颜色
            Assert.Equal(Color.FromRgb(0x11, 0x22, 0x33), custom.Swatches[0]);
            Assert.Equal("未选", vm.Slots[1].ValueText);         // 空槽不显示假色值
            Assert.Equal(string.Empty, vm.Slots[1].Hex);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 应用门槛_空槽时拒绝应用并说明还差几个()
    {
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            Assert.All(vm.Slots, s => Assert.True(s.IsEmpty));

            vm.ApplyDraft();

            Assert.False(vm.IsCustomActive, "空槽必须被拒绝：不应用");
            Assert.False(vm.ThemeCards[^1].IsSelected, "被拒绝时自选颜色卡不许高亮");
            Assert.Contains("4 个颜色没选", vm.Status, StringComparison.Ordinal);
            Assert.False(File.Exists(_path), "被拒绝不得写偏好");

            // 只补 3 个也不行（门槛 = 每一格都有颜色）
            for (var i = 0; i < 3; i++) vm.SetSlotColor(i, Color.FromRgb((byte)(0x40 + i), 0x50, 0x60));
            vm.ApplyDraft();
            Assert.False(vm.IsCustomActive);
            Assert.Contains("还有 1 个颜色没选", vm.Status, StringComparison.Ordinal);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 空槽态_诊断是中性提示而不是红色错误()
    {
        // 空槽 = "还没选完"，**不是**"配色不合法"（用户令 2026-09-20 的空态语义）。
        // 反例（已修）：空态下诊断框显示「✗ 主题需要 4 或 5 个颜色（当前 0 个）」—— 一进面板就报错，像是用户做错了什么。
        var vm = NewVm();
        vm.SetSlotCount(4);
        vm.RefreshDraftDiagnostics();

        Assert.True(vm.HasDiagnostics);
        Assert.DoesNotContain("✗", vm.Diagnostics, StringComparison.Ordinal);
        Assert.Contains("还差", vm.Diagnostics, StringComparison.Ordinal);

        // 走「应用」入口也是同一口径（状态行说明 + 中性提示，不摆红色错误）
        vm.ApplyDraft();
        Assert.Contains("没选", vm.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("✗", vm.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void 自选颜色卡_点它等于应用自选配色()
    {
        // 第 12 张卡的应用路径与「应用这套外观」**同一入口**（ApplyDraft）：合法才生效、才落盘。
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            var custom = vm.ThemeCards[^1];

            vm.ApplyThemeCard(custom);              // 草稿全空 → 拒绝
            Assert.False(vm.IsCustomActive);
            Assert.Contains("没选", vm.Status, StringComparison.Ordinal);

            foreach (var (slot, hex) in ThemeCatalog.Default.Palette.Select((c, i) => (i, c)))
            {
                var v = hex.ToInt();
                vm.SetSlotColor(slot, Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)));
            }
            vm.ApplyThemeCard(custom);              // 合法 → 应用

            Assert.True(vm.IsCustomActive);
            Assert.True(custom.IsSelected, "自选配色生效 = 只有自选颜色卡高亮");
            Assert.DoesNotContain(vm.ThemeCards.Where(c => !c.IsCustom), c => c.IsSelected);
            Assert.Equal("user-custom", vm.SelectedThemeId);

            var prefs = UiPreferenceStore.Load(out var failed);
            Assert.False(failed);
            Assert.True(prefs.Theme.IsCustom, "点自选颜色卡也必须落盘为自选配色");
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    // ── 互斥归属（主题 ↔ 自选配色 二选一）─────────────────────────────
    // 用户报障："自选配色和主题不是应该二选一吗？怎么居然不用二选一"——
    // 根因是面板里根本没有"归属"这个状态，自选区的高亮是按**色槽数量**推的。

    [Fact]
    public void 互斥归属_预设与自选配色恰有一侧是当前使用()
    {
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            Assert.False(vm.IsCustomActive, "出厂默认 = 预设主题");
            Assert.True(vm.ThemeCards[0].IsSelected);

            var matcha = vm.ThemeCards.First(c => c.Id == "uji-matcha");
            vm.ApplyThemeCard(matcha);
            Assert.False(vm.IsCustomActive);
            Assert.Single(vm.ThemeCards, c => c.IsSelected);
            Assert.False(vm.ThemeCards[^1].IsSelected, "预设生效时自选颜色卡不许亮");

            vm.StartFromCurrentTheme();   // 草稿先要有颜色（空槽会被应用门槛拒绝）
            vm.ApplyDraft();

            Assert.True(vm.IsCustomActive, "应用自选配色后归属必须切到自选");
            // 严格二选一：自选生效时**只有第 12 张「自选颜色」卡**高亮，11 张预设卡全灭
            Assert.True(vm.ThemeCards[^1].IsSelected);
            Assert.Single(vm.ThemeCards, c => c.IsSelected);
            Assert.DoesNotContain(vm.ThemeCards.Where(c => !c.IsCustom), c => c.IsSelected);
            Assert.False(vm.IsDraftEditing, "已应用 = 没有未应用改动");
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 草稿_当前用自选配色时_重进面板显示的是它本身()
    {
        // 真缺陷回归：草稿原先**永远**取出厂默认那 5 色 → 正在用自选配色时重进面板，
        // 色槽显示的是别人的颜色（面板与实际不符）。
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            vm.StartFromCurrentTheme();              // 起点 = 出厂默认那 5 色（合法）
            var target = Color.FromRgb(0x20, 0x60, 0x40);
            vm.SetSlotColor(1, target);
            vm.ApplyDraft();
            Assert.True(vm.IsCustomActive);

            var reopened = NewVm();
            Assert.True(reopened.IsCustomActive, "重进面板必须仍认得出当前是自选配色");
            Assert.Equal(vm.SlotCount, reopened.SlotCount);
            Assert.Equal(target, reopened.Slots[1].Color);
            Assert.True(reopened.ThemeCards[^1].IsSelected, "自选配色生效 = 自选颜色卡高亮");
            Assert.DoesNotContain(reopened.ThemeCards.Where(c => !c.IsCustom), c => c.IsSelected);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 草稿_入口对齐不覆盖未应用改动()
    {
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            // 默认草稿 = 4 个**空槽**（预设只读：不再把当前主题的颜色倒进来）
            Assert.Equal(AppearanceViewModel.MinSlots, vm.SlotCount);
            Assert.All(vm.Slots, s => Assert.True(s.IsEmpty));

            var edited = Color.FromRgb(0x11, 0x22, 0x33);
            vm.SetSlotColor(0, edited);
            Assert.True(vm.IsDraftEditing, "改过草稿但没应用 = 编辑中（未应用）");

            vm.SyncFromAppliedTheme();   // 模拟"切走再切回外观页"
            Assert.Equal(edited, vm.Slots[0].Color);
            Assert.Equal(AppearanceViewModel.MinSlots, vm.SlotCount);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    // ── 色槽（纯 VM 逻辑，不碰全局）─────────────────────────────────

    [Fact]
    public void 色槽_默认4个空槽_上下限为4与5()
    {
        var vm = NewVm();
        Assert.Equal(AppearanceViewModel.MinSlots, vm.SlotCount);
        Assert.InRange(vm.SlotCount, AppearanceViewModel.MinSlots, AppearanceViewModel.MaxSlots);
        Assert.All(vm.Slots, s => Assert.True(s.IsEmpty, "默认必须是空槽（不显示任何颜色）"));
        Assert.True(vm.CanAddSlot);
        Assert.False(vm.CanRemoveSlot);

        vm.AddSlot();
        Assert.Equal(5, vm.SlotCount);
        Assert.True(vm.Slots[^1].IsEmpty, "新增的槽也是空的（绝不编造一个没人选过的颜色）");
        Assert.False(vm.CanAddSlot);
        Assert.True(vm.CanRemoveSlot);

        vm.RemoveSlot();
        Assert.Equal(4, vm.SlotCount);
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
    public void 槽数分段_索引与草稿槽数双向一致_越界一律钳制()
    {
        // 「4 色 / 5 色」现在是共享滑动指示器分段控件，选中索引直接绑 SlotCountIndex
        // （唯一事实来源 = 草稿槽数；视图不再按槽数换按钮样式）。
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            Assert.Equal(0, vm.SlotCountIndex);       // 默认 4 槽 → 索引 0

            vm.SlotCountIndex = 1;
            Assert.Equal(AppearanceViewModel.MaxSlots, vm.SlotCount);
            Assert.Equal(1, vm.SlotCountIndex);
            Assert.True(vm.IsDraftEditing, "改槽数 = 草稿有未应用改动");

            vm.SlotCountIndex = 0;
            Assert.Equal(AppearanceViewModel.MinSlots, vm.SlotCount);
            Assert.Equal(0, vm.SlotCountIndex);

            vm.SlotCountIndex = 7;    // 越界 → 钳到 5
            Assert.Equal(AppearanceViewModel.MaxSlots, vm.SlotCount);

            vm.SlotCountIndex = -3;   // 越界 → 钳到 4
            Assert.Equal(AppearanceViewModel.MinSlots, vm.SlotCount);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
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
        // 用出厂默认的身份色（保证合法）→ 应无任何诊断。默认草稿只有 4 格，
        // 而出厂默认是 5 个身份色 → 先把槽数切到 5（与"这套配色整体合法"的前提一致）。
        var vm = NewVm();
        vm.SlotCountIndex = 1;
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
        Assert.Equal(12, vm.ThemeCards.Count);
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

    // ── 必须碰全局的用例（应用/落盘）─────────────────────────────────
    // ⚠️ 它们各自靠**类级 Dispose**（ResetForTests → 再写回偏好备份）复位到"跑前原样"；
    //    类内还有 try/finally ResetForTests 的用例是"中途就要回默认"的那几条，两套并存不冲突。

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
        vm.SlotCountIndex = 1;                 // 5 槽（出厂默认有 5 个身份色）
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
        // 调色台回**空态**：自选配色已随偏好一起清掉，留着 4/5 个色点会名不副实
        Assert.Equal(AppearanceViewModel.MinSlots, vm.SlotCount);
        Assert.All(vm.Slots, s => Assert.True(s.IsEmpty));
        Assert.True(vm.ThemeCards[^1].IsEmpty, "恢复默认后自选颜色卡必须回到空态");
        Assert.Empty(vm.ThemeCards[^1].Swatches);
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
