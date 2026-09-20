using System.Windows;
using LinkPocket.Contracts;
using LinkPocket.Theming.Color;
using LinkPocket.Theming.Publishing;
using LinkPocket.Theming.Themes;

namespace LinkPocket.Theming;

/// <summary>
/// **主题服务**：全站唯一的主题入口（<see cref="Apply"/> / <see cref="Current"/> / <see cref="Changed"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 纪律（方案 §5.8）：<c>M3Theme.Apply</c> + 权威表写入**只在 <see cref="ThemePublisher"/> 里出现一次**；
/// 宿主（<c>App</c>）与探针**都只能经本类**——这是"治双份事实源"的落点
/// （原先 App 与 SmartProbe 各抄了一份"Apply + 3 个表面补丁"）。
/// </para>
/// <para>
/// <b>为什么不提供"只调 Publish 不调 Apply"</b>：库模板自己消费的键（浮层内部等）需要基线，
/// 少一步会出现库默认紫的孤岛。
/// </para>
/// </remarks>
public static class ThemeService
{
    private static ThemeDefinition _current = ThemeCatalog.Default;
    private static TokenTable? _table;

    /// <summary>主题已应用（控件/转换器无需接线：全部走 <c>DynamicResource</c>）。</summary>
    public static event EventHandler<ThemeDefinition>? Changed;

    /// <summary>当前主题定义。</summary>
    public static ThemeDefinition Current => _current;

    /// <summary>当前令牌表（首次访问时按当前主题求解；未 Apply 也可读，便于单测与预览）。</summary>
    public static TokenTable Table => _table ??= PaletteSolver.Solve(_current);

    /// <summary>
    /// 应用一个主题定义：求解 → 发布 → 记留痕 → 抛 <see cref="Changed"/>。
    /// </summary>
    /// <param name="definition">主题定义。</param>
    /// <param name="resources">目标资源字典（缺省 = <c>Application.Current.Resources</c>）。</param>
    /// <returns>本次派生出的令牌表（调用方若要立即读新值，用返回值而不是 <see cref="Table"/>）。</returns>
    public static TokenTable Apply(ThemeDefinition definition, ResourceDictionary? resources = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var table = PaletteSolver.Solve(definition);
        _current = definition;
        _table = table;

        var target = resources ?? Application.Current?.Resources;
        if (target is not null)
            ThemePublisher.Publish(target, table, table.Token(Tokens.AppTokens.AccentFill));

        ThemePublisher.LogApplied(definition, table);
        Changed?.Invoke(null, definition);
        return table;
    }

    /// <summary>按 id 应用（找不到 → 回退出厂默认；启动恢复路径用，不许崩）。</summary>
    public static TokenTable ApplyById(string? id, ResourceDictionary? resources = null) =>
        Apply(ThemeCatalog.FindOrDefault(id), resources);

    /// <summary>回到出厂默认主题。</summary>
    public static TokenTable ApplyDefault(ResourceDictionary? resources = null) =>
        Apply(ThemeCatalog.Default, resources);

    /// <summary>
    /// 只求解不发布（预览用：「外观」面板的实时预览与对比度体检都走它，不污染全局资源）。
    /// </summary>
    public static TokenTable Preview(ThemeDefinition definition) => PaletteSolver.Solve(definition);

    /// <summary>
    /// 测试/宿主收尾复位（把当前主题与缓存表还原为出厂默认，不发布）。
    /// </summary>
    public static void ResetForTests()
    {
        _current = ThemeCatalog.Default;
        _table = null;
        Changed = null;
    }

    /// <summary>应用主题这一动作的日志分类（供设置页与诊断对齐）。</summary>
    public const string LogCategory = "app.theme";
}
