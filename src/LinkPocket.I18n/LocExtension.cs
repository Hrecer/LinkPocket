using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace LinkPocket.I18n;

/// <summary>
/// 取词 markup extension（XAML 侧唯一入口），四条通道同一种形状（模型里流的是**键或文案值**，
/// 不是文本——这是"切语言不重启"能成立的前提）：
/// <list type="bullet">
/// <item><c>{loc:Loc nav.root.bookmarks}</c> = 字面键</item>
/// <item><c>{loc:LocKey LabelKey}</c> = 键来自绑定属性</item>
/// <item><c>{loc:Value Status}</c> = <see cref="LocValue"/>（键 + 参数）来自绑定属性</item>
/// <item><c>{loc:Segment Name}</c> = 路径段投影（根 token 换语言，用户数据原样）</item>
/// </list>
/// </summary>
/// <remarks>
/// 两者都编译成"带 <see cref="LocTable.Version"/> 的 MultiBinding"：版本只当失效触发器用，
/// 真正的取词发生在 <see cref="LocResolver"/> 里。于是语言一变，全树取词点自动重算，
/// 不需要任何 VM 订阅、也不会出现"某页忘了订阅"的混语残留。
/// <para>
/// 不用 <c>{DynamicResource}</c>：它解析不了"键来自绑定属性"这一大类，且缺键时静默不显示。
/// </para>
/// </remarks>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string? Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
        => LocBinding.Literal(Key).ProvideValue(serviceProvider);
}

