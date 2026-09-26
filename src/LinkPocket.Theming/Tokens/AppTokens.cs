namespace LinkPocket.Theming.Tokens;

/// <summary>
/// **应用令牌键名总表**（L2 层）：界面/样式只引这些键，**禁止任何颜色字面量**。
/// </summary>
/// <remarks>
/// <para>
/// 命名口径 <c>App.&lt;语义族&gt;.&lt;角色&gt;</c>。三处分工：
/// <list type="bullet">
/// <item><b>表面族</b> <c>App.Surface.*</c>：页面底 / 卡面 / 悬停底 / 胶囊容器 / 面板 / 弹窗底 / 浮层。
/// 这些键**与库键同值**（同一语义只有一个真值），方便样式在"库模板内部键"与"本应用的语义键"之间取舍。</item>
/// <item><b>语义族</b> <c>App.Text.*</c> / <c>App.Accent.*</c> / <c>App.Support.*</c> / <c>App.Type.*</c>：
/// 由色相锚定族派生（同一动作在药丸 / 图标两种介质上取同一支主色）。</item>
/// <item><b>介质族</b> <c>App.State.*</c> / <c>App.Line.*</c> / <c>App.Overlay.*</c>：状态层、描边、遮罩、阴影。</item>
/// </list>
/// </para>
/// <para>
/// <b>警告色彻底退场</b>：本表**没有**任何"危险 / 警告"色的位置——破坏性动作与次操作共用同一套
/// 呈现（<c>Support.Container</c> / <c>Text.OnContainer</c> + 普通图标钮），不可逆性完全由
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

    /// <summary>
    /// **页面底**这一层的悬停底（画在页面底上的项 hover / 以及"安静容器"底）= 库 <c>SurfaceContainerHighest</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 悬停令牌**按承载面分键**（本键 / <see cref="SurfaceCardHover"/> / <see cref="SurfaceBandHover"/>）：
    /// 每一支都是"把**它所在的那一层**压深一档"。一支通用悬停服务三层时，画在卡面 / 浅带上的悬停
    /// 会换成**另一支色相、另一个明度档**的颜色 —— 屏幕上不是"这一条加深了"，而是"跳了一块别的颜色"
    /// （实测：表头带换成弱撞色档后，表头悬停仍是页面底那一支的压深档 = 看着像旧灰色没变）。
    /// </remarks>
    public const string SurfaceHover = "App.Surface.Hover";

    /// <summary>**卡面**这一层的悬停底（主栏数据行 / 下拉项 / 菜单项 / 卡片行 hover）= 卡面压深一档。</summary>
    public const string SurfaceCardHover = "App.Surface.CardHover";

    /// <summary>
    /// **浅带层**这一层的悬停底（表头药丸 / 目录树行 / 侧区面板内的项）= <see cref="SurfacePanel"/> 压深一档。
    /// </summary>
    public const string SurfaceBandHover = "App.Surface.BandHover";

    /// <summary>
    /// **列表行 / 树行的选中底与拖拽落点高亮**（主栏、树、下拉项、搜索结果行）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="AccentContainer"/> **分开的两个令牌**：两者判据锚在不同的真实承载面上——
    /// 选中行画在**卡面**上（卡面比页面底还亮，浅色容器会被读成"没选中"），
    /// 而指示器 / 徽标 / 分段 / chip 画在**页面底或悬停底**上（深色容器在那里读成"灰块"）。
    /// 同一个令牌服务两种承载面 ⇒ 调一处必破另一处。
    /// 档位 = **够得开阈值的最浅档**（对卡面 / 卡面悬停 / 页面底三条阈值一起量）：
    /// 取"允许的最深档"会一路下探到 T76，同一彩度在更低明度上就是**灰**（实测被指"选中行过深偏灰"）。
    /// <b>色相 = 表面族色相</b>（不是容器槽成员的色相）：选中行铺在页面 / 卡面上，只有与背景同色调才读作
    /// "这一行被选中"（实测青提：容器槽成员是奶黄 ⇒ 选中底是黄的，与绿背景差 35°，被指"选中态是黄色"）。
    /// </remarks>
    public const string SurfaceSelected = "App.Surface.Selected";

    /// <summary>
    /// **次按钮底**（TonalButton / SoftPillButton / 计数药丸的淡色底）——表面族色相 + 表面族彩度，
    /// 取"对卡面 ≥ <c>PaletteSolver.ControlSurfaceMinContrastOnCard</c> 的**最浅达标档**"。
    /// </summary>
    /// <remarks>
    /// 为什么与 <see cref="SupportContainer"/> 分开：支撑容器 = **配色成员本色**，深浅随用户配色在
    /// 对卡面 1.02–1.97 之间摆动 —— 青提那套本色近白（按钮与命令栏胶囊**重合**）、暮色玫瑰那套本色 T73
    /// （一整块重色）。而"次按钮 / 计数药丸"这类面必须**每套主题都稳定可见**且不抢戏，故锚在表面族上：
    /// 与页面底 / 卡面**同色调**（读作"这一层的浅色控件面"），且对卡面 / 面板 / 选中行底三种承载面都分得开。
    /// 真需要"本色"的地方（chip / 徽标 / 分段指示器 / 图标容器）仍用 <see cref="SupportContainer"/>。
    /// </remarks>
    public const string SurfaceControl = "App.Surface.Control";

    /// <summary>近白胶囊容器（返回圆钮 / 编辑页按钮组 / 工具页分段 / 命令栏）= 今天的 <c>TintCard</c>。</summary>
    public const string SurfaceTintCard = "App.Surface.TintCard";

    /// <summary>内容区近白底 = 今天的 <c>TintSurface</c>。</summary>
    public const string SurfaceTint = "App.Surface.Tint";

    /// <summary>
    /// 侧区面板 / 状态栏 / 徽标底：**与表头带同一条浅带**（页面底提亮 <c>SurfaceBandLift</c> 档）——
    /// 两枚键是**同一层的两个角色名**（同 `TintCard`/`Tint`/`Floating` 绑卡面），值必然相同。
    /// </summary>
    public const string SurfacePanel = "App.Surface.Panel";

    /// <summary>
    /// **表头带**：表格顶部的浅色条（浏览页 / 回收站 / 搜索 / 智能列表 / 去重明细共用）。
    /// </summary>
    /// <remarks>
    /// = 页面底**本色档**（<c>SurfaceBandLift = 0</c>）+ 色相**贴到强调槽那一支成员**（仅当它与表面槽相邻
    /// ≤ <c>SurfaceBandHueNeighbourMax</c>）⇒ 与页面底同族弱撞色；**与 <see cref="SurfacePanel"/>
    /// 是同一条带的两个角色名**（值相同、只改档距一处两处一起动）。
    /// 表头悬停画在它上面：药丸用 <see cref="SurfaceBandHover"/>（**本条带自己**压深一档，不是页面底那一支）。
    /// </remarks>
    public const string SurfaceHeaderBand = "App.Surface.HeaderBand";

    /// <summary>弹窗 / 遮罩面板底 = 今天的 <c>TintBg</c>。</summary>
    public const string SurfaceDialog = "App.Surface.Dialog";

    /// <summary>浮层底（菜单 / Tooltip / Popup）= 库 <c>Surface</c>。</summary>
    public const string SurfaceFloating = "App.Surface.Floating";

    // ── 文字族（取自主题中性族 / 强调族 —— 不再是黑）─────────────────────────
    //
    // **界面上的每一个 Foreground 都必须是这一族**（架构护栏 `ThemeRulesTests.界面层_文字色只能引 App.Text`）：
    // 库角色键（OnSurface / Primary / …）**也能**当文字色，但那等于绕过语义层 ——
    // 主题换档时"哪一处字该跟着谁变"就散落在各页 XAML 里，再也盘不清。
    // 本族是**封闭集合**：新增文字色必须先进这里（连同派生式与对比度断言），再落界面。

    /// <summary>正文（T12，带主题色相的墨）= 库 <c>OnSurface</c>。</summary>
    public const string TextPrimary = "App.Text.Primary";

    /// <summary>次要文字（T30）= 库 <c>OnSurfaceVariant</c>。</summary>
    public const string TextSecondary = "App.Text.Secondary";

    /// <summary>弱文字（T40）= 库 <c>OnSurfaceMuted</c>。</summary>
    public const string TextMuted = "App.Text.Muted";

    /// <summary>强调填充上的字（白）。</summary>
    public const string TextOnAccent = "App.Text.OnAccent";

    /// <summary>
    /// 容器（强调 / 次强调）上的字（T15）。
    /// </summary>
    /// <remarks>
    /// <b>为什么强调容器与次强调容器共用一个令牌</b>：两族的容器字**本来就是同一档**（T15）——
    /// 出厂默认主题实测 <c>#2F1E40</c>（强调）/ <c>#352023</c>（支撑），对各自容器的对比度都在 11.7 以上，
    /// 观感都是"坐在浅色容器上的深墨"。拆成两个令牌只会让界面在每个点上随机挑一个 ——
    /// 同一个语义（"浅色容器上的字"）只该有一个真值。
    /// </remarks>
    public const string TextOnContainer = "App.Text.OnContainer";

    // ── 强调族（主操作）──────────────────────────────────────────────────
    /// <summary>主药丸底（= <c>Primary</c>）。</summary>
    public const string AccentFill = "App.Accent.Fill";

    /// <summary>强调图标取色（与填充同档 = 同一件事同一色）。</summary>
    public const string AccentIcon = "App.Accent.Icon";

    /// <summary>强调文字（数字 / 链接 / 计数）。</summary>
    public const string AccentText = "App.Accent.Text";

    /// <summary>
    /// 强调容器：**导航 / 分段 / 分段指示器 / 面包屑当前段 / 徽标 / chip / 空态徽章**的浅色底
    /// （承载面是页面底或悬停底，判据 = 对页面底 ≥1.08、对悬停底 ≥1.06，见
    /// <c>PaletteSolver.LiftContainerUntilVisible</c>）。
    /// </summary>
    /// <remarks>列表行 / 树行的选中底**不用它**（那类面画在卡面上，需要更深的对照）——见 <see cref="SurfaceSelected"/>。</remarks>
    public const string AccentContainer = "App.Accent.Container";


    // ── 次强调族（支撑族：计数 / 分段 / 次要药丸 / **删除类药丸同款**）─────────
    /// <summary>次强调容器（**破坏性动作与次操作共用这一套**）。</summary>
    public const string SupportContainer = "App.Support.Container";


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

    /// <summary>全透明（Color 形态）—— 取色盘 SV 方块的饱和/明度叠加层端点用。</summary>
    public const string SvTransparent = "App.Color.SvTransparent";

    // ── 字体（不是颜色，但同属令牌层；值在 UiTheme 里发布）──────────────────
    /// <summary>界面字体族令牌。</summary>
    public const string FontUi = "App.Font.Ui";

    /// <summary>等宽字体族令牌。</summary>
    public const string FontMono = "App.Font.Mono";

    /// <summary>全部**颜色**令牌（字体令牌不含在内——它们不是 <c>SolidColorBrush</c>）。</summary>
    public static IReadOnlyList<string> AllColorTokens { get; } = new[]
    {
        SurfaceBase, SurfaceCard, SurfaceHover, SurfaceCardHover, SurfaceBandHover, SurfaceSelected, SurfaceControl, SurfaceTintCard, SurfaceTint, SurfacePanel, SurfaceHeaderBand, SurfaceDialog, SurfaceFloating,
        TextPrimary, TextSecondary, TextMuted, TextOnAccent, TextOnContainer,
        AccentFill, AccentIcon, AccentText, AccentContainer,
        SupportContainer, SupportIcon,
        TypeFolder, TypeLink,
        StateHover, StatePressed, StateTitleBarHover, StateTitleBarPressed, StateDisabledFill, StateDisabledContent,
        LineOutline, LineVariant, LineInvalid,
        OverlayScrim, OverlayBusy, OverlayShadow,
        ShadowColor, GradientStart, GradientEnd,
    };

    /// <summary>颜色型令牌（值是 <c>Color</c> 而非 <c>SolidColorBrush</c>；供 Effect / GradientStop 消费）。</summary>
    public static IReadOnlyList<string> AllColorValueTokens { get; } =
        new[] { ShadowColor, GradientStart, GradientEnd, SvTransparent };

    /// <summary>全部字体令牌。</summary>
    public static IReadOnlyList<string> AllFontTokens { get; } = new[] { FontUi, FontMono };
}
