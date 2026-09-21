using System;
using System.Globalization;

namespace LinkPocket.I18n;

/// <summary>
/// 取词门面（C# 侧唯一入口）。XAML 侧走 <see cref="LocExtension"/>，两边读同一张 <see cref="Table"/>。
/// </summary>
/// <remarks>
/// <b>瞬时文案（状态行 / Toast / 对话框）在这里解析是合规的</b>：它们只在发出那一刻存在。
/// 但语言一切换，已显示的瞬时文案就停在旧语言里——所以 <see cref="LocaleService.LanguageChanged"/>
/// 的订阅方必须把它们**清空**，而不是留着混语。会长期驻留的文本一律走 XAML 取词，不走本门面。
/// </remarks>
public static class Loc
{
    public static LocTable Table => LocTable.Instance;

    public static string T(string key) => Table.Get(key);

    /// <summary>位置参数填充（<c>{0}</c>…）。数字一律走 InvariantCulture：千分位分隔符不随语言变。</summary>
    public static string T(string key, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, Table.Get(key), args);

    /// <summary>
    /// 复数。调用点<b>显式给两条完整键</b>（中文侧两条填同一句），而不是在代码里拼 <c>#one</c>——
    /// 拼出来的键护栏查不到，等于把"缺键"从静态可查推到了运行时才发现。
    /// </summary>
    public static string Plural(long n, string oneKey, string otherKey, params object?[] args)
    {
        // 中文没有单复数形态：恒取 oneKey（两条同文，护栏的对称性检查照旧成立）
        var key = Table.Locale != AppLocale.En || n == 1 ? oneKey : otherKey;
        return args.Length == 0 ? Table.Get(key) : string.Format(CultureInfo.InvariantCulture, Table.Get(key), args);
    }
}
