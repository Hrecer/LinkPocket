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
public readonly record struct LocText(LocValue Full, LocValue? Short)
{
    /// <summary>只有全长形态。</summary>
    public static LocText Of(LocValue full) => new(full, null);

    /// <summary>由键直接构造（<c>ShortKey</c> 为 <c>null</c> = 无短式）。</summary>
    public static LocText Key(string key, string? shortKey)
        => new(Loc.K(key), shortKey is null ? null : Loc.K(shortKey));

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
