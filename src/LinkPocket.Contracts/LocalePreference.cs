namespace LinkPocket.Contracts;

/// <summary>
/// 语言偏好的<b>线格式</b>：模式常量与语言码取值域的唯一事实来源。
/// </summary>
/// <remarks>
/// 放在契约层是因为它有两个<b>互不能引用</b>的消费者：<c>Theming</c> 负责存
/// （<c>UiPreferences.Language</c>）、<c>I18n</c> 负责解释（<c>AppLocales.Resolve</c>）。
/// 两边各自抄一份字面量 = 两个事实源，改一边就静默漂移；契约层是它们唯一的公共落点。
/// </remarks>
public static class LocalePreference
{
    /// <summary>跟随系统 UI 语言。</summary>
    public const string ModeAuto = "auto";

    /// <summary>固定为偏好里 <c>Override</c> 指定的语言码。</summary>
    public const string ModeFixed = "fixed";

    /// <summary>简体中文的语言码。</summary>
    public const string CodeZh = "zh-CN";

    /// <summary>英文的语言码。</summary>
    public const string CodeEn = "en";
}
