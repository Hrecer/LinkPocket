namespace LinkPocket.Theming.Tokens;

/// <summary>
/// **应用令牌键名总表**（L2 层）：界面/样式只引这些键，**禁止任何颜色字面量**。
/// </summary>
/// <remarks>
/// <para>
/// 命名口径 <c>App.&lt;语义族&gt;.&lt;角色&gt;</c>。三处分工：
/// <list type="bullet">
/// <item><b>表面族</b> <c>App.Surface.*</c>：页面底 / 卡面 / 悬停底 / 胶囊容器 / 面板 / 弹窗底 / 浮层。
/// 这些键**与库键同值**（同一语义只有一个真值），方便样式在"库模板内部键"与"我们的语义键"之间取舍。</item>
/// <item><b>语义族</b> <c>App.Text.*</c> / <c>App.Accent.*</c> / <c>App.Support.*</c> / <c>App.Type.*</c>：
/// 由色相锚定族派生（同一动作在药丸 / 图标两种介质上取同一支主色）。</item>
/// <item><b>介质族</b> <c>App.State.*</c> / <c>App.Line.*</c> / <c>App.Overlay.*</c>：状态层、描边、遮罩、阴影。</item>
/// </list>
/// </para>
/// <para>
/// <b>警告色彻底退场</b>：本表**没有**任何"危险 / 警告"色的位置——破坏性动作与次操作共用同一套
/// 呈现（<c>Support.Container</c> / <c>Support.OnContainer</c> + 普通图标钮），不可逆性完全由
/// 「确认文案 + 二次确认」承担（方案 §5.6 / 决策 3）。同理，**全站不使用红色**：校验错误的描边
/// 用 <see cref="LineInvalid"/>（= 文字主色）。
/// </para>
/// </remarks>
public static class AppTokens
{
    // ── 表面族（与库键同值；默认主题下逐字节等于今天）─────────────────────────
    /// <summary>页面背景（最底层）= 库 <c>SurfaceContainerLow</c>。</summary>
    public const string SurfaceBase = "App.Surface.Base";

    /// <summary>卡片面（列表 / 卡片默认底）= 库 <c>SurfaceContainerHigh</c>。</summary>
    public const string SurfaceCard = "App.Surface.Card";

    /// <summary>悬停底色（行 / 项 hover）= 库 <c>SurfaceContainerHighest</c>。</summary>
    public const string SurfaceHover = "App.Surface.Hover";

    /// <summary>近白胶囊容器（返回圆钮 / 编辑页按钮组 / 工具页分段 / 命令栏）= 今天的 <c>TintCard</c>。</summary>
    public const string SurfaceTintCard = "App.Surface.TintCard";

    /// <summary>内容区近白底 = 今天的 <c>TintSurface</c>。</summary>
    public const string SurfaceTint = "App.Surface.Tint";

    /// <summary>侧区面板叠层 = 今天的 <c>TintPanel</c>。</summary>
    public const string SurfacePanel = "App.Surface.Panel";

    /// <summary>弹窗 / 遮罩面板底 = 今天的 <c>TintBg</c>。</summary>
    public const string SurfaceDialog = "App.Surface.Dialog";

    /// <summary>浮层底（菜单 / Tooltip / Popup）= 库 <c>Surface</c>。</summary>
    public const string SurfaceFloating = "App.Surface.Floating";

    // ── 文字族（三档，取自主题中性族 —— 不再是黑）───────────────────────────
    /// <summary>正文（T12，带主题色相的墨）= 库 <c>OnSurface</c>。</summary>
    public const string TextPrimary = "App.Text.Primary";

    /// <summary>次要文字（T30）= 库 <c>OnSurfaceVariant</c>。</summary>
    public const string TextSecondary = "App.Text.Secondary";

    /// <summary>弱文字（T40）= 库 <c>OnSurfaceMuted</c>。</summary>
    public const string TextMuted = "App.Text.Muted";

    /// <summary>强调填充上的字（白）。</summary>
    public const string TextOnAccent = "App.Text.OnAccent";

    // ── 强调族（主操作）──────────────────────────────────────────────────
    /// <summary>主药丸底（= <c>Primary</c>）。</summary>
    public const string AccentFill = "App.Accent.Fill";

    /// <summary>强调图标取色（与填充同档 = 同一件事同一色）。</summary>
    public const string AccentIcon = "App.Accent.Icon";

