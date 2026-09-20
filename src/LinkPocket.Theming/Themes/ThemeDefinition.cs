using Material3.Core;

namespace LinkPocket.Theming.Themes;

/// <summary>主题的来源（决定「外观」面板里的分组与是否可删除）。</summary>
public enum ThemeSource
{
    /// <summary>出厂默认主题：唯一，永远排第一，不可删除。</summary>
    FactoryDefault,

    /// <summary>内置预设（TXT 提供的自带主题）：只读，不可删除。</summary>
    BuiltInPreset,

    /// <summary>用户自选配色（4 或 5 个颜色）：用户可保存/删除。</summary>
    UserDefined,
}

/// <summary>
/// 彩度上限档（自研「只钳上限、绝不放大」的强度档；见方案 §4.6）。
/// </summary>
public enum ChromaCap
{
    /// <summary>标准档：强调 ≤36、支撑 ≤24（尊重用户配色的彩度性格）。</summary>
    Standard = 0,

    /// <summary>收敛档：强调 ≤24、支撑 ≤16（配色很艳时用，观感更克制）。</summary>
    Restrained = 1,

    /// <summary>单色档：强调 ≤8、支撑 ≤8（近无彩主题）。</summary>
    Monochrome = 2,
}

/// <summary>
/// 配色应用方式（外观面板的「自动调整颜色」开关；用户令 2026-09-20 第二轮："**默认是打开的**"）。
/// </summary>
public enum PaletteMode
{
    /// <summary>
    /// **直配（开关关闭）**：尽量**原样**用用户给的每一个颜色 ——
    /// 页面底 / 卡面 / 悬停底 / 强调 / 支撑 / 描边 / 正文全部取自配色成员本身，
    /// **只在该角色确实缺位**（配色里没有够深的颜色撑白字、没有够浅的当容器…）时才按**本色**压/提明度。
    /// </summary>
    Exact = 0,

    /// <summary>
    /// **自动调色（开关打开，出厂缺省）**：按明度档位表把配色重排成一套协调的界面色
    /// （强调填充 T40 / 强调文字 T30 / 容器 T90+ / 描边 T65 / 悬停底按可读性反推 / 表面三层按档距推出…）。
    /// </summary>
    Auto = 1,
}

/// <summary>
/// 一个主题的定义 = **4 或 5 个颜色**（用户自选 / 预设 / HEX 输入）+ 彩度上限档
/// + 可选的中性色相钉值。
/// </summary>
/// <remarks>
/// <para>
/// <b>「主题就是颜色本身」</b>：颜色的 H/C（色相与彩度）原样保留，只有明度被"档位化"以换取对比度保证
/// （方案 §4.2）。主题卡展示 <see cref="Palette"/> 的原色，界面用其色调板档位。
/// </para>
/// <para>
/// <b><see cref="NeutralHueOverride"/> 是唯一的定稿钩子</b>：它同时决定表面旋转角
/// （= 该值 − <see cref="ReferenceNeutralHue"/>）、文字三档与描边的色相。出厂默认钉
/// <see cref="ReferenceNeutralHue"/> → 旋转角恰为 0 → 全部键逐字节等于今天（背景保留）。
/// 其它主题为 <c>null</c>，按配色里的中性池派生。用户若把默认主题的色槽改掉再应用，即为新主题
/// （不再钉住）——行为单一、无隐藏特例。
/// </para>
/// </remarks>
public sealed record ThemeDefinition
{
    /// <summary>今天背景（<c>SurfaceContainerLow</c>）的实测色相——表面旋转角的基准，也是出厂默认的钉值。</summary>
    public const double ReferenceNeutralHue = 298.7;

    /// <summary>主题的稳定标识（预设用 kebab-case 名；用户自选为生成 id）。</summary>
    public required string Id { get; init; }

    /// <summary>显示名（「外观」面板卡片标题）。</summary>
    public required string Name { get; init; }

    /// <summary>来源（分组与可删除性）。</summary>
    public required ThemeSource Source { get; init; }

    /// <summary>身份色：4 或 5 个（不透明；透明度不属主题，见方案 §7.3）。</summary>
    public required IReadOnlyList<Argb> Palette { get; init; }

    /// <summary>彩度上限档（缺省标准档）。</summary>
    public ChromaCap ChromaCap { get; init; } = ChromaCap.Standard;

    /// <summary>中性色相钉值（<c>null</c> = 按中性池派生；出厂默认 = <see cref="ReferenceNeutralHue"/>）。</summary>
    public double? NeutralHueOverride { get; init; }

    /// <summary>
    /// 配色应用方式（缺省 <see cref="PaletteMode.Auto"/> = 打开「自动调整颜色」，按明度档位表自动重排）。
    /// </summary>
    /// <remarks>
    /// 由外观面板的「自动调整颜色」开关决定（<c>ThemeService.Apply</c> 统一写入），
    /// **影响每一个令牌的取值**，因此也算主题定义的一部分（会随偏好落盘）。
    /// 缺省值取 <c>ThemeService.DefaultPaletteMode</c>（唯一事实源：用户令"我们默认是打开自动调整颜色的"）。
    /// </remarks>
    public PaletteMode PaletteMode { get; init; } = ThemeService.DefaultPaletteMode;

    /// <summary>方案允许的身份色数量（UI 只开放这两档）。</summary>
    public static readonly IReadOnlyList<int> AllowedPaletteSizes = new[] { 4, 5 };

    /// <summary>色数是否合法（4 或 5）。</summary>
    public bool HasValidPaletteSize => AllowedPaletteSizes.Contains(Palette.Count);

    /// <summary>
    /// 该主题的表面旋转角（度）：本主题中性色相相对今天背景色相（<see cref="ReferenceNeutralHue"/>）的位移。
    /// </summary>
    /// <remarks>
    /// 必须由**实际生效的中性色相**算出（不能只看 <see cref="NeutralHueOverride"/>）：
    /// 其它主题的中性色相是从配色中性池派生的，漏了这一步会让它们全部退回 0°（= 背景不变、只有强调色变，
    /// 与"换主题背景也变"的定稿口径相反）。
    /// </remarks>
    public double SurfaceRotation(double effectiveNeutralHue) =>
        Color.ColorMath.NormalizeHue(effectiveNeutralHue - ReferenceNeutralHue);
}
