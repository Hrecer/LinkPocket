using System;

namespace LinkPocket.I18n;

/// <summary>
/// 一条界面文案的<b>两个长度形态</b>：全长 + 可选短式。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由是<b>降级链的第 ③ 步</b>（见 <c>UI-SPEC §3</c> 的"几何冻结 + 字号自适应"）：
/// 英文比中文宽 30%~150%，而列宽与控件高度是冻结几何 —— 放不下时的正确处置是
/// "换一句更短的话"，<b>不是把日期截断</b>（截断的日期是错的日期）。
/// </para>
/// <para>
/// <see cref="Short"/> 为 <c>null</c> = 这条文案没有短式，降级链直接跳到截断一步；
/// 有没有短式由<b>表里有不有那条 <c>#short</c> 键</b>决定，不靠调用方记。
/// </para>
/// </remarks>
/// <param name="Full">全长形态（渲染边界用 <see cref="Resolve"/> 取当前语言的文本）。</param>
/// <param name="Short">短式形态；<c>null</c> = 无短式。</param>
/// <param name="LangVersion">
/// 这条文案**产生时**的 <see cref="LocTable.Version"/>（语言代数）。
/// </param>
/// <remarks>
/// <para>
/// <b><see cref="LangVersion"/> 为什么必须在值身份里</b>：本类型是 record struct，相等是**值相等**。
/// 模型成员（如 <c>BrowserRowViewModel.ModifiedText</c>）是普通属性、不发通知，切语言时它重新读出来的
/// <see cref="LocText"/> 与上一次<b>值相等</b>——于是"语言版本 → 绑定重算"这条失效链虽然跑了，
/// 但 WPF 属性系统判定"接入值没变"，<b>不触发变更回调</b>，自适应通道的显示文字就停在上一种语言
/// （实测：绑定透传调用 1→2→3 每切一次都跑，屏幕上的字一个字不变）。
/// </para>
/// <para>
/// 把"它属于哪个语言"写进值身份，语言一变就真的变了：接入值不再与上一次相等，
/// 下游（<c>LocFit</c> 的投影）必然被唤醒一次。这不是加机制，而是<b>让"变了"这件事如实反映出来</b>。
/// </para>
/// <para>
/// 字面量值（<see cref="LocValue.Literal"/>）刻意不带代数：它本来就是不随语言变的内容
/// （用户数据、外部传入的成品串），不该因为切语言就触发重算。
/// </para>
/// </remarks>
public readonly record struct LocText(LocValue Full, LocValue? Short, int LangVersion = 0)
{
    /// <summary>只有全长形态。</summary>
    public static LocText Of(LocValue full) => new(full, null, LocTable.Instance.Version);

    /// <summary>由键直接构造（<c>ShortKey</c> 为 <c>null</c> = 无短式）。</summary>
    public static LocText Key(string key, string? shortKey)
        => new(Loc.K(key), shortKey is null ? null : Loc.K(shortKey), LocTable.Instance.Version);

    /// <summary>不带参数的键通道的文本（XAML 的 <c>{loc:Loc}</c> 走这条）。</summary>
    public static LocText OfKey(string key) => Of(Loc.K(key));

    public bool IsEmpty => Full.IsEmpty;

    /// <summary>换语言后是不是"长"了——用于懒求值判据（短式按需解析，见 <see cref="ResolveShort"/>）。</summary>
    public bool HasShort => Short is { IsEmpty: false };

    /// <summary>全长形态的当前语言文本。</summary>
    public string Resolve() => Full.Resolve();

    /// <summary>短式形态的当前语言文本；无短式时回全长（降级链不许在这里造第三种形态）。</summary>
    public string ResolveShort() => HasShort ? Short!.Value.Resolve() : Full.Resolve();

    /// <summary>空文本（未挂文案的占位）。</summary>
    public static LocText Empty { get; } = new(LocValue.Empty, null);
}
