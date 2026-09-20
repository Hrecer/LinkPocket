using Material3.Core;

namespace LinkPocket.Theming.Tokens;

/// <summary>
/// **T2 过渡期兼容层**：把「语义令牌 → 今天实际发布值」的对应关系集中在一处。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：方案把 T2（令牌接管与字面量清零）定义为**视觉零变化**、T3 才是"唯一改视觉的阶段"。
/// 但语义令牌的值在 T2/T3 之间是**有意要变**的（例如强调填充从定稿紫 <c>#A18EB0</c> 换成达标档
/// <c>#6A567C</c>，文件夹类型色从琥珀换成暖玫褐）。若 T2 直接让界面绑到最终语义值，
/// 视觉就会在 T2 提前改变 —— 两个阶段的责任糊在一起，也无法单独回退。
/// </para>
/// <para>
/// <b>做法</b>：令牌表仍然由 <c>PaletteSolver</c> 完整派生（T3 的值一直在算，单测一直在验），
/// 但发布前经本层**覆盖**成今天值。于是：
/// 界面在 T2 就完成了"零字面量 + 全部走令牌"（这阶段真正要做的事），
/// 而 T3 只需**删掉本文件**并调整少量对应关系，视觉变化就一次性发生。
/// </para>
/// <para>
/// <b>纪律</b>：本层**只允许**存在于 T2；T3 必须整文件删除。
/// <c>ThemeRulesTests.过渡期兼容层_T3后必须删除</c> 卡住它（T3 时改为断言该文件不存在）。
/// </para>
/// </remarks>
public static class ThemeCompatibility
{
    /// <summary>
    /// 语义令牌 → 今天的值来自哪个锚定键（键名取自 <see cref="Color.SurfaceAnchors"/>）。
    /// 走这张表说明"该令牌在 T2 应当呈现为那个库键今天的值"。
    /// </summary>
    public static IReadOnlyDictionary<string, string> TokenToAnchorKey { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // 表面族 —— 直接就是今天的锚定键（语义与库键一一对应）
        [AppTokens.SurfaceBase] = "SurfaceContainerLow",
        [AppTokens.SurfaceCard] = "SurfaceContainerHigh",
        [AppTokens.SurfaceHover] = "SurfaceContainerHighest",
        [AppTokens.SurfaceTintCard] = "TintCard",
        [AppTokens.SurfaceTint] = "TintSurface",
        [AppTokens.SurfacePanel] = "TintPanel",
        [AppTokens.SurfaceDialog] = "TintBg",
        [AppTokens.SurfaceFloating] = "Surface",

        // 文字族 —— T2 仍用今天的库派生值（T3 换 T12/T30/T40 的主题墨韵）
        [AppTokens.TextPrimary] = "OnSurface",
        [AppTokens.TextSecondary] = "OnSurfaceVariant",
        [AppTokens.TextMuted] = "OnSurfaceMuted",
        [AppTokens.TextOnAccent] = "OnPrimary",

        // 强调族 —— T2 仍是旧的 AccentBtn 定稿紫（T3 换 T40 达标档，白字 3.00→6.49）。
        // ⚠️ 注意：这里**不能**指向库键 `Primary` —— 那是 M3 官方基线紫 #6750A4，与我们的定稿紫
        // #A18EB0 不是同一个颜色（"两套强调色并存"正是旧缺陷 D1）。故指向兼容令牌的常量值。
        [AppTokens.AccentFill] = "M3LegacyAccentButton",
        [AppTokens.AccentIcon] = "M3LegacyAccentButton",
        [AppTokens.AccentText] = "M3LegacyAccentButton",
        [AppTokens.AccentContainer] = "PrimaryContainer",
        [AppTokens.AccentOnContainer] = "OnPrimaryContainer",

        // 次强调族 —— T2 用旧的 Secondary 族；T3 才换成支撑族
        [AppTokens.SupportContainer] = "SecondaryContainer",
        [AppTokens.SupportOnContainer] = "OnSecondaryContainer",
        [AppTokens.SupportIcon] = "Secondary",

        // 类型色 —— T2 保留旧琥珀（T3 换暖玫褐；琥珀对卡面仅 1.61 ✗）
        [AppTokens.TypeFolder] = "M3FolderAmber",
        [AppTokens.TypeLink] = "Primary",

        // 描边族 —— T2 保持今天的库值（T3 换 T65/T80 档）
        [AppTokens.LineOutline] = "Outline",
        [AppTokens.LineVariant] = "OutlineVariant",
        [AppTokens.LineInvalid] = "M3LegacyInvalidLine",
    };

