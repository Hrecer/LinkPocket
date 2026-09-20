using LinkPocket.Theming.Color;
using Material3.Core;

namespace LinkPocket.Theming.Themes;

/// <summary>
/// **主题目录**：出厂默认 1 套 + 内置预设 10 套（共 11 套可选）。
/// </summary>
/// <remarks>
/// <para>
/// 身份色 = 一个主题的"颜色本身"（「外观」面板的主题卡与再编辑都展示它们）。界面里出现的
/// 是这些原色的**色调板档位**（例如定稿紫 <c>#A18EB0</c> T61.7 → 容器档 <c>#F0DBFF</c> 一类）。
/// </para>
/// <para>
/// <b>角色分配见 <see cref="PaletteSolver.SolveFamilies"/></b>：彩度降序占槽（强调 / 支撑 / 强调容器 / 描边），
/// **明度最高的成员决定表面族与文字墨**——**界面上的色相全部来自用户给的颜色**，不再有 ±60° 发明色相。
/// </para>
/// </remarks>
public static class ThemeCatalog
{
    /// <summary>出厂默认主题的 id。</summary>
    public const string DefaultId = "factory-default";

    /// <summary>
    /// 出厂默认主题（保留紫色身份，5 个色全部占槽）。
    /// </summary>
    /// <remarks>
    /// <b>不再钉中性色相</b>（用户令 2026-09-20："我们给出的 4/5 个颜色是最高优先级"）：表面族色相 =
    /// 配色里最浅的 <c>#F2EEF5</c>（H287.7）→ 背景相对改造前偏紫 11°。代价已确认接受
    /// （旧判据"出厂默认表面族逐字节等于改造前"随之作废，见 `ThemeContrastTests.出厂默认主题_表面族随配色最浅色`）。
    /// </remarks>
    public static ThemeDefinition Default { get; } = new()
    {
        Id = DefaultId,
        Name = "默认（紫罗兰）",
        Source = ThemeSource.FactoryDefault,
        Palette = Palette(0x3F3448, 0x6E5A80, 0xA18EB0, 0xD5C7DE, 0xF2EEF5),
        ChromaCap = ChromaCap.Standard,
    };

    /// <summary>内置预设（10 套；顺序 = 方案附录 A 的顺序）。</summary>
    /// <remarks>
    /// <para>
    /// <b>每套的身份色是 2–3 个**（出厂默认为 5 个），这是标定收敛的**结果**：每套只放「强调族色 + 支撑族色」两个彩色成员，
    /// 缺位的槽（强调容器 / 描边）走 <see cref="PaletteSolver.SolveFamilies"/> 的**回退**（= 强调色相）。
    /// 表面族色相由 <see cref="ThemeDefinition.NeutralHueOverride"/> 钉住（= 各自标定过的表面底色相），
    /// 于是每套的派生结果与附录 A **逐项对齐**（强调族 H ≤1.8°、支撑族 H ≤1.3°、表面底 RGB ≤2/255）。
    /// </para>
    /// <para>
    /// <b>为什么预设钉住中性色相</b>：预设只有 1–2 个颜色，若让"最浅成员"决定背景，背景就会跟着那个彩色漂走
    /// （实测表面底偏 1–3/255 且层感跟着挪）。钉住它之后自由度从 3 降到 2，标定才收敛。
    /// **出厂默认不钉**（它有 5 个颜色，最浅的一个本来就是"背景色"），两者是同一套规则的两种输入，不是特例分支。
    /// </para>
    /// <para>
    /// ⚠️ 因此预设**不满足** <see cref="ThemeValidator"/> 的"4 或 5 色"规则（那是给**用户自选配色**定的门槛，
    /// 见 §7.3 的 4/5 色槽）。两者是两条路径，不要混用校验。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ThemeDefinition> Presets { get; } = new[]
    {
        Preset("ochre-rose", "赭石玫瑰", 52.4, 0x4E321B, 0x787164),
        Preset("dusk-rose", "暮色玫瑰", 27.8, 0x654C52, 0x6F727B),
        Preset("lotus-sage", "藕粉灰绿", 21.6, 0x2D3C2E, 0x93666D),
        Preset("caramel-rose", "焦糖玫瑰", 50.8, 0x4E3218),
        Preset("uji-matcha", "宇治抹茶", 200.5, 0x405658, 0x537A68),
        Preset("shine-muscat", "晴王青提饮", 131.0, 0x114123),
        Preset("blueberry-yogurt", "蓝莓优格杯", 253.5, 0x335384, 0x7F6B83),
        Preset("mint-soda", "薄荷气泡水", 190.2, 0x005D5A, 0x5E795E),
        Preset("sakura-panna", "樱花奶冻卷", 0.6, 0x6F4755, 0x6C746E),
        Preset("green-pear", "青梨冻冻", 133.9, 0x455833),
    };

    /// <summary>全部可选主题（默认 + 预设）。</summary>
    public static IReadOnlyList<ThemeDefinition> All { get; } =
        new[] { Default }.Concat(Presets).ToArray();

    /// <summary>按 id 查找（找不到返回 null——调用方决定是回退默认还是如实报错）。</summary>
    public static ThemeDefinition? Find(string id)
    {
        foreach (var t in All)
            if (string.Equals(t.Id, id, StringComparison.Ordinal))
                return t;
        return null;
    }

    /// <summary>按 id 查找，找不到**回退出厂默认**（启动恢复路径用；偏好文件损坏时不许崩）。</summary>
    public static ThemeDefinition FindOrDefault(string? id) =>
        id is null ? Default : Find(id) ?? Default;

    /// <summary>内置预设：身份色（强调族色 + 可选的支撑族色）+ 钉住的中性色相。</summary>
    private static ThemeDefinition Preset(string id, string name, double neutralHue, params int[] rgb) => new()
    {
        Id = id,
        Name = name,
        Source = ThemeSource.BuiltInPreset,
        Palette = Palette(rgb),
        ChromaCap = ChromaCap.Standard,
        NeutralHueOverride = neutralHue,
    };

    private static IReadOnlyList<Argb> Palette(params int[] rgb) =>
        rgb.Select(v => Argb.FromInt(unchecked((int)(0xFF000000u | (uint)v)))).ToArray();
}
