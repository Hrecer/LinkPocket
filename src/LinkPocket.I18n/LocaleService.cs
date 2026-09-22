using System;

namespace LinkPocket.I18n;

/// <summary>
/// 语言状态的<b>唯一入口</b>（与 <c>ThemeService</c> 在主题层的地位对齐）。
/// </summary>
/// <remarks>
/// 本层不碰持久化：<c>Language</c> 字段归 <c>Theming.UiPreferences</c> 存，语义归本层，
/// 两边由组合根 <c>App</c> 搬运（启动读偏好 → <see cref="Apply"/>；<see cref="LanguageChanged"/> 里写回）。
/// 这样 <c>I18n↛Theming</c> 与 <c>Theming↛I18n</c> 同时成立。
/// </remarks>
public static class LocaleService
{
    public static AppLocale Current => Loc.Table.Locale;

    /// <summary>
    /// 语言已换。<b>只给宿主级副作用用</b>（写回偏好、刷新窗口标题一类）——显示文本的重算
    /// 一律由取词绑定自己完成（<c>{loc:Loc}</c> / <c>{loc:Value}</c> / <c>{loc:FitValue}</c>
    /// 都带版本失效），不存在"某个宿主忘了注册就残留"的问题，所以这里不再有逐宿主的重投影注册表。
    /// </summary>
    public static event Action<AppLocale>? LanguageChanged;

    /// <summary>应用语言（幂等）。整表一次换入，与主题发布同一语义。</summary>
    /// <remarks>
    /// 换表即通知，<b>没有第三步</b>。曾经有过一个"显示重算钩子"（<c>AfterApply</c>，
    /// 由 <c>LocFit</c> 注册、在 <c>DispatcherPriority.Loaded</c> 上再排一次队去强制重取绑定），
    /// 它建立在"WPF 不会因为子绑定变化而重算 MultiBinding"这个**错误前提**上：
    /// 实测那个钩子连转换器都没碰到（<c>ConvertCalls 0 → 0</c>），真正的重算一直由 WPF 自己完成。
    /// 钩子已整段删除——它属于"打补丁式修复"，且会掩盖"绑定链其实没问题"这个事实。
    /// </remarks>
    public static void Apply(AppLocale target)
    {
        if (Loc.Table.Locale == target) return;
        Loc.Table.Reload(target);

        // 唯一的收尾：通知宿主级副作用。显示文本已经由版本失效自己重算。
        LanguageChanged?.Invoke(target);
    }
}
