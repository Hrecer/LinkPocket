using Material3.Core;

namespace LinkPocket.Theming.Color;

/// <summary>
/// **全量锚定表**：今天（重构前）实际发布的每一个颜色键及其不透明分量。
/// </summary>
/// <remarks>
/// <para>
/// <b>锚点 = 今天的设计值，不是推导值</b>。取值来源（可复算）：
/// <c>M3Theme.Apply(MaterialTheme.FromSeed(#6750A4, TonalSpot), isDark:false)</c>
/// 叠加 <c>App.xaml.cs</c> 的三个表面补丁（<c>SurfaceContainerLow/High/Highest</c>）。
/// 实测 = <b>49 个库键</b>（<c>M3Theme.Roles</c>：43 个 <c>ColorScheme</c> 角色 + 6 个 <c>SurfaceElevation0..5</c>），
/// 加上 <see cref="ApplicationKeys"/> 的 4 个应用键，共 <b>53 键</b>。
/// </para>
/// <para>
/// <b>逐键锚定</b>：换主题时每个键按同一色相旋转角旋转（明度与彩度保持该键自己的锚点）
/// → <b>层感（谁比谁深、谁比谁艳）在任何主题下恒定</b>，而颜色整体随主题变化（含大面积背景）。
/// 默认主题旋转角恰为 0 → <b>全部键逐字节等于今天</b>。
/// </para>
/// <para>
/// <b>例外清单（不许悄悄扩大）</b>：
/// ① <see cref="NonRotatingKeys"/> = 12 个语义键（<c>Error*</c> / <c>Warning*</c> / <c>Success*</c> 三族）
/// —— 全站零消费、不参与旋转，保持库基线；
/// ② <see cref="Scrim"/> / <see cref="Shadow"/> 无彩度，按常量（仅 α 保留）；
/// ③ <c>SurfaceElevation0..5</c> 的**键名**保留（库模板按名取值），色值同样逐键锚定并旋转。
/// </para>
/// <para>
/// <b>α 必须随锚点携带</b>：HCT 不含 α，若在派生时用 <c>Hct.ToColor()</c> 的 α，<c>Scrim</c>（α=A8）
/// 会被拉成 FF（实测踩中）。故每个锚点显式记录 α。
/// </para>
/// </remarks>
public static class SurfaceAnchors
{
    /// <summary>一个锚点：键名 + 今天的值 + 是否参与色相旋转。</summary>
    /// <param name="Key">资源键名（库角色名或应用键名）。</param>
    /// <param name="Today">今天实际发布的值（含 α）。</param>
    /// <param name="Rotates">true = 换主题时按表面旋转角旋转色相；false = 保持库基线常量。</param>
    public readonly record struct Anchor(string Key, Argb Today, bool Rotates);

    // ── 语义族（全站零消费；不旋转，保持库基线）──────────────────────────────
    private static readonly string[] SemanticKeys =
    {
        "Error", "OnError", "ErrorContainer", "OnErrorContainer",
        "Warning", "OnWarning", "WarningContainer", "OnWarningContainer",
        "Success", "OnSuccess", "SuccessContainer", "OnSuccessContainer",
    };

    /// <summary>无彩度常量键（仅保留 α 锚点，色值恒为黑）。</summary>
    public static readonly IReadOnlyList<string> ConstantKeys = new[] { "Scrim", "Shadow" };

    /// <summary>不参与旋转的键（例外清单 ①）。</summary>
    public static IReadOnlyList<string> NonRotatingKeys => SemanticKeys;

    /// <summary>
    /// 今天的 53 个键（49 库键 + 4 应用键），按 <c>M3Theme.Roles</c> 的声明顺序，
    /// 应用键追加在末尾。值是实测导出值（见类注释的复算来源）。
    /// </summary>
    public static IReadOnlyList<Anchor> All { get; } = Build();

    /// <summary>应用键（我们自定义、不进库 <c>M3Theme.Roles</c> 的四个表面键）。</summary>
    public static readonly IReadOnlyList<string> ApplicationKeys =
        new[] { "TintCard", "TintSurface", "TintPanel", "TintBg" };