/// <summary>键来自绑定属性（值 = 当前语言下该键的文本）。用法：<c>{loc:LocKey LabelKey}</c>。</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocKeyExtension : MarkupExtension
{
    public LocKeyExtension() { }

    public LocKeyExtension(string path) => Path = path;

    [ConstructorArgument("path")]
    public string? Path { get; set; }

    /// <summary>绑定源用名字指定（<c>{loc:LocKey HeaderKey, ElementName=Root}</c>）。</summary>
    public string? ElementName { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
        => LocBinding.FromKeyPath(Path, ElementName).ProvideValue(serviceProvider);
}

/// <summary>
/// 文案值通道：模型流 <see cref="LocValue"/>（键 + 参数），这里在渲染边界取词。
/// 用法：<c>{loc:Value Status}</c>。
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class ValueExtension : MarkupExtension
{
    public ValueExtension() { }

    public ValueExtension(string path) => Path = path;

    [ConstructorArgument("path")]
    public string? Path { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
        => LocBinding.FromValuePath(Path).ProvideValue(serviceProvider);
}

internal static class LocBinding
{
    /// <summary>字面键：只挂版本触发器，键走 ConverterParameter（避免"空路径绑定"的歧义）。</summary>
    public static MultiBinding Literal(string? key)
    {
        var mb = New();
        mb.ConverterParameter = key;
        return mb;
    }

    /// <summary>绑定键：版本触发器 + 该路径的键值（<paramref name="elementName"/> 非空时按名字找源）。</summary>
    public static MultiBinding FromKeyPath(string? path, string? elementName = null)
    {
        var mb = New();
        if (!string.IsNullOrEmpty(path))
            mb.Bindings.Add(new Binding(path) { Mode = BindingMode.OneWay, ElementName = elementName });
        return mb;
    }

    /// <summary>绑定文案值：版本触发器 + 该路径的 <see cref="LocValue"/>。</summary>
    public static MultiBinding FromValuePath(string? path)
    {
        var mb = new MultiBinding { Converter = LocValueResolver.Instance, Mode = BindingMode.OneWay };
        mb.Bindings.Add(new Binding(nameof(LocTable.Version)) { Source = LocTable.Instance, Mode = BindingMode.OneWay });
        if (!string.IsNullOrEmpty(path)) mb.Bindings.Add(new Binding(path) { Mode = BindingMode.OneWay });
        return mb;
    }

    /// <summary>路径段投影：版本触发器 + 该路径的段值（根 token 换语言，其余原样）。</summary>
    public static MultiBinding SegmentPath(string? path)
    {
        var mb = new MultiBinding { Converter = SegmentResolver.Instance, Mode = BindingMode.OneWay };
        mb.Bindings.Add(new Binding(nameof(LocTable.Version)) { Source = LocTable.Instance, Mode = BindingMode.OneWay });
        if (!string.IsNullOrEmpty(path)) mb.Bindings.Add(new Binding(path) { Mode = BindingMode.OneWay });
        return mb;
    }

    /// <summary>
    /// 自适应文案（字面键）：版本触发器 + 键走 ConverterParameter。
    /// 产物是<b>新的</b> <see cref="LocText"/> 实例——值变了才会被下游（LocFit）当成"文案换了"，
    /// 因此"切语言 → 重新自适应一次"是绑定的自然结果，不需要任何宿主登记。
    /// </summary>
    public static MultiBinding FitLiteral(string key, string? shortKey)
    {
        var mb = new MultiBinding
        {
            Converter = FitResolver.Instance,
            ConverterParameter = new KeyPair(key, shortKey),
            Mode = BindingMode.OneWay,
        };
        mb.Bindings.Add(new Binding(nameof(LocTable.Version)) { Source = LocTable.Instance, Mode = BindingMode.OneWay });
        return mb;
    }

    /// <summary>自适应文案（键来自绑定属性）：版本触发器 + 该路径的键值。</summary>
    public static MultiBinding FromFitKeyPath(string? path, string? elementName = null)
    {
        var mb = new MultiBinding
        {
            Converter = FitResolver.Instance,
            ConverterParameter = KeyPair.ShortByConvention,
            Mode = BindingMode.OneWay,
        };
        mb.Bindings.Add(new Binding(nameof(LocTable.Version)) { Source = LocTable.Instance, Mode = BindingMode.OneWay });
        if (!string.IsNullOrEmpty(path))
            mb.Bindings.Add(new Binding(path) { Mode = BindingMode.OneWay, ElementName = elementName });
        return mb;
    }

    /// <summary>
    /// 自适应文案（<see cref="LocText"/> 来自模型）：版本触发器 + 该路径的值。
    /// 版本那一路是**唯一的失效机制**——模型成员是普通属性、不发通知，没有它切语言就不会重读。
    /// </summary>
    public static MultiBinding FromFitValuePath(string? path)
    {
        var mb = new MultiBinding { Converter = FitValueResolver.Instance, Mode = BindingMode.OneWay };
        mb.Bindings.Add(new Binding(nameof(LocTable.Version)) { Source = LocTable.Instance, Mode = BindingMode.OneWay });
        if (!string.IsNullOrEmpty(path)) mb.Bindings.Add(new Binding(path) { Mode = BindingMode.OneWay });
        return mb;
    }

    /// <summary>一条自适应文案的两个键（<c>ShortByConvention</c> = 短式键按 <c>#short</c> 约定推）。</summary>
    public readonly record struct KeyPair(string Key, string? ShortKey)
    {
        public static KeyPair ShortByConvention { get; } = new(string.Empty, null);
    }

    private static MultiBinding New()
    {
        var mb = new MultiBinding { Converter = LocResolver.Instance, Mode = BindingMode.OneWay };
        mb.Bindings.Add(new Binding(nameof(LocTable.Version))
        {
            Source = LocTable.Instance,
            Mode = BindingMode.OneWay,
        });
        return mb;
    }
}

/// <summary>
/// 取词解析器。<b>单向</b>：界面显示的文本永远不写回任何状态（写回去就是把"显示"当成了"事实"）。
/// </summary>
public sealed class LocResolver : IMultiValueConverter
{
    public static LocResolver Instance { get; } = new();

    /// <param name="values">[0] = 语言版本（仅作失效触发器）；[1]（可选）= 来自绑定属性的键。</param>
    /// <param name="parameter">字面键（<see cref="LocExtension"/> 走这条）。</param>
    public object? Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var key = values.Length > 1 ? values[1] as string : parameter as string;
        return string.IsNullOrEmpty(key) ? string.Empty : Loc.Table.Get(key!);
    }

    public object[]? ConvertBack(object? value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException("resolution is one-way: displayed text is not state.");
}

/// <summary>
/// 路径段投影：<c>{loc:Segment Name}</c> —— 虚根 token（<c>@root</c> / <c>@trash</c>）与断链哨兵换成
/// 当前语言的显示名，其余段（用户自己的文件夹名）原样通过。
/// </summary>
/// <remarks>
/// 存在的理由是让<b>模型里只放身份</b>：树节点与面包屑段的根名一旦烤成"全部书签"这类字符串，
/// 换语言就得靠宿主记得重跑重建；而模型存 token + 模板投影，版本一变绑定自己重算，
/// 宿主没有任何可以忘记的地方。
/// </remarks>
[MarkupExtensionReturnType(typeof(object))]
public sealed class SegmentExtension : MarkupExtension
{
    public SegmentExtension() { }

    public SegmentExtension(string path) => Path = path;

    [ConstructorArgument("path")]
    public string? Path { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
        => LocBinding.SegmentPath(Path).ProvideValue(serviceProvider);
}

/// <summary>
/// 自适应文案通道（<c>loc:LocFit.Text</c> 专用）：<c>{loc:Fit some.key}</c> = 带可选短式变体的字面键。
/// </summary>
/// <remarks>
/// 与 <see cref="LocExtension"/> 的差别只有一处：产物是 <see cref="LocText"/>（全长 + 短式两条通道），
/// 而不是一个已经取好词的字符串。自适应随时要在两者之间换（取决于可用宽与字号），
/// 一次性字符串到不了降级链的第 ③ 步（"换短式"）。
/// <para>
/// 短式键缺省 = <c>键 + "#short"</c>（有没有由表决定，不必逐处写）；要指向别的键就显式给 <see cref="ShortKey"/>。
/// </para>
/// </remarks>
[MarkupExtensionReturnType(typeof(object))]
public sealed class FitExtension : MarkupExtension
{
    public FitExtension() { }

    public FitExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string? Key { get; set; }

    /// <summary>显式短式键；缺省 = <c>Key + "#short"</c>。</summary>
    public string? ShortKey { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key)) return LocText.Empty;
        return LocBinding.FitLiteral(Key!, ShortKey ?? Key + Loc.ShortSuffix).ProvideValue(serviceProvider);
    }
}

/// <summary>键来自绑定属性的自适应文案通道：<c>{loc:FitKey LabelKey}</c>。</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class FitKeyExtension : MarkupExtension
{
    public FitKeyExtension() { }

    public FitKeyExtension(string path) => Path = path;

    [ConstructorArgument("path")]
    public string? Path { get; set; }

    /// <summary>绑定源按名字指定（<c>{loc:FitKey HeaderKey, ElementName=Root}</c>）。</summary>
    public string? ElementName { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
        => LocBinding.FromFitKeyPath(Path, ElementName).ProvideValue(serviceProvider);
}

/// <summary>
/// <c>LocText</c> 来自模型的绑定通道：<c>{loc:FitValue ModifiedText}</c>。
/// </summary>
/// <remarks>
/// <b>为什么不能直接写 <c>{Binding ModifiedText}</c></b>：那样这条通道上<b>没有任何东西对语言版本敏感</b>。
/// 模型成员（<c>LocText</c>）本身是普通属性、不发通知，取词又发生在它构造的那一刻——
/// 于是切语言后绑定不重算，界面上留着上一种语言的日期（实测：模型侧已经是
/// <c>09/20/2026 10:50 AM</c>，屏幕上仍画着 <c>2026-09-20 10:50</c>）。
/// 本扩展把语言版本一起挂进 MultiBinding，让它在语言一变时重取模型成员，
/// 与 <c>{loc:Value}</c> / <c>{loc:Loc}</c> 是同一套失效机制。
/// </remarks>
[MarkupExtensionReturnType(typeof(object))]
public sealed class FitValueExtension : MarkupExtension
{
    public FitValueExtension() { }

    public FitValueExtension(string path) => Path = path;

    [ConstructorArgument("path")]
    public string? Path { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
        => LocBinding.FromFitValuePath(Path).ProvideValue(serviceProvider);
}

/// <summary>
/// 文案值解析器：<see cref="LocValue"/>（键 + 参数）→ 当前语言的文本。
/// 与 <see cref="LocResolver"/> 同构，同样吃 <see cref="LocTable.Version"/> 作失效触发器。
/// </summary>
public sealed class LocValueResolver : IMultiValueConverter
{
    public static LocValueResolver Instance { get; } = new();

    /// <param name="values">[0] = 语言版本（仅作失效触发器）；[1] = 模型里的 <see cref="LocValue"/>。</param>
    public object? Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => values.Length > 1 && values[1] is LocValue value ? value.Resolve() : string.Empty;

    public object[]? ConvertBack(object? value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException("resolution is one-way: displayed text is not state.");
}

/// <summary>
/// 路径段解析器：虚根 token 与断链哨兵换成当前语言的显示名，其余段（用户自己的文件夹名）原样通过。
/// 与 <see cref="LocResolver"/> 的区别是本表<b>不查键</b>——传进来的多半是用户数据。
/// </summary>
public sealed class SegmentResolver : IMultiValueConverter
{
    public static SegmentResolver Instance { get; } = new();

    public object? Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => values.Length > 1 ? BookmarkDisplay.Segment(values[1] as string) : string.Empty;

    public object[]? ConvertBack(object? value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException("projection is one-way: displayed text is not identity.");
}

/// <summary>
/// 自适应文案解析器：产物是 <see cref="LocText"/>（键对，不是取好词的字符串）。
/// 每次求值都造新实例，因此"语言版本变了 → 绑定重算 → 下游认作文案换了"这条链自然成立。
/// </summary>
public sealed class FitResolver : IMultiValueConverter
{
    public static FitResolver Instance { get; } = new();

    /// <param name="values">[0] = 语言版本（仅作失效触发器）；[1]（可选）= 来自绑定属性的键。</param>
    /// <param name="parameter"><see cref="LocBinding.KeyPair"/>：字面键 + 显式短式键；<c>ShortByConvention</c> = 键取绑定值。</param>
    public object? Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var pair = parameter as LocBinding.KeyPair?;
        var key = pair is { Key.Length: > 0 }
            ? pair.Value.Key
            : values.Length > 1 ? values[1] as string : null;
        if (string.IsNullOrEmpty(key)) return LocText.Empty;

        var shortKey = pair is { Key.Length: > 0 }
            ? pair.Value.ShortKey
            : key + Loc.ShortSuffix;
        return LocText.Key(key!, shortKey);
    }

    public object[]? ConvertBack(object? value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException("resolution is one-way: displayed text is not state.");
}

/// <summary>
/// 自适应文案的"值来自模型"解析器：把该路径的 <see cref="LocText"/> 投出去，
/// 并<b>把语言版本烙进这个值</b>（取词与长度形态的判断仍在 <c>LocFit</c> 里）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须由本解析器烙版本</b>（实测根因，改这里之前先读）：本绑定第二路指向的是模型上的
/// <b>普通属性</b>（如 <c>BrowserRowViewModel.ModifiedText</c>）。语言一变，WPF 会重算这条
/// <c>MultiBinding</c>，但<b>复用第二路子绑定的缓存值、不再调 getter</b>——
/// 实测 getter 全程只被读了一次，重算三次拿到的都是同一个旧值。
/// </para>
/// <para>
/// 于是"重算出来的值"与"上一次的值"<b>值相等</b>，WPF 属性系统判定没变、不推给目标属性，
/// 依赖这个值的下游（<c>LocFit</c> 的两级投影）一次都不会被唤醒，屏幕上继续画上一种语言。
/// </para>
/// <para>
/// 本解析器手里同时有"版本"（<c>values[0]</c>）与"文案"（<c>values[1]</c>），所以由它把两者合成一个值：
/// 版本一变，产出值就<b>真的不同</b>，属性系统的变更推送自己会把这件事传到底——
/// 用的是 WPF 本来的机制，不是另装一套派发。
/// </para>
/// <para>
/// 顺带说明那条版本子绑定的正确定位：它<b>不是</b>"失效触发器"（触发器不会让模型重新取值），
/// 而是<b>把版本号送到本解析器手里</b>的那一路。
/// </para>
/// </remarks>
public sealed class FitValueResolver : IMultiValueConverter
{
    public static FitValueResolver Instance { get; } = new();

    /// <param name="values">[0] = 语言版本；[1] = 模型里的 <see cref="LocText"/>。</param>
    public object? Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is not LocText text) return null;
        var version = values[0] is int v ? v : Loc.Table.Version;
        return text.LangVersion == version ? text : text with { LangVersion = version };
    }

    public object[]? ConvertBack(object? value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException("resolution is one-way: displayed text is not state.");
}
