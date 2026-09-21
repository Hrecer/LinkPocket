using System;
using LinkPocket.I18n;

namespace LinkPocket.I18n;

/// <summary>
/// 文案值：<b>键 + 参数</b>，不是文本。UI 层的模型成员用它承载"会说一句话"的状态。
/// </summary>
/// <remarks>
/// <para>
/// 本仓的文本不变式：<b>文本只在渲染边界存在</b>。模型里若存过成品字符串，
/// "换语言之后它还新不新"就变成一个需要每个宿主记住的问题；存 <see cref="LocValue"/> 则
/// 由取词绑定（含 <see cref="LocTable.Version"/>）在语言一变时自动重算，没有任何可忘的地方。
/// </para>
/// <para>
/// 参数在<b>构造的那一刻</b>捕获（"已复制 3 个链接"里的 3 是当时的事实），
/// 语言只决定这句话怎么说。数字格式化走 <see cref="System.Globalization.CultureInfo.InvariantCulture"/>。
/// </para>
/// </remarks>
public readonly record struct LocValue(string Key, ReadOnlyMemory<object?> Args)
{
    /// <summary>空值（界面显示为空，不是"未翻译"）。</summary>
    public static LocValue Empty { get; } = new(string.Empty, Array.Empty<object?>());

    /// <summary>没有参数的纯文本键。</summary>
    public static LocValue Of(string key) => new(key, Array.Empty<object?>());

    /// <summary>
    /// 路径投影的保留键：参数是 <b>canonical</b> 路径（<c>@root/A</c>），渲染时经
    /// <see cref="BookmarkDisplay.Path"/> 投影成当前语言的显示串（根段换名、其余原样）。
    /// 与 XAML 的 <c>{loc:Segment}</c> 同一个概念——投影不是透传，更不是把显示串存进模型。
    /// </summary>
    public const string PathKey = "@path";

    /// <summary>canonical 路径 → 显示串的文案值（代码里建表格单元格 / 详情行时用）。</summary>
    public static LocValue Projection(string? canonical) => new(PathKey, new object?[] { canonical ?? string.Empty });

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    /// <summary>路径投影值（canonical → 显示串）。</summary>
    public bool IsPath => Key == PathKey;

    /// <summary>在当前语言下取词——<b>只允许渲染边界调用</b>。嵌套的 <see cref="LocValue"/> 参数一起解析。</summary>
    public string Resolve() => IsEmpty ? string.Empty
        : IsPath ? BookmarkDisplay.Path(Args.Span[0] as string)
        : Loc.T(Key, Args.ToArray());
}

/// <summary>
/// 取词门面的"造值"侧：模型成员一律用 <c>Loc.K(...)</c>，不用 <c>Loc.T(...)</c>。
/// </summary>
public static partial class Loc
{
    /// <summary>构造一个文案值（不取词）。</summary>
    public static LocValue K(string key) => LocValue.Of(key);

    /// <summary>构造一个带位置参数的文案值（<c>{0}</c>…）。</summary>
    public static LocValue K(string key, params object?[] args) => new(key, args);

    /// <summary>复数文案值：调用点显式给两条完整键（中文侧两条同文）。</summary>
    public static LocValue PluralK(long n, string oneKey, string otherKey, params object?[] args)
        => new(n == 1 || Table.Locale != AppLocale.En ? oneKey : otherKey, args);
}
