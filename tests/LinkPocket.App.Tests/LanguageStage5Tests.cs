using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.Services;
using LinkPocket.Theming;
using LinkPocket.Theming.Fonts;
using LinkPocket.Theming.Preferences;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 阶段 5 的界面侧口径：**排序不变式**（切语言不改变任何顺序）、**默认字体族随语言**、
/// **字体偏好的"没选过"表达**。
/// </summary>
/// <remarks>
/// <para>
/// 与探针的分工：探针 P7 在真实窗口里逐行比对"切语言前后顺序"，P12 比对默认族；
/// 这里断言的是可观测的**机制**（排序口径、偏好读写、字号自检基准）——两者互补，
/// 单测能给出"为什么红"的读数，探针能给出"界面上真的这样吗"的证据。
/// </para>
/// <para>
/// <c>LocTable</c> 与 <c>UiPreferenceStore</c> 都是进程级状态，所以本类挂
/// <see cref="LocaleStateCollection"/> 与 <see cref="UiPreferencesCollection"/>。
/// </para>
/// </remarks>
[Collection(LocaleStateCollection.Name)]
public sealed class LanguageStage5Tests : IDisposable
{
    public void Dispose()
    {
        LocaleService.Apply(AppLocales.Default);
        ThemeService.ResetForTests();
    }

    // ── 排序：切语言前后顺序一字不变 ─────────────────────────────────────

