using System.Collections.Generic;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels;

/// <summary>
/// 主题 id → 主题名的<b>文案键</b>。主题名是界面文本，所以它住在界面层；
/// <c>Theming</c> 只携带身份（<c>ThemeDefinition.Id</c>，kebab-case、跨语言稳定）。
/// </summary>
/// <remarks>
/// 未登记的 id <b>照抛</b>：预设表是封闭的，走到这里说明目录与界面两处漂移了——
/// 悄悄回退成一个通用名字会让"这套主题叫什么"变成不可观测的问题（本仓禁静默兜底）。
/// </remarks>
internal static class ThemeNames
{
    /// <summary>自选配色的 id（<c>ThemeService</c> 从偏好重建时用它）。</summary>
    public const string CustomId = "user-custom";

    public static LocValue Of(string id) => id switch
    {
        "factory-default" => Loc.K("theme.factoryDefault"),
        "ochre-rose" => Loc.K("theme.ochreRose"),
        "dusk-rose" => Loc.K("theme.duskRose"),
        "lotus-sage" => Loc.K("theme.lotusSage"),
        "caramel-rose" => Loc.K("theme.caramelRose"),
        "uji-matcha" => Loc.K("theme.ujiMatcha"),
        "shine-muscat" => Loc.K("theme.shineMuscat"),
        "blueberry-yogurt" => Loc.K("theme.blueberryYogurt"),
        "mint-soda" => Loc.K("theme.mintSoda"),
        "sakura-panna" => Loc.K("theme.sakuraPanna"),
        "green-pear" => Loc.K("theme.greenPear"),
        CustomId => Loc.K("theme.custom"),
        _ => throw new KeyNotFoundException($"theme '{id}' has no display name key (catalog and UI drifted)"),
    };
}
