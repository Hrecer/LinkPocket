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
    /// 一律由取词绑定自己完成（<c>{loc:Loc}</c> / <c>{loc:Value}</c> 都带版本失效），
    /// 不存在"某个宿主忘了注册就残留"的问题，所以这里不再有逐宿主的重投影注册表。
    /// </summary>
    public static event Action<AppLocale>? LanguageChanged;

    /// <summary>应用语言（幂等）。整表一次换入，与主题发布同一语义。</summary>
    public static void Apply(AppLocale target)
    {
        if (Loc.Table.Locale == target) return;
        Loc.Table.Reload(target);
        LanguageChanged?.Invoke(target);
    }
}