    /// <summary>页面底 / 卡面 / 悬停底 / 内容区近白底 / 弹窗底 / 侧区面板 —— 层感排序的关键行。</summary>
    public static readonly IReadOnlyList<string> SurfaceStackKeys =
        new[] { "SurfaceContainerLow", "SurfaceContainerHigh", "SurfaceContainerHighest", "TintCard", "TintSurface", "TintPanel", "TintBg" };

    /// <summary>按今天的值做键名查找（找不到返回 null）。</summary>
    public static Anchor? Find(string key)
    {
        foreach (var a in All)
            if (string.Equals(a.Key, key, StringComparison.Ordinal))
                return a;
        return null;
    }

    private static Anchor[] Build()
    {
        // 顺序与值 = 实测导出（tools probe / 附录 D）。Rotates 由两个例外清单推导，避免两处手抄。
        var rows = new (string Key, uint Today)[]
        {
            ("Primary", 0xFF6750A4), ("OnPrimary", 0xFFFFFFFF),
            ("PrimaryContainer", 0xFFE9DDFF), ("OnPrimaryContainer", 0xFF22005D),
            ("InversePrimary", 0xFFCFBCFF),
            ("Secondary", 0xFF625B71), ("OnSecondary", 0xFFFFFFFF),
            ("SecondaryContainer", 0xFFE8DEF8), ("OnSecondaryContainer", 0xFF1E192B),
            ("Tertiary", 0xFF7E5260), ("OnTertiary", 0xFFFFFFFF),
            ("TertiaryContainer", 0xFFFFD9E3), ("OnTertiaryContainer", 0xFF31101D),
            ("Error", 0xFFBA1A1A), ("OnError", 0xFFFFFFFF),
            ("ErrorContainer", 0xFFFFDAD6), ("OnErrorContainer", 0xFF410002),
            ("Success", 0xFF256C27), ("OnSuccess", 0xFFFFFFFF),
            ("SuccessContainer", 0xFFA9F59F), ("OnSuccessContainer", 0xFF002203),
            ("Warning", 0xFF795900), ("OnWarning", 0xFFFFFFFF),
            ("WarningContainer", 0xFFFFDEA0), ("OnWarningContainer", 0xFF261900),
            ("Surface", 0xFFFDF8FD), ("SurfaceDim", 0xFFDDD8DD), ("SurfaceBright", 0xFFFDF8FD),
            ("SurfaceContainerLowest", 0xFFFFFFFF),
            // ↓ 补丁 #1（App.xaml.cs）：页面底
            ("SurfaceContainerLow", 0xFFEAE4ED),
            ("SurfaceContainer", 0xFFF2ECF1),
            // ↓ 补丁 #2：卡面
            ("SurfaceContainerHigh", 0xFFF6F1F8),
            // ↓ 补丁 #3：悬停底
            ("SurfaceContainerHighest", 0xFFE3D9EB),
            ("InverseSurface", 0xFF313033), ("InverseOnSurface", 0xFFF4EFF4),
            ("OnSurface", 0xFF1C1B1E), ("OnSurfaceVariant", 0xFF49454E), ("OnSurfaceMuted", 0xFF7A757F),
            ("Outline", 0xFF7A757F), ("OutlineVariant", 0xFFCAC4CF),
            ("SurfaceTint", 0xFF6750A4),
            ("Scrim", 0xA8000000), ("Shadow", 0xFF000000),
            ("SurfaceElevation0", 0xFFFDF8FD), ("SurfaceElevation1", 0xFFF6F0F9),
            ("SurfaceElevation2", 0xFFF1EBF6), ("SurfaceElevation3", 0xFFECE6F3),
            ("SurfaceElevation4", 0xFFEBE4F2), ("SurfaceElevation5", 0xFFE8E0F1),
            // 应用键（UIKit.xaml 的四个自定画刷）
            ("TintCard", 0xFFF5F1FB), ("TintSurface", 0xFFFAF8FD),
            ("TintPanel", 0xFFE4DCF3), ("TintBg", 0xFFEAE4ED),
        };

        var list = new List<Anchor>(rows.Length);
        foreach (var (key, today) in rows)
        {
            var rotates = !SemanticKeys.Contains(key) && !ConstantKeys.Contains(key);
            list.Add(new Anchor(key, Argb.FromInt(unchecked((int)today)), rotates));
        }
        return list.ToArray();
    }
}
