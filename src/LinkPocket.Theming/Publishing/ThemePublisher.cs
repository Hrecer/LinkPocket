using System.Windows;
using System.Windows.Media;
using LinkPocket.Contracts;
using LinkPocket.Theming.Color;
using LinkPocket.Theming.Themes;
using LinkPocket.Theming.Tokens;
using Material3.Core;
using Material3.Wpf;

namespace LinkPocket.Theming.Publishing;

/// <summary>
/// **单点发布管线**：把一张 <see cref="TokenTable"/> 全量写进 WPF 资源字典。
/// </summary>
/// <remarks>
/// <para>
/// 顺序固定、只在这里出现一次（App 与探针都只能经 <c>ThemeService</c> 调它）：
/// <list type="number">
/// <item><c>M3Theme.Apply</c> 建**基线**——库模板自己消费的键需要一个和谐的底
/// （删掉会出现库默认紫）；</item>
/// <item>再把我们的**权威表**逐键覆盖写入（49 个库键 + 4 个应用键 + 全部 <c>App.*</c> 语义令牌）。</item>
/// </list>
/// </para>
/// <para>
/// <b>幂等</b>：同一张表重复发布 → 资源字典逐键同值（有单测断言）。
/// </para>
/// <para>
/// <b>画刷冻结</b>：写入的是 <c>Freeze()</c> 过的 <see cref="SolidColorBrush"/>——
/// 冻结后不可变，可跨线程读取，且避免绑定引擎为每次读取做变更跟踪。
/// 这也顺手堵住"某处偷偷改画刷颜色"的旁路。
/// </para>
/// </remarks>
public static class ThemePublisher
{
    /// <summary>基线的种子（M3 官方基线紫）。仅用于给未接管的库模板键一个和谐底，不影响我们的权威表。</summary>
    private static readonly Argb BaselineSeed = Argb.FromArgb(0x67, 0x50, 0xA4);

    /// <summary>
    /// 发布一张令牌表：先建基线，再全量写权威表。
    /// </summary>
    /// <param name="resources">目标资源字典（宿主传 <c>Application.Current.Resources</c>）。</param>
    /// <param name="table">派生出的令牌表。</param>
    /// <param name="baselineSeed">基线种子覆盖（缺省 = M3 基线紫；测试可传主题强调色以获得更和谐的库模板底）。</param>
    public static void Publish(ResourceDictionary resources, TokenTable table, Argb? baselineSeed = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(table);

        // ① 基线：未接管的库模板键（浮动层内部等）保持与主题和谐
        M3Theme.Apply(
            MaterialTheme.FromSeed(baselineSeed ?? BaselineSeed, SchemeVariant.TonalSpot),
            isDark: false,
            resources);

        // ② 权威表：我们消费的**全部**键一次性写入
        foreach (var (key, value) in table.Anchored)
            resources[key] = Brush(value);
        foreach (var (token, value) in table.Tokens)
            resources[token] = IsColorValueToken(token) ? value.ToMedia() : Brush(value);
    }

    /// <summary>
    /// 该令牌发布为 <c>Color</c> 还是 <c>SolidColorBrush</c>。
    /// </summary>
    /// <remarks>
    /// <c>DropShadowEffect.Color</c> 与 <c>GradientStop.Color</c> 吃的是 **Color**，
    /// 塞 Brush 给它们会立刻抛（WPF 不做 Brush→Color 转换）。故这一族必须按 Color 发布。
    /// </remarks>
    private static bool IsColorValueToken(string token) =>
        AppTokens.AllColorValueTokens.Contains(token, StringComparer.Ordinal);

    /// <summary>把一个值写成已冻结的画刷（唯一转换点）。</summary>
    public static SolidColorBrush Brush(Argb value)
    {
        var b = new SolidColorBrush(value.ToMedia());
        b.Freeze();
        return b;
    }

    /// <summary>
    /// 发布字体令牌（<see cref="AppTokens.FontUi"/> / <see cref="AppTokens.FontMono"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>字体令牌与颜色令牌同属令牌层，但介质不同</b>：值是 <see cref="System.Windows.Media.FontFamily"/>
    /// 而不是画刷。界面对它们的引用**必须是 <c>DynamicResource</c>**——
    /// <c>StaticResource</c> 在解析时就把值固化了，运行时换字体会**毫无反应**
    /// （这正是"字体系统"能否成立的物理前提，方案 §6.1）。
    /// </para>
    /// <para>
    /// 值是**回退链**（<c>导入族名, Microsoft YaHei UI, Segoe UI</c>）：
    /// 用户导入的拉丁字体不会让中文变方块——WPF 逐字形回退。
    /// </para>
    /// </remarks>
    public static void PublishFonts(ResourceDictionary resources, string uiFamily, string monoFamily)
    {
        ArgumentNullException.ThrowIfNull(resources);
        resources[AppTokens.FontUi] = Fonts.FontCatalog.BuildFontFamily(uiFamily);
        resources[AppTokens.FontMono] = Fonts.FontCatalog.BuildFontFamily(monoFamily);
    }

    /// <summary>
    /// 令牌表里"会被界面消费的键"总数（库键 + 应用令牌）——供诊断读数与文档核对。
    /// </summary>
    public static int PublishedKeyCount(TokenTable table) => table.Anchored.Count + table.Tokens.Count;

    /// <summary>日志分类（观测面：主题切换留痕）。</summary>
    private const string LogCategory = "app.theme";

    /// <summary>切主题后写一条留痕（失败/静默兜底都不许，故只记不吞）。</summary>
    public static void LogApplied(ThemeDefinition definition, TokenTable table)
    {
        LpLog.Info(
            $"已应用主题「{definition.Name}」（{definition.Source}，{definition.Palette.Count} 色，旋转 {table.SurfaceRotation:F1}°，{PublishedKeyCount(table)} 键）",
            LogCategory);
    }
}
