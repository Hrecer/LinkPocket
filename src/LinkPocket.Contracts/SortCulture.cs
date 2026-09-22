using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace LinkPocket.Contracts;

/// <summary>
/// 把进程的<b>排序与数字格式文化</b>钉成 zh-CN（<b>只钉 culture，不钉界面语言</b>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>要解决的是什么</b>：界面语言切到英文后，若 <see cref="CultureInfo.CurrentCulture"/> 跟着变，
/// 所有按文化比较的名称排序（库内文件夹名 / 链接标题 / 回收站列 / 字体候选）会<b>重新排序</b>——
/// 用户看到的是"切个语言，列表顺序全乱了"。i18n 规划把这条写成硬约束：
/// <b>切语言不得改变任何排序结果</b>，所以排序文化必须与界面语言解耦。
/// </para>
/// <para>
/// <b>为什么钉在契约层 + 模块初始化器</b>：这是<b>进程级一次性</b>设置，而"运行起来"的入口有四个
/// （WPF 宿主 / 无头宿主 / 测试宿主 / 探针）。写在某个宿主里就漏掉另外三个，于是会出现
/// "同一个库在两个宿主里顺序不同"——这类分歧最难查。放在契约层的模块初始化器里，
/// 它<b>先于任何契约类型被触碰</b>（CLR 在首次访问该程序集任何成员之前跑完模块初始化器），
/// 而全站每一个程序集都直接或间接引用契约层、每一次排序都发生在它之后，
/// 于是"第一次 <see cref="NameOrder"/> 比较发生时文化已经钉好"是<b>结构性成立</b>的，不靠约定。
/// </para>
/// <para>
/// <b>不覆盖用户显式的选择</b>：只把"非中文"的文化拨到中文；已经是中文（<c>zh</c> / <c>zh-CN</c> /
/// <c>zh-TW</c> / <c>zh-Hans</c> …）就一个字节都不动。于是中文机器上的排序口径与本次规划之前完全一致
/// （"切语言不得改变顺序"的另一半是：<b>出厂口径只有一个</b>）。
/// </para>
/// <para>
/// <b>为什么不动 <see cref="CultureInfo.CurrentUICulture"/></b>：那是"跟随系统语言"的判据
/// （<c>AppLocales.FromSystemUi</c>），钉了它等于把所有机器的出厂语言变成中文。
/// 需要按语言渲染的格式化（<c>UiClock</c>）显式传文化，同样不读它。
/// </para>
/// </remarks>
public static class SortCulture
{
    /// <summary>排序/格式的固定文化名（唯一事实源）。</summary>
    public const string Name = "zh-CN";

    /// <summary>固定文化对象（只读缓存，不随线程与界面语言变）。</summary>
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo(Name);

    /// <summary>
    /// 程序集装载时钉住排序文化：没被钉过就钉，已经是中文家族就保持原样。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>public</c> 只因为<b>模块初始化器必须是公开可见的成员</b>（编译器强制），
    /// 它不是给调用方用的入口——排序口径请走 <see cref="NameOrder"/>。
    /// </para>
    /// <para>
    /// ⚠️ <c>CA2255</c>（"模块初始化器只用于应用程序代码"）在这里<b>不适用</b>：
    /// 契约层是每一个宿主与每一个测试进程都必然装载的那个程序集，
    /// 排序口径必须在<b>任何排序发生之前</b>成立——而"哪个宿主先起来"恰恰是不该被依赖的东西。
    /// 规则要挡的是"库在模块初始化器里做不可预期的重活"；这里只写两个静态字段，无 IO、无锁。
    /// </para>
    /// </remarks>
#pragma warning disable CA2255 // 见上面的理由：契约层的模块初始化器是"全站排序口径"的落点
    [ModuleInitializer]
    public static void Pin()
    {
        if (IsChinese(CultureInfo.CurrentCulture)) return;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.CurrentCulture = Culture;
    }
#pragma warning restore CA2255

    /// <summary>
    /// 这个文化是不是"中文家族"。保留 <c>zh-TW</c> 不动是有意的：
    /// 它已经是中文排序，改成简体反而是替用户改口径。
    /// </summary>
    public static bool IsChinese(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
    }
}
