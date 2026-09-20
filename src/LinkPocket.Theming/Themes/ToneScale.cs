namespace LinkPocket.Theming.Themes;

/// <summary>
/// **明度档位表**（方案 §4.5，自研；不套用库的标准档位）。
/// </summary>
/// <remarks>
/// <para>
/// <b>档位表一旦定稿就是我们的规范，库升级不改它</b>。所有档位都经 WCAG 实测（附录 A 的 11 套主题）
/// 保证可读性——这是"HCT 的 T 直接映射到相对亮度、因而对比度只由两个 tone 决定、与色相无关"
/// 这条性质的工程红利：用户随便选 4/5 个颜色，我们都能保证可读。
/// </para>
/// <para>
/// 与库标准映射的差别：文字弱档从 T45 收到 <b>T40</b>（我们的表面比库默认更深，T45 对悬停底只有 3.91 ✗）；
/// 强调文字设 T30（保证对卡面 ≥8）。
/// </para>
/// </remarks>
public static class ToneScale
{
    /// <summary>文字：主（正文）——对卡面 ≈14.7。</summary>
    public const double TextPrimary = 12.0;

    /// <summary>文字：次（信息行、次要说明）——对卡面 ≈8.4。</summary>
    public const double TextSecondary = 30.0;

    /// <summary>文字：弱（提示、占位）——对卡面 ≈5.8、对悬停底 ≈4.7（今天 4.04/3.29 ✗ → 修到达标）。</summary>
    public const double TextMuted = 40.0;

    /// <summary>强调填充 / 强调图标：白字 ≈6.5、对卡面 ≈5.8。</summary>
    public const double AccentFill = 40.0;

    /// <summary>强调文字（数字 / 链接 / 计数）：对卡面 ≈8.4。</summary>
    public const double AccentText = 30.0;

    /// <summary>强调容器（选中指示器 / 落点高亮 / 徽标）：对容器字 ≈11.8。</summary>
    public const double AccentContainer = 90.0;

    /// <summary>强调容器上的字。</summary>
    public const double AccentOnContainer = 15.0;

    /// <summary>次强调容器（计数药丸 / 分段选中 / 删除类药丸）。</summary>
    public const double SupportContainer = 90.0;

    /// <summary>次强调容器上的字。</summary>
    public const double SupportOnContainer = 15.0;

    /// <summary>描边：主（分隔线、卡片描边）。</summary>
    public const double LineOutline = 65.0;

    /// <summary>描边：弱。</summary>
    public const double LineVariant = 80.0;

    /// <summary>状态层：悬停（OnSurface @8%）。</summary>
    public const double StateHoverOpacity = 0.08;

    /// <summary>状态层：按压（OnSurface @12%）。</summary>
    public const double StatePressedOpacity = 0.12;

    /// <summary>禁用态：填充（OnSurface @12%）。</summary>
    public const double DisabledFillOpacity = 0.12;

    /// <summary>禁用态：内容（OnSurface @38%）。</summary>
    public const double DisabledContentOpacity = 0.38;

    /// <summary>遮罩（弹窗背后）：中性族 T20 @55%。</summary>
    public const double ScrimTone = 20.0;

    /// <summary>遮罩不透明度。</summary>
    public const double ScrimOpacity = 0.55;

    /// <summary>忙碌遮罩：页面底 @60%。</summary>
    public const double BusyOverlayOpacity = 0.60;

    /// <summary>阴影：中性族 T0 @32%。</summary>
    public const double ShadowTone = 0.0;

    /// <summary>阴影不透明度。</summary>
    public const double ShadowOpacity = 0.32;

    /// <summary>浮层（菜单 / Tooltip / Popup）叠加在库 <c>SurfaceElevation*</c> 上的近似不动点。</summary>
    public const double FloatingSurfaceTone = 98.1;

    /// <summary>所有档位（自检用：确保没有越界值）。</summary>
    public static IReadOnlyList<double> All { get; } = new[]
    {
        TextPrimary, TextSecondary, TextMuted,
        AccentFill, AccentText, AccentContainer, AccentOnContainer,
        SupportContainer, SupportOnContainer,
        LineOutline, LineVariant,
        ScrimTone, ShadowTone, FloatingSurfaceTone,
    };
}