    /// <summary>
    /// 回收站主栏按**类型**排序时走身份（文件夹在前），不走投影出来的类型文案。
    /// </summary>
    /// <remarks>
    /// 旧实现在比较函数里现取 <c>TypeText.Resolve()</c>——那是<b>当前语言</b>的文案
    /// （中文「文件夹」/ 英文 "Folder"），切成英文后排序键整体换了值，顺序就变了。
    /// 这条用例把"切语言前后逐行一致"钉在回收站主栏上（真实数据 + 真实 VM）。
    /// </remarks>
    [Fact]
    public async Task 回收站主栏_按类型排序_切语言前后逐行一致()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("Alpha")).Data!;
            await client.LinkCreateAsync("https://a.example/", "Zulu", autoFetchMetadata: false, listId: folder.FolderId);
            await client.LinkCreateAsync("https://b.example/", "Bravo", autoFetchMetadata: false);
            await client.FolderDeleteAsync(folder.FolderId);

            var vm = new TrashViewModel(client, new UiPortProvider { Dialogs = new SilentDialogs() });
            await vm.LoadAsync();

            vm.ApplySort("type", ascending: true);
            var zh = vm.Rows.Select(r => r.Id).ToList();

            LocaleService.Apply(AppLocale.En);
            vm.ApplySort("type", ascending: true);
            var en = vm.Rows.Select(r => r.Id).ToList();

            Assert.Equal(zh, en);
            // 前提：两种语言下都真的取到行（否则本用例在空跑）
            Assert.NotEmpty(zh);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>回收站主栏按名称 / 原位置排序，切语言前后也逐行一致。</summary>
    [Fact]
    public async Task 回收站主栏_按名称与原位置排序_切语言前后逐行一致()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var work = (await client.FolderCreateAsync("工作")).Data!;
            var play = (await client.FolderCreateAsync("娱乐")).Data!;
            await client.LinkCreateAsync("https://a.example/", "b 链接", autoFetchMetadata: false, listId: work.FolderId);
            await client.LinkCreateAsync("https://b.example/", "A 链接", autoFetchMetadata: false, listId: play.FolderId);
            await client.FolderDeleteAsync(work.FolderId);
            await client.FolderDeleteAsync(play.FolderId);

            var vm = new TrashViewModel(client, new UiPortProvider { Dialogs = new SilentDialogs() });
            await vm.LoadAsync();

            foreach (var field in new[] { "name", "origin_path" })
            {
                vm.ApplySort(field, ascending: true);
                var zh = vm.Rows.Select(r => r.Id).ToList();
                LocaleService.Apply(AppLocale.En);
                vm.ApplySort(field, ascending: true);
                var en = vm.Rows.Select(r => r.Id).ToList();
                LocaleService.Apply(AppLocale.ZhCn);

                Assert.NotEmpty(zh);
                Assert.Equal(zh, en);
            }
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    // ── 默认字体族随语言（决策 5） ───────────────────────────────────────

    [Fact]
    public void 默认字体族_中文雅黑_英文SegoeUI()
    {
        Assert.Equal(FontCatalog.ZhDefaultUiFamily, FontCatalog.DefaultUiFamily(null));
        Assert.Equal(FontCatalog.ZhDefaultUiFamily, FontCatalog.DefaultUiFamily(""));
        Assert.Equal(FontCatalog.ZhDefaultUiFamily, FontCatalog.DefaultUiFamily("zh-CN"));
        Assert.Equal(FontCatalog.ZhDefaultUiFamily, FontCatalog.DefaultUiFamily("zh-TW"));   // 认不出来一律中文
        Assert.Equal(FontCatalog.EnDefaultUiFamily, FontCatalog.DefaultUiFamily("en"));
        Assert.Equal(FontCatalog.EnDefaultUiFamily, FontCatalog.DefaultUiFamily("en-US"));
        Assert.Equal(FontCatalog.EnDefaultUiFamily, FontCatalog.DefaultUiFamily("EN-gb"));
        Assert.Equal(FontCatalog.ZhDefaultUiFamily, FontCatalog.DefaultUiFamily("de-DE"));   // 第三语言未支持 → 中文
    }

    /// <summary>
    /// <b>用户显式选过的族跨语言不变</b>（用户选择优先于语言）：
    /// 偏好里写了族名 → 两种语言下都用它；只有"没选过（空）"才按语言给默认族。
    /// </summary>
    [Fact]
    public void 偏好里显式选过的字体_跨语言不变_没选过才按语言()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var lang in new[] { "zh-CN", "en-US" })
            {
                ThemeService.SetActiveLanguage(lang);

                // 显式选过：原样用它（哪怕它等于另一种语言的默认族）
                ThemeService.ApplyFonts("Consolas", null, resources: null);
                Assert.Equal("Consolas", ThemeService.CurrentUiFont);

                // 没选过（null / 空串）= 当前语言的默认族
                ThemeService.ApplyFonts(null, null, resources: null);
                Assert.Equal(FontCatalog.DefaultUiFamily(lang), ThemeService.CurrentUiFont);
                ThemeService.ApplyFonts("  ", null, resources: null);
                Assert.Equal(FontCatalog.DefaultUiFamily(lang), ThemeService.CurrentUiFont);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            ThemeService.ResetForTests();
        }
    }

    /// <summary>
    /// "恢复默认字体"落盘时**归一成"没选过"（空）**：否则那个具体族名会被当成
    /// "用户显式选过的族"，之后切语言时默认族永远不跟着走。
    /// </summary>
    [Fact]
    public void 恢复默认字体_落盘成没选过_显式选择才写族名()
    {
        var path = UiPreferenceStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            ThemeService.SetActiveLanguage("en-US");
            ThemeService.ApplyFonts(null, null, resources: null);       // 英文默认族 = Segoe UI
            ThemeService.SaveCurrentPreferences();
            Assert.Null(UiPreferenceStore.Load(out _).Fonts.Ui);        // 落成"没选过"

            ThemeService.ApplyFonts("Consolas", null, resources: null); // 用户显式选过
            ThemeService.SaveCurrentPreferences();
            Assert.Equal("Consolas", UiPreferenceStore.Load(out _).Fonts.Ui);
        }
        finally
        {
            UiPreferenceStore.Clear();
            if (backup is not null) File.WriteAllText(path, backup);
            ThemeService.ResetForTests();
        }
    }

    /// <summary>
    /// 启动恢复路径：偏好里**没有**字体字段（老偏好 / 新装）时，界面字体 = 当前语言的默认族。
    /// </summary>
    /// <remarks>
    /// 顺序**照生产启动序**：先由 I18n 解释偏好里的语言（<c>AppLocales.Resolve</c>），
    /// 把结论登记给 ThemeService，然后才 <c>ApplyFromPreferences</c> ——
    /// 默认字体族就是在那一格里定的（App.OnStartup 的三条启动序）。
    /// </remarks>
    [Fact]
    public void 启动恢复_偏好无字体字段_用当前语言的默认族()
    {
        var path = UiPreferenceStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            foreach (var (lang, family) in new[]
                     {
                         ("zh-CN", FontCatalog.ZhDefaultUiFamily),
                         ("en-US", FontCatalog.EnDefaultUiFamily),
                     })
            {
                UiPreferenceStore.Save(new UiPreferences());   // 只有默认值：Language=auto、Fonts 为空
                ThemeService.ResetInMemoryForRestartTests();
                ThemeService.SetActiveLanguage(lang);

                ThemeService.ApplyFromPreferences(resources: null);

                Assert.Equal(family, ThemeService.CurrentUiFont);
            }
        }
        finally
        {
            UiPreferenceStore.Clear();
            if (backup is not null) File.WriteAllText(path, backup);
            ThemeService.ResetForTests();
        }
    }

    /// <summary>
    /// 度量自检的基准 = **当前语言的默认族**：英文下拿雅黑当基准会把两个默认族的宽度差算进结论。
    /// </summary>
    [Fact]
    public void 度量自检基准_按语言给()
    {
        // 默认族自己检自己：两种语言的基准下都必须恰好 0 偏差
        foreach (var (lang, probe) in new[]
                 {
                     ("zh-CN", "LinkPocket 书签管理 0123"),
                     ("en-US", "LinkPocket Bookmarks 0123"),
                 })
        {
            ThemeService.SetActiveLanguage(lang);
            var baseline = ThemeService.DefaultUiFont;
            var verdict = FontMetricsProbe.Inspect(FontCatalog.BuildTokenValue(baseline), probe, baseline);

            Assert.True(verdict.Ok, $"{lang}: {verdict.Code}");
            Assert.Equal(0.0, verdict.WidthDelta, 6);
        }
    }

    /// <summary>没有对话框交互的测试替身（本类只读 VM 排序，不弹窗）。</summary>
    private sealed class SilentDialogs : IDialogService
    {
        public bool ConfirmDeleteFolder(string folderName) => true;
        public bool Confirm(string title, string message, string? confirmText = null, string iconKind = "delete-outline") => true;
        public void Alert(string title, string message) { }
    }
}
