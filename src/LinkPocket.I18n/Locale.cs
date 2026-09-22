using System;
using System.Globalization;
using LinkPocket.Contracts;

namespace LinkPocket.I18n;

/// <summary>支持的语言。加一门语言 = 加一个枚举值 + 在 <see cref="StringTables"/> 里补一列，根别名与保留名自动跟着表走。</summary>
public enum AppLocale
{
    /// <summary>简体中文（出厂缺省）。</summary>
    ZhCn = 0,

    /// <summary>英文。</summary>
    En = 1,
}

/// <summary>
/// 语言口径。<b>排序与数字不随界面语言变</b>（切语言不得改变任何顺序），文化只在两处出现：
/// 英文日期的 12 小时制标记，以及"跟随系统"的判定。
/// </summary>
public static class AppLocales
{
    /// <summary>出厂缺省语言。</summary>
    public const AppLocale Default = AppLocale.ZhCn;

    /// <summary>排序与数字的固定文化（语言切换不碰它）。</summary>
    public static CultureInfo Order { get; } = CultureInfo.GetCultureInfo("zh-CN");

    /// <summary>界面语言码（偏好里存的就是它，也是 <see cref="TryParse"/> 的输入）。</summary>
    public static string CodeOf(this AppLocale locale) => locale == AppLocale.En ? LocalePreference.CodeEn : LocalePreference.CodeZh;

    /// <summary>
    /// 偏好 → 语言。<b>认不出来不静默回退</b>：<paramref name="fellBack"/> 置真并交调用方向用户提示一次
    /// （与"偏好文件损坏 → 回退默认外观 + 提示"同一条口径）。
    /// </summary>
    /// <param name="mode"><c>auto</c> / <c>fixed</c>（取值域 = <c>Contracts.LocalePreference</c>）。</param>
    /// <param name="overrideCode">固定模式下的语言码。</param>
    public static AppLocale Resolve(string? mode, string? overrideCode, out bool fellBack)
    {
        fellBack = false;
        var normalized = mode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized == LocalePreference.ModeAuto) return FromSystemUi();
        if (normalized != LocalePreference.ModeFixed)
        {
            fellBack = true;
            return FromSystemUi();
        }
        if (TryParse(overrideCode, out var fixedLocale)) return fixedLocale;

        fellBack = true;
        return FromSystemUi();
    }

    /// <summary>固定到某语言的偏好写法（语言卡「简体中文 / English」两枚走这条）。</summary>
    public static (string Mode, string? OverrideCode) FixedPreference(this AppLocale locale)
        => (LocalePreference.ModeFixed, locale.CodeOf());

    /// <summary>跟随系统的偏好写法。</summary>
    public static (string Mode, string? OverrideCode) FollowSystemPreference()
        => (LocalePreference.ModeAuto, null);

    /// <summary>
    /// 这门语言在<b>当前界面语言</b>下叫什么（给语言选择器用）。
    /// </summary>
    /// <remarks>
    /// <b>给的是键，不是文本</b>：语言选择器上的选项名也是文案，换语言后要跟着变成"用新语言写的名字"
    /// （中文界面写「简体中文」，英文界面写 "Simplified Chinese"）。返回成品 <c>string</c>
    /// 就等于把它冻结在取词那一刻——那正是本仓文本不变式禁止的形状。
    /// 加一门语言 = 加一个枚举值 + 补一行 <c>appearance.language.*</c>（两表键对称的护栏会盯着）。
    /// </remarks>
    public static string KeyFor(this AppLocale locale) => locale switch
    {
        AppLocale.En => "common.english",
        _ => "common.chinese",
    };

    /// <summary>
    /// 日期格式化文化。英文**必须**显式取 en-US：进程 culture 为守排序钉在 zh-CN，
    /// 不显式传则 <c>tt</c> 渲染成「下午」。
    /// </summary>
    public static CultureInfo FormatOf(this AppLocale locale)
        => locale == AppLocale.En ? CultureInfo.GetCultureInfo("en-US") : Order;

    /// <summary>偏好里的字符串 → 语言。认不出来即损坏（不猜意图，交调用方如实暴露）。</summary>
    public static bool TryParse(string? code, out AppLocale locale)
    {
        locale = Default;
        if (string.IsNullOrWhiteSpace(code)) return false;
        switch (code.Trim().ToLowerInvariant())
        {
            case "zh":
            case "zh-cn":
                locale = AppLocale.ZhCn;
                return true;
            case "en":
            case "en-us":
            case "en-gb":
                locale = AppLocale.En;
                return true;
            default:
                return false;
        }
    }

    /// <summary>「跟随系统」：只有系统 UI 语言属英文系才走英文，其余（含未知语言）一律中文。</summary>
    public static AppLocale FromSystemUi()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? AppLocale.En
            : Default;
}
