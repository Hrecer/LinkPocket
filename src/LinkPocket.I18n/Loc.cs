using System;
using System.Globalization;

namespace LinkPocket.I18n;

/// <summary>
/// 取词门面（C# 侧唯一入口）。XAML 侧走 <see cref="LocExtension"/>，两边读同一张 <see cref="Table"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>会驻留在界面上的文本不要用本门面的 <c>T</c></b>：它把取词时机钉死在调用那一刻，
/// 换语言后那份文本就停在旧语言里。模型成员一律 <see cref="K(string)"/> 造 <see cref="LocValue"/>，
/// 由 XAML 的 <c>{loc:Value}</c> 在渲染边界取词（语言一变自己重算）。
/// </para>
/// <para>
/// <c>T</c> 的正当用处只剩"显示完就消失"的那一类：对话框在弹出的那一刻、剪贴板写入的文本、
/// 以及日志与错误码渲染等<em>不进绑定</em>的地方。
/// </para>
/// </remarks>
public static partial class Loc
{
    public static LocTable Table => LocTable.Instance;

    public static string T(string key) => Table.Get(key);

    /// <summary>位置参数填充（<c>{0}</c>…）。数字一律走 InvariantCulture：千分位分隔符不随语言变。</summary>
    /// <summary>位置参数填充（<c>{0}</c>…）。数字一律走 InvariantCulture：千分位分隔符不随语言变。
    /// 参数里的 <see cref="LocValue"/> 在这一刻一起解析——所以嵌套的句子也不会在模型里留下成品文本。</summary>
    public static string T(string key, params object?[] args)
    {
        if (args.Length == 0) return Table.Get(key);
        var resolved = new object?[args.Length];
        for (var i = 0; i < args.Length; i++)
            resolved[i] = args[i] is LocValue nested ? nested.Resolve() : args[i];
        return string.Format(CultureInfo.InvariantCulture, Table.Get(key), resolved);
    }

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

    /// <summary>短式变体键后缀（<c>count.views</c> → <c>count.views#short</c>）。</summary>
    public const string ShortSuffix = "#short";

    /// <summary>这条文案有没有短式变体（<c>key#short</c> 在表里）。</summary>
    public static bool HasShort(string key) => Table.TryGet(key + ShortSuffix, out _);

    /// <summary>
    /// 短式变体的文本；<b>表里没有就回全长</b>（降级链的第 ③ 步是"有则换、无则跳过"，不是义务）。
    /// </summary>
    public static string Short(string key, params object?[] args)
    {
        var shortKey = key + ShortSuffix;
        if (!Table.TryGet(shortKey, out var text)) return args.Length == 0 ? Table.Get(key) : T(key, args);
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }
}
