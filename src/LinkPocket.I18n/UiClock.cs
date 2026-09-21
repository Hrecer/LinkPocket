using System;

namespace LinkPocket.I18n;

/// <summary>
/// 日期/时间的<b>唯一</b>显示出口（界面层不得再出现裸 <c>ToString("yyyy-MM-dd…")</c>，护栏 G7 卡住）。
/// </summary>
/// <remarks>
/// 中文侧沿用既有格式，一字不动；英文侧走 12 小时制，且必须带 <see cref="Locale.FormatOf"/> 给的
/// 英文文化——进程 culture 为守住排序钉在 zh-CN，不显式传则 <c>tt</c> 渲染成「下午」。
/// <para>
/// <b>短式（<see cref="EnShortPattern"/>）是降级链上"截断"之前的一步</b>：列宽是冻结几何，
/// 英文日期比中文宽约 25%，宁可去年份也不许把日期截成 <c>09/21/2026 1:4…</c>——截断的日期是错的日期。
/// </para>
/// </remarks>
public static class UiClock
{
    /// <summary>中文格式（现行基线，切语言不许动它）。</summary>
    public const string ZhPattern = "yyyy-MM-dd HH:mm";

    /// <summary>英文格式。</summary>
    public const string EnPattern = "MM/dd/yyyy h:mm tt";

    /// <summary>英文短式（列宽触底时用；中文侧无此需要，回基线格式）。</summary>
    public const string EnShortPattern = "MM/dd h:mm tt";

    /// <summary>按当前语言格式化（入参必须是本地时间——调用方负责 <c>ToLocalTime()</c>，与现状同口径）。</summary>
    public static string Format(DateTime local)
        => local.ToString(Pattern, Loc.Table.Locale.FormatOf());

    /// <summary>按当前语言格式化，用降级短式。</summary>
    public static string FormatShort(DateTime local)
        => local.ToString(ShortPattern, Loc.Table.Locale.FormatOf());

    private static string Pattern => Loc.Table.Locale == AppLocale.En ? EnPattern : ZhPattern;

    private static string ShortPattern => Loc.Table.Locale == AppLocale.En ? EnShortPattern : ZhPattern;

    /// <summary>「从未」哨兵（没有访问时间时显示的东西，不是时间格式）。</summary>
    public static string Never => Loc.T("clock.never");
}