    /// <summary>强调文字（数字 / 链接 / 计数）。</summary>
    public const string AccentText = "App.Accent.Text";

    /// <summary>强调容器（选中指示器 / 落点高亮 / 徽标）。</summary>
    public const string AccentContainer = "App.Accent.Container";

    /// <summary>强调容器上的字。</summary>
    public const string AccentOnContainer = "App.Accent.OnContainer";

    // ── 次强调族（支撑族：计数 / 分段 / 次要药丸 / **删除类药丸同款**）─────────
    /// <summary>次强调容器（**破坏性动作与次操作共用这一套**）。</summary>
    public const string SupportContainer = "App.Support.Container";

    /// <summary>次强调容器上的字。</summary>
    public const string SupportOnContainer = "App.Support.OnContainer";

    /// <summary>次强调图标取色。</summary>
    public const string SupportIcon = "App.Support.Icon";

    // ── 类型语义色（文件夹 / 链接靠色相区分，替代琥珀 #FFB300）──────────────
    /// <summary>文件夹图标（= 支撑族图标档）。</summary>
    public const string TypeFolder = "App.Type.Folder";

    /// <summary>链接图标（= 强调族图标档）。</summary>
    public const string TypeLink = "App.Type.Link";

    // ── 状态层（叠层，而不是另找一个颜色）────────────────────────────────
    /// <summary>控件悬停叠层（OnSurface @8%；底色 tone ≤40 时叠白）。**已合成好的实色**，可直接当背景。</summary>
    public const string StateHover = "App.State.Hover";

    /// <summary>控件按压叠层（OnSurface @12%）。已合成好的实色。</summary>
    public const string StatePressed = "App.State.Pressed";

    /// <summary>标题栏/无底按钮的悬停底（中性墨 10% 实色）。</summary>
    public const string StateTitleBarHover = "App.State.TitleBar.Hover";

    /// <summary>标题栏/无底按钮的按压底（中性墨 20% 实色）。</summary>
    public const string StateTitleBarPressed = "App.State.TitleBar.Pressed";

    /// <summary>禁用态填充（OnSurface @12%，取代 <c>Opacity=0.4</c> 的"灰法"）。</summary>
    public const string StateDisabledFill = "App.State.Disabled.Fill";

    /// <summary>禁用态内容（OnSurface @38%）。</summary>
    public const string StateDisabledContent = "App.State.Disabled.Content";

    // ── 描边族 ──────────────────────────────────────────────────────────
    /// <summary>主描边（分隔线、卡片描边）= 库 <c>Outline</c>。</summary>
    public const string LineOutline = "App.Line.Outline";

    /// <summary>弱描边 = 库 <c>OutlineVariant</c>。</summary>
    public const string LineVariant = "App.Line.Variant";

    /// <summary>校验错误描边（= 文字主色，2px）——**全站去红**的落地形式。</summary>
    public const string LineInvalid = "App.Line.Invalid";

    // ── 遮罩 / 阴影 ──────────────────────────────────────────────────────
    /// <summary>弹窗遮罩（中性族 T20 @55%，替 <c>#AA000000</c>）。已合成实色，可直接当背景。</summary>
    public const string OverlayScrim = "App.Overlay.Scrim";

    /// <summary>忙碌遮罩（页面底 @60%，替 <c>#80FFFFFF</c>）。已合成实色。</summary>
    public const string OverlayBusy = "App.Overlay.Busy";

    /// <summary>阴影（中性族 T0 @32%，替 <c>#000000</c> 字面量）。已合成实色。</summary>
    public const string OverlayShadow = "App.Overlay.Shadow";

    // ── 颜色型令牌（值是 Color 而不是 Brush；供 DropShadowEffect / GradientStop 直接消费）──
    //
    // 为什么单独一族：WPF 的 `DropShadowEffect.Color` 与 `GradientStop.Color` 吃的是 **Color**，
    // 不是 Brush。若把 SolidColorBrush 塞给它们会立刻抛；而 WPF 也不会自动做 Brush→Color 转换
    // （只有 Brush→Brush、Color→Color 能跨资源引用）。故这两类**必须**用纯 Color 资源。
    // 它们与同名 Brush 令牌**共用一个事实源**（同一个 Argb），只是介质不同。

    /// <summary>阴影色（Color 形态）= <see cref="OverlayShadow"/> 的同源值。</summary>
    public const string ShadowColor = "App.Color.Shadow";

