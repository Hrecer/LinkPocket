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
    /// 语言已换。<b>只给宿主级副作用用</b>（写回偏好、刷新窗口标题一类）——显示文本的重算
    /// 一律由取词绑定自己完成（<c>{loc:Loc}</c> / <c>{loc:Value}</c> 都带版本失效），
    /// 不存在"某个宿主忘了注册就残留"的问题，所以这里不再有逐宿主的重投影注册表。
    /// </summary>
    public static event Action<AppLocale>? LanguageChanged;

    /// <summary>
    /// 换表之后的**显示重算钩子**（由持有"取词结果如何落到控件"的那一层注册）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么需要它：<c>{loc:Loc}</c> / <c>{loc:Value}</c> 这类绑定自带版本失效、换表即生效，
    /// 但<b>带自适应形态判断的通道不一样</b>——它要把"绑定值 + 可用宽 + 字号"一起重新求一遍，
    /// 而"可用宽"只有布局之后才知道，所以那一步必须在换表<b>之后</b>由界面层驱动一次。
    /// </para>
    /// <para>
    /// 依赖方向上不能反过来（本层是零依赖叶子、不许引 UIKit），所以用注册点：
    /// <c>LocFit</c> 在静态构造里注册自己的 <c>RefreshAll</c>。
    /// 调用时机是"<see cref="LocTable.Reload"/> 已返回、<see cref="LanguageChanged"/> 已发完"——
    /// 此刻监听方读到的必然是新语言的完整状态。
    /// </para>
    /// </remarks>
    public static Action? AfterApply { get; set; }

    /// <summary>应用语言（幂等）。整表一次换入，与主题发布同一语义。</summary>
    public static void Apply(AppLocale target)
    {
        if (Loc.Table.Locale == target) return;
        Loc.Table.Reload(target);
        LanguageChanged?.Invoke(target);

        // 最后一步：显示重算（界面层注册的钩子自己会排到本次派发之后，见 LocFit.RefreshAll）
        AfterApply?.Invoke();
    }
}
