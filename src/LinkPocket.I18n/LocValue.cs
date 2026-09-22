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
    /// <b>成品文本</b>（不查表）：只给"本来就不是文案键"的东西用——例如
    /// <c>LocFit</c> 的 <c>string</c> 入参（用户数据、外部传入的显示串）。
    /// 文案一律走 <see cref="Of"/> / <c>Loc.K</c>：这条路径不参与"缺键显形"，误用等于绕过 G5。
    /// </summary>
    public static LocValue Literal(string text) => new(LiteralMarker, new object?[] { text });

    /// <summary>成品文本通道的哨兵键（<c>@</c> 开头，与任何文案键都不可能撞）。</summary>
    public const string LiteralMarker = "@literal";

    /// <summary>
    /// 路径投影的保留键：参数是 <b>canonical</b> 路径（<c>@root/A</c>），渲染时经
    /// <see cref="BookmarkDisplay.Path"/> 投影成当前语言的显示串（根段换名、其余原样）。
    /// 与 XAML 的 <c>{loc:Segment}</c> 同一个概念——投影不是透传，更不是把显示串存进模型。
    /// </summary>
    public const string PathKey = "@path";

    /// <summary>
    /// 时钟值的保留键：参数是 <c>(DateTime, 是否短式)</c>，渲染时按**当前**语言格式化。
    /// </summary>
    /// <remarks>
    /// <b>存在的理由</b>：日期若在构造期就格式化成文本（<see cref="Literal"/>），那条文案就被**冻结**在
    /// 取词那一刻的语言上了——版本号再怎么变，它也只是"一个已经烤好的字符串"。
    /// 实测症状：切到英文后同一条单元格里 <c>Never</c> 换了、日期没换（前者走键、后者是成品文本），
    /// 表格里于是两种语言混排。
    /// 本形态让日期与 <see cref="Projection"/>（路径）同构：<b>存的是一份身份（时刻 + 形态），
    /// 不是一句话</b>，什么时候取词由渲染边界决定。
    /// </remarks>
    public const string ClockKey = "@clock";

    /// <summary>canonical 路径 → 显示串的文案值（代码里建表格单元格 / 详情行时用）。</summary>
    public static LocValue Projection(string? canonical) => new(PathKey, new object?[] { canonical ?? string.Empty });

    /// <summary>时刻 → 当前语言日期的文案值（不进库、不落状态，只在渲染边界求值）。</summary>
    /// <param name="local">本地时间（调用方负责 <c>ToLocalTime()</c>，与全库同口径）。</param>
    /// <param name="short">是否用短式（降级链第 ③ 步的"去年份"形态）。</param>
    public static LocValue Clock(DateTime local, bool @short)
        => new(ClockKey, new object?[] { local, @short });

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    /// <summary>路径投影值（canonical → 显示串）。</summary>
    public bool IsPath => Key == PathKey;

    /// <summary>时钟值（时刻 → 当前语言日期；见 <see cref="Clock"/>）。</summary>
    public bool IsClock => Key == ClockKey;

    /// <summary>成品文本值（不查表；见 <see cref="Literal"/>）。</summary>
    public bool IsLiteral => Key == LiteralMarker;

    /// <summary>在当前语言下取词——<b>只允许渲染边界调用</b>。嵌套的 <see cref="LocValue"/> 参数一起解析。</summary>
    public string Resolve() => IsEmpty ? string.Empty
        : IsPath ? BookmarkDisplay.Path(Args.Span[0] as string)
        : IsClock ? ResolveClock()
        : IsLiteral ? Args.Span[0] as string ?? string.Empty
        : Loc.T(Key, Args.ToArray());

    /// <summary>时钟值按当前语言格式化（长式 / 短式由参数决定，见 <see cref="Clock"/>）。</summary>
    private string ResolveClock()
    {
        if (Args.Length < 2 || Args.Span[0] is not DateTime local) return string.Empty;
        return Args.Span[1] is true ? UiClock.FormatShort(local) : UiClock.Format(local);
    }

    /// <summary>
    /// 短式形态（<c>key#short</c>）的当前语言文本——降级链第 ③ 步用（<c>LocFit</c>）。
    /// </summary>
    /// <remarks>
    /// <b>无短式时回全长，绝不发警告</b>：短式是"放不下时的可选出路"，不是每条文案的义务。
    /// 缺键的显形机制（<c>⟨key⟩</c> + Warn）留给真正的拼写错误 —— 让每条没有短式的文案都刷一条
    /// 警告，等于把这个机制淹掉（观测面纪律：噪音会让真信号失效）。
    /// </remarks>
    public string ResolveShort()
    {
        if (IsPath || IsLiteral || string.IsNullOrEmpty(Key)) return Resolve();
        if (IsClock) return ResolveClock();
        return Loc.Short(Key, Args.ToArray());
    }

    /// <summary>这条文案在表里有没有短式变体（判据在表，不靠调用方记）。</summary>
    public bool HasShortForm => !IsPath && !IsLiteral && !string.IsNullOrEmpty(Key) && Loc.HasShort(Key);
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
