using System;
using System.IO;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.Theming;
using LinkPocket.Theming.Fonts;
using LinkPocket.Theming.Preferences;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 设置页「外观」区的**语言卡**：三枚分段（自动 / 中文 / English）、切了即刻生效且落盘、
/// 界面字体跟着语言换（用户显式选过的除外）。
/// </summary>
/// <remarks>
/// <para>
/// 语言与偏好文件都是进程级状态，故挂 <see cref="LocaleStateCollection"/> 并逐用例收尾复位。
/// 渲染与真实点击由渲染检查的 <c>language</c> 套件覆盖（<c>CheckLanguageCard</c>）；
/// 这里钉的是可观测行为（分段 → 偏好的映射、字体跟随的判据）。
/// </para>
/// <para>
/// ⚠️ <b>不要断言"点第一枚后界面语言是中文"</b>：「自动」的结论取自系统 UI 语言，
/// 那不是本用例能假定的事实（在英文系统上它会正确地变成英文）。
/// </para>
/// </remarks>
[Collection(LocaleStateCollection.Name)]
public sealed class LanguageCardTests : IDisposable
{
    private readonly string _path = UiPreferenceStore.FilePath;
    private readonly string? _backup;

    public LanguageCardTests()
    {
        _backup = File.Exists(_path) ? File.ReadAllText(_path) : null;
    }

    public void Dispose()
    {
        // 收尾顺序不能反：先复位进程内状态（它会清偏好文件），再把跑前的文件状态还回去。
        LocaleService.Apply(AppLocales.Default);
        ThemeService.ResetForTests();
        if (_backup is null) UiPreferenceStore.Clear();
        else File.WriteAllText(_path, _backup);
    }

