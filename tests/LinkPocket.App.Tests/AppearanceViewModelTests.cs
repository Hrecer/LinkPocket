using System.IO;
using System.Linq;
using System.Windows.Media;
using LinkPocket.Theming;
using LinkPocket.Theming.Color;
using LinkPocket.Theming.Fonts;
using LinkPocket.Theming.Preferences;
using LinkPocket.Theming.Themes;
using LinkPocket.Theming.Tokens;
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
    private static FontOptionViewModel FontOption(string family, string? filePath = null)
        => new(new FontChoice(family, family, filePath));

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
        // 色点 = 该主题**设计档给的全部颜色**（用户设计档「配色方案.txt」：第 1–4 套 5 色、第 5–10 套 4 色、
        // 出厂默认 5 色）——不是"补位凑到 4 个"。逐张对账，少一个都算红。
        var vm = NewVm();
        var presets = vm.ThemeCards.Where(c => !c.IsCustom).ToList();
        Assert.Equal(11, presets.Count);
        Assert.All(presets, c =>
        {
            Assert.Equal(c.Definition!.Palette.Count, c.Swatches.Count);
            Assert.InRange(c.Swatches.Count, 4, 5);
        });
        Assert.All(presets, c => Assert.False(string.IsNullOrWhiteSpace(c.Summary), $"{c.Name} 缺派生摘要"));
        Assert.All(presets, c => Assert.False(c.IsEmpty, $"{c.Name} 不该是空态"));

        // 设计档第 1–4 套 = 5 色、第 5–10 套 = 4 色；加上 5 色的出厂默认
        // → 12 张卡里 5 色 5 张、4 色 6 张（"五色主题"就是这个，不能被压成四色）
        Assert.Equal(5, presets.Count(c => c.Swatches.Count == 5));
        Assert.Equal(6, presets.Count(c => c.Swatches.Count == 4));
        Assert.Equal(5, vm.ThemeCards[0].Swatches.Count);
        Assert.Equal(ThemeCatalog.Presets.Count(p => p.Palette.Count == 5), presets.Count(c => c.Swatches.Count == 5) - 1);

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
            // ⚠️ 槽数**跟着当前主题走**：宇治抹茶在设计档里是 4 色 → 4 格
            //    （"槽数"是投影，"颜色"才是用户的草稿，两者互不牵连）。
            Assert.Equal(PaletteSolver.EditableSlots(ThemeCatalog.Find("uji-matcha")!).Count, vm.SlotCount);
            Assert.Equal(4, vm.SlotCount);
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
    public void 槽数_跟着当前外观的颜色数走_五色主题就是五格()
    {
        // 用户报障 2026-09-20："我们很多默认主题不是五色的吗？为什么到了这里变成四色"——
        // 草稿槽数原先恒为 4（MinSlots），于是 5 色的出厂默认在调色台上被**显示成 4 色**，
        // 与主题卡上的 5 个色点自相矛盾。判据 = 草稿槽数 == PaletteSolver.EditableSlots(当前主题).Count。
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            Assert.Equal(5, PaletteSolver.EditableSlots(ThemeCatalog.Default).Count);
            Assert.Equal(5, vm.SlotCount);
            Assert.Equal(1, vm.SlotCountIndex);          // 分段选中「5 色」
            Assert.True(vm.ThemeCards[0].IsSelected);
            Assert.Equal(5, vm.ThemeCards[0].Swatches.Count);   // 卡片与色槽同数（用户看到的是同一件事）

            // 切到 4 格的预设 → 槽数跟着变 4（颜色仍不自动载入）
            vm.ApplyThemeCard(vm.ThemeCards.First(c => c.Id == "uji-matcha"));
            Assert.Equal(4, vm.SlotCount);
            Assert.Equal(0, vm.SlotCountIndex);

            // 切回出厂默认 → 回 5
            vm.ApplyThemeCard(vm.ThemeCards[0]);
            Assert.Equal(5, vm.SlotCount);
            Assert.All(vm.Slots, s => Assert.True(s.IsEmpty, "槽数对齐不许顺手把颜色倒进来"));
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 起点_复制当前主题的颜色_并立即应用为自选配色()
    {
        // 用户报障 2026-09-20："当我们选择以什么为起点的时候，应该立即切换到自选颜色这一栏，
        // 也就是主题应该立即更改"——旧行为只倒颜色、归属仍留在预设上（按了按钮界面一点没变）。
        // 新语义 = 复制 + **立刻应用**：归属切自选、第 12 张卡亮起、界面换成这份配色。
        ThemeService.ResetForTests();
        try
        {
            ThemeService.ApplyById("uji-matcha", null);
            var matcha = ThemeCatalog.Find("uji-matcha")!;
            var before = matcha.Palette.ToList();

            var vm = NewVm();
            Assert.Equal("以「宇治抹茶」为起点", vm.StartFromCurrentThemeLabel);
            Assert.Contains("宇治抹茶", vm.DraftIntro, StringComparison.Ordinal);   // 整句说明也同源

            vm.StartFromCurrentTheme();

            // ① 颜色逐格 = 该主题的可编辑槽（值拷贝）
            var expected = PaletteSolver.EditableSlots(matcha);
            Assert.Equal(expected.Count, vm.SlotCount);
            for (var i = 0; i < expected.Count; i++)
                Assert.Equal($"#{expected[i].ToInt() & 0x00FFFFFF:X6}", vm.Slots[i].Hex);

            // ② 立即生效：归属切自选 + 只有自选颜色卡高亮 + 落盘为自选（不是"编辑中"）
            Assert.True(vm.IsCustomActive, "点起点必须立即生效（归属切到自选配色）");
            Assert.Equal(ThemeCardViewModel.CustomCardId, vm.SelectedThemeId);
            Assert.True(vm.ThemeCards[^1].IsSelected, "自选生效 = 自选颜色卡高亮");
            Assert.DoesNotContain(vm.ThemeCards.Where(c => !c.IsCustom), c => c.IsSelected);
            Assert.False(vm.IsDraftEditing, "已应用 = 不再显示「编辑中（未应用）」");
            Assert.Equal("user-custom", ThemeService.Current.Id);

            var prefs = UiPreferenceStore.Load(out var failed);
            Assert.False(failed);
            Assert.True(prefs.Theme.IsCustom, "起点立即应用 → 必须当场落盘为自选配色");

            // ③ 硬判据：预设定义逐项未变（复制而非引用）
            Assert.Equal(before, matcha.Palette);
            Assert.Equal(before, ThemeCatalog.Find("uji-matcha")!.Palette);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 起点_文字标签与整句说明随当前外观更新()
    {
        // 用户报障 2026-09-20："为什么这里一直显示以默认紫罗兰为起点，一直都没有更改过"——
        // 根因是这句话在 XAML 里被拆成「Run 字面量 + 绑定 Run + 字面量」，VM 侧没有任何一处能被守住。
        // 现行：整句 + 按钮文案都由 VM 出（同一个 AppliedThemeName），本用例把它们钉在"随主题更新"上。
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            Assert.Equal("以「默认（紫罗兰）」为起点", vm.StartFromCurrentThemeLabel);
            Assert.Contains("默认（紫罗兰）", vm.DraftIntro, StringComparison.Ordinal);

            var changes = new List<string>();
            vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? string.Empty);

            vm.ApplyThemeCard(vm.ThemeCards.First(c => c.Id == "green-pear"));

            Assert.Equal("以「青梨冻冻」为起点", vm.StartFromCurrentThemeLabel);
            Assert.Contains("青梨冻冻", vm.DraftIntro, StringComparison.Ordinal);
            Assert.DoesNotContain("默认（紫罗兰）", vm.DraftIntro, StringComparison.Ordinal);
            Assert.Contains(nameof(AppearanceViewModel.StartFromCurrentThemeLabel), changes);
            Assert.Contains(nameof(AppearanceViewModel.DraftIntro), changes);
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
            var total = vm.SlotCount;              // 出厂默认 5 色 → 5 格（槽数随当前外观）

            vm.ApplyDraft();

            Assert.False(vm.IsCustomActive, "空槽必须被拒绝：不应用");
            Assert.False(vm.ThemeCards[^1].IsSelected, "被拒绝时自选颜色卡不许高亮");
            Assert.Contains($"{total} 个颜色没选", vm.Status, StringComparison.Ordinal);
            Assert.False(File.Exists(_path), "被拒绝不得写偏好");

            // 只差一格也不行（门槛 = 每一格都有颜色）
            for (var i = 0; i < total - 1; i++) vm.SetSlotColor(i, Color.FromRgb((byte)(0x40 + i), 0x50, 0x60));
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
    public void 清空颜色_草稿回空态_回到紫罗兰_自选卡回空心占位_且诊断提示还差几个()
    {
        // 用户令 2026-09-20 第三轮：调色台加「清空颜色」（清空全部只有这一条路径；单槽「清除」在取色盘里）。
        // 清完必须**重算诊断**——空槽态要显示"还差 N 个颜色"这条中性提示，
        // 否则面板上没有任何一处告诉用户"还差几个"（清诊断 = 静默）。
        // 用户令 2026-09-21："清空颜色的时候应该回到紫罗兰" —— 清空**一并回出厂默认主题**
        // （只清草稿不动已应用外观 = 界面一点没变，"清空"名不副实），并落盘。
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            vm.SetSlotCount(4);
            for (var i = 0; i < vm.SlotCount; i++)
                vm.SetSlotColor(i, Color.FromRgb((byte)(0x40 + i), 0x50, 0x60));
            vm.ApplyDraft();                                   // 先让它真的生效（自选配色）
            Assert.True(vm.IsCustomActive, "前置：自选配色已生效");
            Assert.False(vm.ThemeCards[^1].IsEmpty);

            vm.ApplyThemeCard(vm.ThemeCards.First(c => c.Id == "uji-matcha"));   // 再切到一套预设
            Assert.False(vm.IsCustomActive);

            vm.ClearDraft();

            Assert.All(vm.Slots, s => Assert.True(s.IsEmpty, "清空后每一格都必须是空槽"));
            Assert.Equal(5, vm.SlotCount);                     // 出厂默认是 5 色主题 → 槽数跟着它
            Assert.False(vm.IsCustomActive, "清空必须把归属切回预设");
            Assert.Equal(ThemeCatalog.DefaultId, vm.SelectedThemeId);
            Assert.Equal(ThemeCatalog.Default.Palette.Count, vm.Slots.Count);
            Assert.Equal(ThemeCatalog.Default.Id, ThemeService.Current.Id);      // 界面真的回到紫罗兰
            Assert.Equal(0xE6E3FAu, (uint)(ThemeService.DerivedTable.Token(AppTokens.SurfaceBase).ToInt() & 0x00FFFFFF));
            Assert.True(vm.ThemeCards.First(c => c.Id == ThemeCatalog.DefaultId).IsSelected, "主题卡高亮回到出厂默认");
            Assert.Empty(vm.ThemeCards[^1].Swatches);
            Assert.True(vm.ThemeCards[^1].IsEmpty, "清空后自选颜色卡必须回空心占位（色点 = 草稿投影）");
            Assert.True(vm.HasDiagnostics);
            Assert.Contains("还差 5 个颜色", vm.Diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("✗", vm.Diagnostics, StringComparison.Ordinal);

            // 回默认这件事本身要落盘（与「恢复默认外观」的分工：那个连偏好文件一起清）
            var prefs = UiPreferenceStore.Load(out _);
            Assert.False(prefs.Theme.IsCustom);
            Assert.Equal(ThemeCatalog.DefaultId, prefs.Theme.Id);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 自选颜色卡_点它等于应用自选配色()
    {
        // 第 12 张卡的应用路径与 VM 的 `ApplyDraft` **同一入口**（合法才生效、才落盘）——
        // 面板顶部的「应用这套外观」按钮已随 2026-09-20 第三轮删除，这张卡就是自选配色唯一的应用手势。
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

            vm.StartFromCurrentTheme();   // 复制 + 立即应用（草稿里已有颜色，不会被应用门槛拒绝）

            Assert.True(vm.IsCustomActive, "点起点即切到自选配色（旧行为：停在预设上，按了没反应）");
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
            // 默认草稿 = 与当前外观同数的**空槽**（预设只读：不再把当前主题的颜色倒进来）
            Assert.Equal(PaletteSolver.EditableSlots(ThemeCatalog.Default).Count, vm.SlotCount);
            Assert.All(vm.Slots, s => Assert.True(s.IsEmpty));

            var edited = Color.FromRgb(0x11, 0x22, 0x33);
            vm.SetSlotColor(0, edited);
            Assert.True(vm.IsDraftEditing, "改过草稿但没应用 = 编辑中（未应用）");

            vm.SyncFromAppliedTheme();   // 模拟"切走再切回外观页"
            Assert.Equal(edited, vm.Slots[0].Color);
            Assert.Equal(PaletteSolver.EditableSlots(ThemeCatalog.Default).Count, vm.SlotCount);

            // 未应用改动期间切主题：**颜色**仍不被冲掉，但**槽数**照旧跟着新主题走（两件事互不牵连）
            vm.ApplyThemeCard(vm.ThemeCards.First(c => c.Id == "uji-matcha"));
            Assert.Equal(edited, vm.Slots[0].Color);
            Assert.Equal(PaletteSolver.EditableSlots(ThemeCatalog.Find("uji-matcha")!).Count, vm.SlotCount);
            Assert.True(vm.IsDraftEditing, "换主题不改「用户改过没应用」这个事实");
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    // ── 色槽（纯 VM 逻辑，不碰全局）─────────────────────────────────

    [Fact]
    public void 色槽_默认格数随当前外观_上下限为4与5()
    {
        // 出厂默认是 5 色 → 5 格；4/5 是**上下限**，不是"默认恒为 4"（用户报障"五色主题显示成四色"）。
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            Assert.Equal(PaletteSolver.EditableSlots(ThemeCatalog.Default).Count, vm.SlotCount);
            Assert.InRange(vm.SlotCount, AppearanceViewModel.MinSlots, AppearanceViewModel.MaxSlots);
            Assert.All(vm.Slots, s => Assert.True(s.IsEmpty, "默认必须是空槽（不显示任何颜色）"));

            // 减到下限就减不动了
            while (vm.CanRemoveSlot) vm.RemoveSlot();
            Assert.Equal(AppearanceViewModel.MinSlots, vm.SlotCount);
            Assert.False(vm.CanRemoveSlot);

            // 加到上限就加不动了
            while (vm.CanAddSlot) vm.AddSlot();
            Assert.Equal(AppearanceViewModel.MaxSlots, vm.SlotCount);
            Assert.False(vm.CanAddSlot);
            Assert.True(vm.Slots[^1].IsEmpty, "新增的槽也是空的（绝不编造一个没人选过的颜色）");
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 色槽_下限4_减不到3()
    {
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            while (vm.CanRemoveSlot) vm.RemoveSlot();
            vm.RemoveSlot();
            Assert.Equal(AppearanceViewModel.MinSlots, vm.SlotCount);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
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
            var initial = vm.SlotCount;               // 出厂默认 5 → 索引 1
            Assert.Equal(initial - AppearanceViewModel.MinSlots, vm.SlotCountIndex);

            vm.SlotCountIndex = 0;
            Assert.Equal(AppearanceViewModel.MinSlots, vm.SlotCount);
            Assert.Equal(0, vm.SlotCountIndex);

            vm.SlotCountIndex = 1;
            Assert.Equal(AppearanceViewModel.MaxSlots, vm.SlotCount);
            Assert.Equal(1, vm.SlotCountIndex);
            Assert.True(vm.IsDraftEditing, "改槽数 = 草稿有未应用改动");

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
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            // ⚠️ 必须填满**每一格**（格数随当前外观 = 出厂默认 5 格）：留空格时诊断走的是
            //    "还差 N 个颜色"那条中性提示，压根到不了派生诊断（本用例曾因此红）。
            for (var i = 0; i < vm.SlotCount; i++)
                vm.SetSlotColor(i, Color.FromRgb((byte)(0xE8 + i), (byte)(0xE8 + i), (byte)(0xE8 + i)));
            vm.RefreshDraftDiagnostics();

            Assert.True(vm.HasDiagnostics);
            Assert.Contains("没有深色", vm.Diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("✗", vm.Diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 诊断_合法配色无诊断()
    {
        // 用出厂默认的身份色（保证合法）→ 应无任何诊断。草稿格数 = 出厂默认的颜色数（5），
        // 逐格填满即"这套配色整体合法"。
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            var palette = ThemeCatalog.Default.Palette;
            Assert.Equal(palette.Count, vm.SlotCount);
            for (var i = 0; i < vm.SlotCount && i < palette.Count; i++)
            {
                var v = palette[i].ToInt();
                vm.SetSlotColor(i, Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)));
            }
            vm.RefreshDraftDiagnostics();
            Assert.False(vm.HasDiagnostics, vm.Diagnostics);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    // ── 字体逻辑 ────────────────────────────────────────────────────

    [Fact]
    public void 未装载字体候选时_面板状态可用且不崩()
    {
        // 候选是**惰性**的（用户没展开下拉就不该付全量枚举的钱），但下拉框里**必须看得见当前字体**：
        // 池为空时用当前族名补一个占位项（用户报障 2026-09-20 第二轮："我选系统字体，
        // 此时根本就拉取不到任何字体" —— 那个框是空白的，读成"拉不到"，其实只是"还没去拉"）。
        // ⚠️ 占位项**只补属于当前来源的那一个**（用户令 2026-09-21："使用自定义字体的时候，
        //    不应该显示系统字体，而是什么都没有"）：自定义来源没导入过就是**空列表**。
        var vm = NewVm();
        Assert.Single(vm.UiFonts);                                // 只补"当前界面字体"这一个占位项
        Assert.All(vm.UiFonts, f => Assert.False(f.CanDelete));    // 占位项 = 系统字体口径（不可删）
        Assert.Contains(vm.UiFonts, f => f.Family == LinkPocket.Theming.Fonts.FontCatalog.DefaultUiFamily);
        Assert.NotNull(vm.SelectedUiFont);                        // 投影已就位（不是 null）

        vm.FontSource = FontSourceKind.Custom;
        Assert.Empty(vm.UiFonts);                                 // 自定义侧：系统字体一个都不许出现
        Assert.Equal(12, vm.ThemeCards.Count);
    }

    [Fact]
    public async Task 字体候选_装载候选_且投影当前字体_自定义侧只有导入字体()
    {
        // ⚠️ 这条用例**走真实系统字体枚举**（生产字体来源），断言"枚举 + 候选 + 投影"整条链路真的成立。
        //    它曾经因为"据说会吊住测试宿主"被换成两个弱用例（只断言"能往集合里塞假项"），
        //    等于把"枚举路径有没有坏"的覆盖让给了探针 —— 而探针只在 UI 改动时才跑。
        //    现在枚举在后台线程（FontCatalog.LoadAsync）+ 实例内缓存，这条覆盖必须留着。
        ThemeService.ResetForTests();
        var vm = NewVm();

        await vm.EnsureFontsLoadedAsync();

        Assert.NotEmpty(vm.UiFonts);

        // 投影：当前已应用字体必须在候选里被选中（否则下拉看起来"没生效"）
        Assert.NotNull(vm.SelectedUiFont);
        Assert.Equal(ThemeService.CurrentUiFont, vm.SelectedUiFont!.Family, ignoreCase: true);

        // 默认字体一定在候选里（回退链承诺它存在）
        Assert.Contains(vm.UiFonts, f => f.Family == FontCatalog.DefaultUiFamily);

        // 自定义来源 = **只列导入的字体**（用户令 2026-09-21："不应该显示系统字体，而是什么都没有"）：
        // 没导入过就是空；有导入项时也一个系统字体都不许出现。
        vm.FontSource = FontSourceKind.Custom;
        Assert.All(vm.UiFonts, f => Assert.True(f.IsImported, $"「{f.Family}」不是导入字体，却出现在自定义来源里"));

        vm.FontSource = FontSourceKind.System;                    // 切回来 = 系统候选完整
        Assert.NotEmpty(vm.UiFonts);
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
    public void 自动调整颜色开关_缺省打开_切换即生效并落盘()
    {
        // 用户令 2026-09-20 **第二轮**（口径取代第一轮的"默认关闭"）：
        // "我们默认是打开自动调整颜色的，自动调整颜色是一个那种滑动开关……当我们开关自动调整颜色的按钮时，
        //  主题那个色点也会同步修改，这样就没有问题了"。
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            Assert.True(vm.AutoAdjustColors);                        // 缺省打开 = 自动调色
            Assert.Equal(PaletteMode.Auto, ThemeService.PaletteMode);
            Assert.Contains("已开启", vm.PaletteModeHint, StringComparison.Ordinal);

            // 关掉：当场重新应用 + 落盘（偏好里记着这个开关）
            vm.AutoAdjustColors = false;
            Assert.Equal(PaletteMode.Exact, ThemeService.PaletteMode);
            Assert.Equal(PaletteMode.Exact, ThemeService.Current.PaletteMode);
            Assert.Contains("已关闭", vm.PaletteModeHint, StringComparison.Ordinal);
            var prefsOff = UiPreferenceStore.Load(out var failedOff);
            Assert.False(failedOff);
            Assert.False(prefsOff.Theme.AutoAdjustColors, "开关必须落盘（重启后保持）");

            // 再打开
            vm.AutoAdjustColors = true;
            Assert.Equal(PaletteMode.Auto, ThemeService.PaletteMode);
            var prefsOn = UiPreferenceStore.Load(out _);
            Assert.True(prefsOn.Theme.AutoAdjustColors);

            // 两种模式下，每张主题卡的"背景色成员"格都显示**该模式实际生效的页面底**（融合）。
            // ⚠️ 判据 = "色点里**含**页面底颜色"，不能拿"最亮的那一枚"当代理：
            //    表面族色点由**配色里最浅的成员**担任，而某些配色里更亮的成员（如晴王青提饮的米白 `#FDF5DA`）
            //    属于别的角色 → 拿最亮的那枚去比会误报（实测）。
            foreach (var on in new[] { true, false })
            {
                vm.AutoAdjustColors = on;
                foreach (var card in vm.ThemeCards.Where(c => !c.IsCustom))
                {
                    var baseColor = ColorMath.ToMedia(
                        PaletteSolver.Solve(card.Definition! with
                        {
                            PaletteMode = on ? PaletteMode.Auto : PaletteMode.Exact,
                        }).Token(AppTokens.SurfaceBase));
                    Assert.True(card.Swatches.Any(c => c == baseColor),
                        $"[auto={on}] {card.Name} 的色点里没有该模式实际生效的页面底"
                        + $" #{baseColor.R:X2}{baseColor.G:X2}{baseColor.B:X2}（该融合）"
                        + $"：色点 {string.Join("/", card.Swatches.Select(c => $"{c.R:X2}{c.G:X2}{c.B:X2}"))}");
                }
            }
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 主题卡色点_换主题与重进面板都会按当前模式重投影()
    {
        // 用户令 2026-09-20："当我们开关自动调整颜色的按钮时，主题那个色点也会同步修改"——
        // 色点重投影**不能只在切开关那一条路上**：换主题（`ApplyThemeCard`）/ 重进面板（`SyncFromAppliedTheme`）
        // 也必须按当前模式重算（否则色点会停在旧模式的口径上，与页面底不同色）。
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            var card = vm.ThemeCards.First(c => !c.IsCustom);

            // 人为把色点写坏 → 入口对齐必须把它重投影回"当前模式下实际生效的页面底"
            //    （判据仍是"色点里含页面底颜色"：表面族色点由配色里最浅的成员担任，不是"最亮的那一枚"）
            card.SetSwatches(new[] { System.Windows.Media.Colors.Red });
            vm.SyncFromAppliedTheme();
            var baseColor = ColorMath.ToMedia(
                PaletteSolver.Solve(card.Definition! with { PaletteMode = ThemeService.PaletteMode })
                    .Token(AppTokens.SurfaceBase));
            Assert.Contains(card.Swatches, c => c == baseColor);

            // 换主题这条入口同样要重投影（另一张卡）
            var other = vm.ThemeCards.First(c => !c.IsCustom && c.Id != card.Id);
            other.SetSwatches(new[] { System.Windows.Media.Colors.Red });
            vm.ApplyThemeCard(other);
            var otherBase = ColorMath.ToMedia(
                PaletteSolver.Solve(other.Definition! with { PaletteMode = ThemeService.PaletteMode })
                    .Token(AppTokens.SurfaceBase));
            Assert.Contains(other.Swatches, c => c == otherBase);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 字体来源_二选一且候选按来源分开()
    {
        // 用户令 2026-09-20："将系统本身的字体和我们导入的字体区分开来，我们先要选择系统本身的字体，
        // 然后还是自定义字体，然后只能二选一，然后对于自定义的字体，我们可以进行导入和删除"。
        ThemeService.ResetForTests();
        try
        {
            var vm = NewVm();
            Assert.Equal(FontSourceKind.System, vm.FontSource);      // 默认在"系统字体"这一侧
            Assert.False(vm.IsCustomFontSource);

            vm.FontSource = FontSourceKind.Custom;
            Assert.True(vm.IsCustomFontSource);
            Assert.Equal(1, vm.FontSourceIndex);
            Assert.Contains("自定义", vm.FontSourceHint, StringComparison.Ordinal);

            vm.FontSource = FontSourceKind.System;
            Assert.Equal(0, vm.FontSourceIndex);
            Assert.Contains("只读", vm.FontSourceHint, StringComparison.Ordinal);

            // 能力位：导入那份可删、系统那份不可删（界面据此只在一侧显示导入/删除按钮）
            Assert.True(FontOption("X", "C:\\tmp\\x.ttf").CanDelete);
            Assert.False(FontOption("Microsoft YaHei UI").CanDelete);
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }

    [Fact]
    public void 删除导入字体_文件已不在也照样作废缓存()
    {
        // 旧实现：文件不存在时 `Remove` 直接 no-op（连缓存都不失效）→ 界面照样说"已删除"、下次进面板它还在。
        var tmp = System.IO.Path.Combine(LinkPocket.Engine.TempArea.Resolve(), "lp-font-del-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tmp);
        try
        {
            var choice = new FontChoice("Gone Font", "Gone Font", System.IO.Path.Combine(tmp, "gone.ttf"));
            Assert.True(new FontOptionViewModel(choice).CanDelete);
            Assert.False(FontCatalog.Remove(choice));   // 不抛、返回 false（缓存已作废 → 界面会刷新）
        }
        finally
        {
            try { System.IO.Directory.Delete(tmp, recursive: true); } catch { /* 用例自清尽力而为 */ }
        }
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
        // 状态行不播报"已应用主题「X」"（用户令 2026-09-20 第二轮："删掉这个提示"）；
        // 当前生效的是哪套由选中投影（上面三条）表达。
        Assert.Equal(string.Empty, vm.Status);
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
        // 状态行**不播报成功**（用户令 2026-09-20 第二轮："下面根本就不需要这个提示，删掉"）：
        // 当前生效的是哪套外观由主题卡高亮 + 「当前使用」徽标表达，不再写一行文字。
        Assert.Equal(string.Empty, vm.Status);

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

        // 状态行不播报成功（用户令：删掉提示）；"回到默认"由选中卡 + 调色台空态表达
        Assert.Equal(string.Empty, vm.Status);
        Assert.Equal(ThemeCatalog.DefaultId, vm.SelectedThemeId);
        Assert.Equal(ThemeCatalog.DefaultId, ThemeService.Current.Id);
        Assert.Equal(FontCatalog.DefaultUiFamily, ThemeService.CurrentUiFont);
        // 调色台回**空态**：自选配色已随偏好一起清掉，留着 4/5 个色点会名不副实；
        // 格数 = 恢复后的出厂默认颜色数（5）
        Assert.Equal(PaletteSolver.EditableSlots(ThemeCatalog.Default).Count, vm.SlotCount);
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
        vm.ApplyFonts();

        Assert.Equal(FontCatalog.DefaultUiFamily, ThemeService.CurrentUiFont);
        var prefs = UiPreferenceStore.Load(out _);
        Assert.Equal(FontCatalog.DefaultUiFamily, prefs.Fonts.Ui);
        // 等宽字体**不再可改**（用户令 2026-09-21："等宽字体不应该被更改"）：面板动作不碰它
        Assert.Equal(FontCatalog.DefaultMonoFamily, ThemeService.CurrentMonoFont);
    }

    [Fact]
    public async Task 恢复默认字体_回默认族并落盘_且下拉显示默认族()
    {
        // 用户令 2026-09-20 第三轮：字体卡加「恢复默认字体」——**不读下拉选中项**，直接把界面字体回默认并落盘，
        // 且下拉里立刻显示默认族（候选没装载时走占位项投影这条路径；**不触发系统字体枚举**）。
        // 等宽字体已固定为默认族（用户令 2026-09-21 删掉了那一行），复位顺手把它也归默认。
        ThemeService.ResetForTests();
        try
        {
            ThemeService.ApplyFonts("Segoe UI", "Consolas", null);   // 先换成一个非默认界面字体
            var vm = NewVm();
            Assert.Equal("Segoe UI", vm.SelectedUiFont?.Family);

            await vm.ResetFontsAsync();

            Assert.Equal(FontCatalog.DefaultUiFamily, ThemeService.CurrentUiFont);
            Assert.Equal(FontCatalog.DefaultMonoFamily, ThemeService.CurrentMonoFont);
            var prefs = UiPreferenceStore.Load(out var failed);
            Assert.False(failed);
            Assert.Equal(FontCatalog.DefaultUiFamily, prefs.Fonts.Ui);
            Assert.Equal(FontCatalog.DefaultMonoFamily, prefs.Fonts.Mono);
            Assert.Equal(FontCatalog.DefaultUiFamily, vm.SelectedUiFont?.Family);    // 下拉显示的 = 默认族
            Assert.Equal(string.Empty, vm.Status);                                  // 成功不播报（用户令）
        }
        finally
        {
            ThemeService.ResetForTests();
        }
    }
}