    /// <summary>入口卡面板的背景渐变起点（完全透明，Color 形态）。</summary>
    public const string GradientStart = "App.Color.Gradient.Start";

    /// <summary>入口卡面板的背景渐变终点（强调色 6% 淡染，Color 形态）。</summary>
    public const string GradientEnd = "App.Color.Gradient.End";

    // ── 字体（不是颜色，但同属令牌层；值在 UiTheme 里发布）──────────────────
    /// <summary>界面字体族令牌。</summary>
    public const string FontUi = "App.Font.Ui";

    /// <summary>等宽字体族令牌。</summary>
    public const string FontMono = "App.Font.Mono";

    // ── 过渡期兼容令牌（T2 专用；T3 视觉定稿时整族删除）──────────────────────
    //
    // 为什么需要：T2 的目标是"同值搬家、视觉零变化"。但**动作语义已经换过一轮**——
    // 旧 `AccentBtn`（#A18EB0 定稿紫）与新的 `App.Accent.Fill`（T40 档 #6A567C）不是同一个值，
    // 旧 `WarnBg`（奶油黄）在新体系里**根本没有对应语义**（决策 3：破坏性动作不设专门视觉）。
    // 若 T2 直接让老键绑到新令牌，视觉会在 T2 就变，两个阶段的责任就糊在一起、也无法回退。
    // 故 T2 先让老键绑到**锚点值**（逐字节等于今天），T3 再一次性改绑到新语义并删掉本族。
    //
    // 纪律：这一族**只允许**存在于 T2；`ThemeRulesTests.过渡期兼容令牌_T3后必须清零` 卡住它。

    /// <summary>[过渡期] 主操作按钮填充 = 旧 <c>AccentBtn</c>（#A18EB0）。T3 起改用 <see cref="AccentFill"/>。</summary>
    public const string LegacyAccentButton = "App.Legacy.AccentButton";

    /// <summary>[过渡期] 删除/警告底色 = 旧 <c>WarnBg</c>（奶油黄 #F5E9B8）。T3 起整族退场。</summary>
    public const string LegacyWarnBackground = "App.Legacy.WarnBackground";

    /// <summary>[过渡期] 正文色 = 旧 <c>OnSurface</c>（#1C1B1F，今天的库派生值）。T3 起改用 <see cref="TextPrimary"/>（带主题墨韵的 T12）。</summary>
    public const string LegacyTextPrimary = "App.Legacy.TextPrimary";

    /// <summary>[过渡期] 校验错误描边 = 旧红 <c>#E24B4A</c>。T3 去红后改用 <see cref="LineInvalid"/>。</summary>
    public const string LegacyInvalidLine = "App.Legacy.InvalidLine";

    /// <summary>过渡期兼容令牌（T3 必须清零）。</summary>
    public static IReadOnlyList<string> TransitionalTokens { get; } =
        new[] { LegacyAccentButton, LegacyWarnBackground, LegacyTextPrimary, LegacyInvalidLine };

    /// <summary>全部**颜色**令牌（字体令牌不含在内——它们不是 <c>SolidColorBrush</c>）。</summary>
    public static IReadOnlyList<string> AllColorTokens { get; } = new[]
    {
        SurfaceBase, SurfaceCard, SurfaceHover, SurfaceTintCard, SurfaceTint, SurfacePanel, SurfaceDialog, SurfaceFloating,
        TextPrimary, TextSecondary, TextMuted, TextOnAccent,
        AccentFill, AccentIcon, AccentText, AccentContainer, AccentOnContainer,
        SupportContainer, SupportOnContainer, SupportIcon,
        TypeFolder, TypeLink,
        StateHover, StatePressed, StateTitleBarHover, StateTitleBarPressed, StateDisabledFill, StateDisabledContent,
        LineOutline, LineVariant, LineInvalid,
        OverlayScrim, OverlayBusy, OverlayShadow,
        ShadowColor, GradientStart, GradientEnd,
        LegacyAccentButton, LegacyWarnBackground, LegacyTextPrimary, LegacyInvalidLine,
    };

    /// <summary>颜色型令牌（值是 <c>Color</c> 而非 <c>SolidColorBrush</c>；供 Effect / GradientStop 消费）。</summary>
    public static IReadOnlyList<string> AllColorValueTokens { get; } =
        new[] { ShadowColor, GradientStart, GradientEnd };

    /// <summary>全部字体令牌。</summary>
    public static IReadOnlyList<string> AllFontTokens { get; } = new[] { FontUi, FontMono };
}
