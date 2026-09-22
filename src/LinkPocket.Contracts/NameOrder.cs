using System.Globalization;

namespace LinkPocket.Contracts;

/// <summary>
/// <b>名称排序的唯一口径</b>：库内文件夹名 / 链接标题 / 回收站列 / 字体候选的排序一律经这里，
/// 不再各自取 <see cref="StringComparer.CurrentCulture"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须收成一处</b>：排序口径散在各层（模块 / 数据 / UIKit / Theming / Shell）时，
/// "切语言界面顺序会不会变"就只能靠人逐个记住——界面语言的取词与文化无关，
/// 排序却会跟着 <see cref="CultureInfo.CurrentCulture"/> 走。收成一处后，
/// "切语言不得改变任何顺序"这条不变式才有唯一的落点可断言。
/// </para>
/// <para>
/// <b>文化由 <see cref="SortCulture"/> 在程序集装载时钉住</b>（进程级只做一次）。本类<b>不缓存</b>
/// 比较器、每次都读当前文化：排序永远跟着当前文化走，"钉住"这件事因此只有一处可观测
/// （<see cref="Culture"/>）——把文化捕获成静态常量会让"到底钉成什么"变成不可测的隐式状态。
/// </para>
/// <para>
/// <b>与 SQL 侧的关系</b>：库内列表的排序在 SQL 下推（<c>EfSortEngine</c>，<c>COLLATE NOCASE</c>），
/// 本类是<b>内存侧</b>（UI 树 / 回收站行 / 字体候选 / 内存兜底排序）的同一口径。
/// 两边的差异（<c>NOCASE</c> 是 ASCII 折叠、<c>CurrentCulture</c> 带语言权重）已在
/// <c>EfSortEngine</c> 的注释里承认，本次不改 SQL。
/// </para>
/// <para>
/// <b>放在契约层</b>的理由与 <see cref="SortCulture"/> / <c>LogRedactor</c> 同族：
/// 调用方一头在引擎侧（模块 / 数据），一头在界面侧（<c>UIKit</c> / UI 页 / Theming），
/// 而界面层<b>禁引 Kernel</b>（<c>DependencyRulesTests</c> 卡住）——只有契约层两侧都到得了。
/// 本类零依赖、纯函数。
/// </para>
/// </remarks>
public static class NameOrder
{
    /// <summary>名称升序（文化敏感、稳定——与既有 <c>StringComparer.CurrentCulture</c> 逐字节等价）。</summary>
    public static StringComparer Comparer => StringComparer.CurrentCulture;

    /// <summary>比较两个名称（与 <see cref="Comparer"/> 同源；给需要 <see cref="StringComparison"/> 的调用点用）。</summary>
    public static int Compare(string? x, string? y)
        => string.Compare(x, y, StringComparison.CurrentCulture);

    /// <summary>当前排序文化（读数口：判定"排序口径有没有被界面语言带跑"就看它）。</summary>
    public static CultureInfo Culture => CultureInfo.CurrentCulture;
}
