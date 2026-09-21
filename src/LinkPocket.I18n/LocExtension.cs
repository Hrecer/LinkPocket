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