    /// <summary>
    /// 状态层 / 遮罩 / 阴影的今天值（这些在今天就是"常量 + 透明度"写法，故按常量锚定）。
    /// </summary>
    public static IReadOnlyDictionary<string, Argb> ConstantOverrides { get; } = new Dictionary<string, Argb>(StringComparer.Ordinal)
    {
        // 标题栏/无底按钮：中性墨 10% / 20%（今天写死 #1A000000 / #33000000）
        [AppTokens.StateTitleBarHover] = Argb.FromArgb(0x1A, 0x00, 0x00, 0x00),
        [AppTokens.StateTitleBarPressed] = Argb.FromArgb(0x33, 0x00, 0x00, 0x00),

        // 表头悬停：今天写死的外来色板 #1F6750A4（M3 基线紫 12%）
        [AppTokens.StateHover] = Argb.FromArgb(0x1F, 0x67, 0x50, 0xA4),

        // 控件按压 / 禁用态：今天分别是 OnSurface 12% / 38% 叠层
        [AppTokens.StatePressed] = Argb.FromArgb(0x1F, 0x1C, 0x1B, 0x1E),
        [AppTokens.StateDisabledFill] = Argb.FromArgb(0x61, 0x1C, 0x1B, 0x1E),
        [AppTokens.StateDisabledContent] = Argb.FromArgb(0x61, 0x1C, 0x1B, 0x1E),

        // 遮罩 / 阴影：今天的写死值
        [AppTokens.OverlayScrim] = Argb.FromArgb(0xAA, 0x00, 0x00, 0x00),
        [AppTokens.OverlayBusy] = Argb.FromArgb(0x80, 0xFF, 0xFF, 0xFF),
        [AppTokens.OverlayShadow] = Argb.FromArgb(0xFF, 0x00, 0x00, 0x00),
        [AppTokens.ShadowColor] = Argb.FromArgb(0xFF, 0x00, 0x00, 0x00),

        // 入口卡面板背景渐变：今天写死的是**外来色板** M3 基线紫（0% → 6%）
        [AppTokens.GradientStart] = Argb.FromArgb(0x00, 0x67, 0x50, 0xA4),
        [AppTokens.GradientEnd] = Argb.FromArgb(0x0F, 0x67, 0x50, 0xA4),

        // 过渡期兼容令牌（T3 连带本文件一起删除）
        [AppTokens.LegacyAccentButton] = Argb.FromArgb(0xFF, 0xA1, 0x8E, 0xB0),
        [AppTokens.LegacyWarnBackground] = Argb.FromArgb(0xFF, 0xF5, 0xE9, 0xB8),
        [AppTokens.LegacyTextPrimary] = Argb.FromArgb(0xFF, 0x1C, 0x1B, 0x1E),
        [AppTokens.LegacyInvalidLine] = Argb.FromArgb(0xFF, 0xE2, 0x4B, 0x4A),
    };

    /// <summary>今天的文件夹类型色（琥珀，实测对卡面仅 1.61 —— T3 换暖玫褐）。</summary>
    public static readonly Argb FolderAmber = Argb.FromArgb(0xFF, 0xFF, 0xB3, 0x00);

    /// <summary>
    /// 把一张"最终语义"令牌表**覆盖**成今天值（T2 专用）。
    /// </summary>
    /// <param name="table">求解器产出的最终表（含被语义覆写过的库键）。</param>
    /// <param name="rawAnchored">**未覆写**的锚定键值（键 → 按主题旋转后的今天值）。</param>
    /// <returns>覆盖后的新表（不修改入参）。</returns>
    /// <remarks>
    /// 覆盖两块：
    /// <list type="number">
    /// <item><b>库键</b>回到 <paramref name="rawAnchored"/>——T2 视觉零变化意味着连库角色键也不许变
    /// （语义覆写掉的那 12 个键在 T3 才生效）。</item>
    /// <item><b>应用令牌</b>按 <see cref="TokenToAnchorKey"/> / <see cref="ConstantOverrides"/> 取今天值。</item>
    /// </list>
    /// </remarks>
    public static Color.TokenTable Overlay(Color.TokenTable table, IReadOnlyDictionary<string, Argb> rawAnchored)
    {
        var anchored = new Dictionary<string, Argb>(rawAnchored, StringComparer.Ordinal);
        var tokens = new Dictionary<string, Argb>(table.Tokens, StringComparer.Ordinal);

        foreach (var (token, anchorKey) in TokenToAnchorKey)
        {
            if (anchorKey == "M3FolderAmber") { tokens[token] = FolderAmber; continue; }
            if (anchorKey == "M3LegacyAccentButton") { tokens[token] = ConstantOverrides[AppTokens.LegacyAccentButton]; continue; }
            if (anchorKey == "M3LegacyInvalidLine") { tokens[token] = ConstantOverrides[AppTokens.LegacyInvalidLine]; continue; }
            if (anchored.TryGetValue(anchorKey, out var v)) tokens[token] = v;
        }
        foreach (var (token, value) in ConstantOverrides)
            tokens[token] = value;

        return new Color.TokenTable
        {
            Anchored = anchored,
            Tokens = tokens,
            SurfaceRotation = table.SurfaceRotation,
            Families = table.Families,
        };
    }
}
