using System;

namespace LinkPocket.I18n;

/// <summary>
/// 语言状态的<b>唯一入口</b>（与 <c>ThemeService</c> 在主题层的地位对齐）。
/// </summary>
/// <remarks>
/// 本层不碰持久化：<c>Language</c> 字段归 <c>Theming.UiPreferences</c> 存，语义归本层，
/// 两边由组合根 <c>App</c> 搬运（启动读偏好 → <see cref="Apply"/>；<see cref="LanguageChanged"/> 里写回）。
/// 这样 <c>I18n↛Theming</c> 与 <c>Theming↛I18n</c> 同时成立。
/// </remarks>
public static class LocaleService
{
    public static AppLocale Current => Loc.Table.Locale;

    /// <summary>
    /// 语言已换。<b>只用于宿主级副作用</b>（写回偏好、清空瞬时文案）——显示重投影请走
    /// <see cref="RegisterReprojector"/>：事件是强引用，控件在构造期 += 而忘记退订就是泄漏，
    /// 而"忘记退订"比"忘记注册"更难发现。
    /// </summary>
    public static event Action<AppLocale>? LanguageChanged;

    /// <summary>宿主 → 它的重投影回调。<b>弱键</b>：宿主被回收后条目自动消失，宿主不必写退订。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Action> Reprojectors = new();

    /// <summary>
    /// 注册一个"语言一变就重跑"的显示投影回调（控件 / VM 把自己的重投影方法交进来）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不能弱持有委托本身</b>：方法组转换出来的那个委托对象是<b>临时对象</b>，除了注册表
    /// 没有别人引用它——弱持有它等于注册完就被回收，重投影静默失效（实测：列头切语言后仍是中文，
    /// 被探针的零 CJK 闸抓了出来）。所以弱键必须是<b>宿主实例</b>，回调本身强存在表值里。
    /// </para>
    /// <para>
    /// 因此回调<b>必须是宿主实例自己的方法组</b>；闭包会被当场拒绝——闭包对象同样只有注册表引用它，
    /// 而且它捕获的是局部变量而不是宿主，语义上也不该在这里发生。
    /// </para>
    /// <para>
    /// 适用判据：文本是<b>建对象时烤进模型的字符串</b>（表头文案、动作标签、树根名）。
    /// 若文本走 XAML 取词（<c>{loc:Loc}</c>），版本一变绑定自己会重算，<b>不要</b>来这里注册第二次。
    /// </para>
    /// </remarks>
    public static void RegisterReprojector(object owner, Action reproject)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(reproject);
        if (!ReferenceEquals(reproject.Target, owner))
            throw new ArgumentException(
                "the reproject callback must be a method group of the owner instance (a closure has no other reference, so the weak registration dies at once)", nameof(reproject));

        Reprojectors.AddOrUpdate(owner, reproject);
    }

    private static void FireReprojectors()
    {
        // 先取快照再逐个跑：重投影过程中可能有控件构造/回收去增删表项
        var live = System.Linq.Enumerable.ToArray(Reprojectors);
        foreach (var entry in live) entry.Value();
    }

    /// <summary>应用语言（幂等）。整表一次换入，与主题发布同一语义。</summary>
    public static void Apply(AppLocale target)
    {
        if (Loc.Table.Locale == target) return;
        Loc.Table.Reload(target);
        LanguageChanged?.Invoke(target);
        FireReprojectors();
    }
}