    /// <summary>
    /// 语言切换失败时**如实播报**（不是静默）：语言已当场生效，但偏好没落盘 ⇒ 下次启动会退回旧语言。
    /// </summary>
    /// <remarks>
    /// 造法的依据：偏好是**文件**，而"写不进去"有一种与权限无关的可靠形态——
    /// 目标路径上先放一个**同名目录**（<c>File.Move</c> / <c>File.WriteAllText</c> 必抛）。
    /// 失败面覆盖整条"语言 → 偏好"的写路径，且不依赖文件系统权限设置。
    /// </remarks>
    [Fact]
    public void 切换失败_暴露在状态行而不是静默()
    {
        try
        {
            UiPreferenceStore.Clear();                       // 先清掉旧偏好（否则写路径会走 File.Replace）
            Directory.CreateDirectory(_path);                // 把偏好路径变成目录 ⇒ 之后写必失败

            var vm = new AppearanceViewModel();
            vm.SetLanguage(2);

            Assert.False(vm.Status.IsEmpty, "切换失败必须播报（状态行）");
            Assert.Contains("languageFailed", vm.Status.Key, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
            UiPreferenceStore.Clear();
            if (_backup is not null) File.WriteAllText(_path, _backup);
            LocaleService.Apply(AppLocales.Default);
            ThemeService.ResetForTests();
        }
    }

    /// <summary>三枚分段 → 偏好写法：自动 / 固定 zh-CN / 固定 en，且界面当场跟着换。</summary>
    /// <remarks>
    /// <b>读（<c>LanguageIndex</c>）与写（<c>SetLanguage</c>）是两个方向</b>：
    /// 读的是"用户选了什么"（模式 + 固定码），不是"当前生效的是哪门语言"——
    /// 跟随系统时生效的可能是英文，但分段必须停在第一枚。
    /// </remarks>
    [Fact]
    public void 三枚分段_切换并落盘()
    {
        var vm = new AppearanceViewModel();

        vm.SetLanguage(2);                                   // English
        Assert.Equal(AppLocale.En, LocaleService.Current);
        Assert.Equal(2, vm.LanguageIndex);
        var en = UiPreferenceStore.Load(out _).Language;
        Assert.Equal(LocalePreference.ModeFixed, en.Mode);
        Assert.Equal(LocalePreference.CodeEn, en.Override);

        vm.SetLanguage(1);                                   // 简体中文
        Assert.Equal(AppLocale.ZhCn, LocaleService.Current);
        Assert.Equal(1, vm.LanguageIndex);
        var zh = UiPreferenceStore.Load(out _).Language;
        Assert.Equal(LocalePreference.ModeFixed, zh.Mode);
        Assert.Equal(LocalePreference.CodeZh, zh.Override);

        vm.SetLanguage(0);                                   // 跟随系统
        Assert.Equal(0, vm.LanguageIndex);
        var auto = UiPreferenceStore.Load(out _).Language;
        Assert.Equal(LocalePreference.ModeAuto, auto.Mode);
        Assert.Null(auto.Override);
    }

    /// <summary>偏好里是"固定语言"时，分段必须停在对应那一枚（重启后照旧）。</summary>
    [Fact]
    public void 偏好里的固定语言_投影到对应分段()
    {
        foreach (var (mode, code, expected) in new[]
                 {
                     (LocalePreference.ModeFixed, LocalePreference.CodeEn, 2),
                     (LocalePreference.ModeFixed, LocalePreference.CodeZh, 1),
                     (LocalePreference.ModeAuto, null, 0),
                 })
        {
            ThemeService.SetLanguagePreference(mode, code);
            Assert.Equal(expected, new AppearanceViewModel().LanguageIndex);
        }
    }

    /// <summary>
    /// 切语言时，**没选过字体**才跟着换默认族；**用户显式选过的族**跨语言不变。
    /// </summary>
    [Fact]
    public void 字体跟随语言_但用户选择优先()
    {
        // 没选过：切到英文 → 换上英文默认族；再切回中文 → 换回中文默认族
        UiPreferenceStore.Save(new UiPreferences { Fonts = new FontPreference() });
        var vm = new AppearanceViewModel();
        vm.SetLanguage(2);
        Assert.Equal(FontCatalog.EnDefaultUiFamily, ThemeService.CurrentUiFont);
        vm.SetLanguage(1);
        Assert.Equal(FontCatalog.ZhDefaultUiFamily, ThemeService.CurrentUiFont);

        // 显式选过：切语言不动它，偏好里也原样保留
        ThemeService.ApplyFonts("Consolas", null, resources: null);
        ThemeService.SaveCurrentPreferences();
        vm.SetLanguage(2);
        Assert.Equal("Consolas", ThemeService.CurrentUiFont);
        Assert.Equal("Consolas", UiPreferenceStore.ExplicitUiFont());
    }

    /// <summary>
    /// <b>语言默认族在偏好里一定落成"跟随语言"（空）</b>——包括"上一种语言的默认族"这种形态。
    /// </summary>
    /// <remarks>
    /// 这条钉的是一个真实缺陷：判据只认"当前语言的默认族"时，切换语言之后生效的还是上一种语言的默认族，
    /// 它不等于当前语言的默认族 ⇒ 被当成显式选择写进偏好 ⇒ 默认族从此不再跟着语言走
    /// （症状：语言卡切到英文，偏好里被写上中文默认族，字体停在雅黑）。
    /// </remarks>
    [Fact]
    public void 语言默认族_落盘成跟随语言_两种语言的默认族都算()
    {
        foreach (var family in new[] { FontCatalog.ZhDefaultUiFamily, FontCatalog.EnDefaultUiFamily })
        {
            ThemeService.ApplyFonts(family, null, resources: null);
            ThemeService.SaveCurrentPreferences();
            Assert.Null(UiPreferenceStore.Load(out _).Fonts.Ui);
        }

        // 非默认族才写成具体族名（用户显式选择）
        ThemeService.ApplyFonts("Consolas", null, resources: null);
        ThemeService.SaveCurrentPreferences();
        Assert.Equal("Consolas", UiPreferenceStore.Load(out _).Fonts.Ui);
    }

    /// <summary>语言选项名按**当前界面语言**取词：中文界面写「中文」，英文界面写 "Chinese"。</summary>
    [Fact]
    public void 语言选项名_按当前界面语言取词()
    {
        foreach (var locale in new[] { AppLocale.ZhCn, AppLocale.En })
        {
            var table = StringTables.For(locale);
            Assert.True(table.ContainsKey(AppLocale.ZhCn.KeyFor()));
            Assert.True(table.ContainsKey(AppLocale.En.KeyFor()));
            Assert.NotEqual("", table[AppLocale.ZhCn.KeyFor()].Trim());
            Assert.NotEqual("", table[AppLocale.En.KeyFor()].Trim());
        }

        Assert.Equal("中文", StringTables.For(AppLocale.ZhCn)[AppLocale.ZhCn.KeyFor()]);
        Assert.Equal("Chinese", StringTables.For(AppLocale.En)[AppLocale.ZhCn.KeyFor()]);
        Assert.Equal("English", StringTables.For(AppLocale.ZhCn)[AppLocale.En.KeyFor()]);
    }

    /// <summary>语言卡的说明句两段都在表里，且随界面语言变（不是成品文本）。</summary>
    [Fact]
    public void 说明句走键_随界面语言变()
    {
        var vm = new AppearanceViewModel();
        LocaleService.Apply(AppLocale.ZhCn);
        var zh = vm.LanguageHint.Resolve();
        LocaleService.Apply(AppLocale.En);
        var en = vm.LanguageHint.Resolve();

        Assert.NotEqual("", zh);
        Assert.NotEqual("", en);
        Assert.NotEqual(zh, en);
        Assert.NotEqual("", vm.LanguagePathNote.Resolve());
    }
}
