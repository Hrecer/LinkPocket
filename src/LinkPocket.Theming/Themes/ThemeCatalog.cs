using LinkPocket.Theming.Color;
using Material3.Core;

namespace LinkPocket.Theming.Themes;

/// <summary>
/// **主题目录**：出厂默认 1 套 + 内置预设 10 套（共 11 套可选）。
/// </summary>
/// <remarks>
/// <para>
/// 身份色 = 一个主题的"颜色本身"（「外观」面板的主题卡与再编辑都展示它们）。界面里出现的
/// 是这些原色的**色调板档位**（例如默认紫 <c>#A18EB0</c> T61.7 → 容器档 <c>#F0DBFF</c> 一类）。
/// </para>
/// <para>
/// <b>角色分配见 <see cref="PaletteSolver.SolveFamilies"/></b>：彩度降序占槽（强调 / 支撑 / 强调容器 / 描边），
/// **明度最高的成员决定表面族与文字墨**——**界面上的色相全部来自用户给的颜色**，不再有 ±60° 发明色相。
/// </para>
/// </remarks>
public static class ThemeCatalog
{
    /// <summary>出厂默认主题的 id（沿用不换：老偏好的 "factory-default" 直接指向新配色）。</summary>
    public const string DefaultId = "factory-default";

    /// <summary>
    /// 出厂默认主题 = **薄荷气泡水**（2026-10-14 起；旧出厂默认紫罗兰降级为普通预设「紫罗兰」，配色原样保留）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>表面族色相 = 配色里最浅的那个成员</b>（不钉中性色相，配色成员优先级最高）；
    /// <b>表面族彩度 = 配色里"浅调成员"的量级</b>（见 `ThemeFamilies.SurfaceChroma`）。
    /// </para>
    /// <para>
    /// <b>为什么是"深 → 浅"的 4 枚梯度</b>（深青 / 气泡青 `#8ED6DE` / 薄荷 `#BCF1E0` / 米白 `#EFFFE0`）：
    /// ① 薄荷气泡水的设计档原色全为浅色——全浅配色没有"墨"的来源（走"自动加深派生"）、也没有"深色成员"，
    /// 且按彩度排第 3 的成员会**零出口**（leave-one-out 实测：去掉它界面毫无变化）；
    /// ② 补一枚**同族深青**（色相取自配色的青槽，不发明色相）把"墨/描边"变成显式成员——两个问题一并消失；
    /// ③ 设计档第 4 色 `#D7FADF` 与米白 `#EFFFE0` 色相相同、明度只差 3 档（近重复），故按"每枚成员都要有出口"
    /// 的口径只保留米白；`#BCF1E0` / `#8ED6DE` / `#EFFFE0` 三枚设计档原色原样保留。
    /// </para>
    /// <para>
    /// ⚠️ 第 4 色 = 背景色成员（`#EFFFE0`）：表面族色相与页面底由它推出（见上），改它等于改整页底色与卡面层感。
    /// </para>
    /// </remarks>
    public static ThemeDefinition Default { get; } = new()
    {
        Id = DefaultId,
        Source = ThemeSource.FactoryDefault,
        Palette = Palette(0x17434A, 0x8ED6DE, 0xBCF1E0, 0xEFFFE0),
        ChromaCap = ChromaCap.Standard,
    };

    /// <summary>内置预设（10 套；顺序 = 设计档「配色方案」的顺序）。</summary>
    /// <remarks>
    /// <para>
    /// <b>每套的身份色 = 设计档给的全部颜色</b>（设计档「配色方案.txt」：第 1–4 套 **5 色**、
    /// 第 5–10 套 **4 色**），**逐色原样收录**、不做任何"取两个代表色"的压缩。
    /// </para>
    /// <para>
    /// <b>为什么不钉中性色相</b>：配色里的**最浅成员就是"背景色"**——让表面族跟着它走，背景与那枚色点才是同一个颜色（融合）；
    /// 一旦用 <see cref="ThemeDefinition.NeutralHueOverride"/> 把表面色相钉到别处，背景就会和
    /// "背景色成员"分开（实测默认主题最浅色点 `#F2EEF5` 对页面底 `#E8E4ED` 的对比度 **1.09**、看得出两块；
    /// 而让表面族跟随最浅成员后，第 1–4 套实测 **1.01 / 1.06 / 1.04 / 1.01** —— 就是设计稿那种融合）。
    /// 因此**预设与出厂默认走同一条规则**（明度最高者管表面），不存在"预设钉值"这条特例分支。
    /// </para>
    /// <para>
    /// ⚠️ 第 5–10 套的配色里有**高彩度浅色**（如晴王青提饮 `#EDFFDB` C19、薄荷气泡水 `#EFFFE0` C16.8）：
    /// 它们当"背景色"时，页面底会带一点该色的彩度（融合度 1.09–1.20，比第 1–4 套略松）。
    /// 这是**设计档原色**的直接结果（不发明色相、不擅自降彩度），对比度矩阵实测仍全绿。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ThemeDefinition> Presets { get; } = new[]
    {
        // 旧出厂默认（紫罗兰）：2026-10-14 出厂默认改薄荷气泡水后**降级为普通预设**（配色原样保留、
        // 仍可选；「默认」徽标由 Source==FactoryDefault 决定，随新的出厂默认走）。
        Preset("violet", 0x3F3448, 0x6E5A80, 0xA18EB0, 0xEFC8DC, 0xE3DDE8),
        // 1–4：设计档「5 色配色」（暗 → 浅：暗调 / 主调 / 中间调 / 浅调 / 米白背景色）
        Preset("ochre-rose", 0x4A3322, 0x7E5E40, 0xC09480, 0xDCBFA8, 0xF0E6D6),
        Preset("dusk-rose", 0xC2A0A2, 0xB09295, 0x907884, 0xA3A6B0, 0xE6DEDC),
        Preset("lotus-sage", 0x3E4A40, 0x7A8A78, 0xC896A0, 0xE0C4C4, 0xF2E8E4),
        Preset("caramel-rose", 0x4A3424, 0x7E5C40, 0xC09478, 0xDCBEA0, 0xF0E4D2),
        // 5–10：设计档「4 色配色」（浅色底 / 浅彩 / 近白 / 深彩）
        Preset("uji-matcha", 0xE8F2EF, 0xBCE8D5, 0xD5EBD5, 0x60787A),
        // 唯一开了悬停可见性护栏的预设：这套配色的卡面只比页面底亮 6 档，
        // 悬停压深 6 档后正好回到页面底色值（按钮悬停底 #D7EBC9 对页面底 #D9EBC8，ΔRGB ≤ 2）——
        // 实测"鼠标移上去和背景一个色"，护栏把悬停档再往下压 3 档才分得开。
        // 第 1 色原本 `#DBF9F2`（青）彩度排第 3 ⇒ 只拿到"容器色相位"而容器实际色另取奶黄，青色被覆盖、
        // 界面上完全不可见（实测"青色和天蓝色都没用上"）。提高它的彩度让它占到**强调**槽（青色成为主调）。
        Preset("shine-muscat", 0xA9F0E2, 0xBDF9D8, 0xFDF5DA, 0xEDFFDB) with { EnforceHoverVisibility = true },
        Preset("blueberry-yogurt", 0xF2F6FF, 0xB7CBF4, 0xE2ECFF, 0x88ABF2),
        Preset("sakura-panna", 0xFEDFE9, 0xEEF6EE, 0xFEE6EC, 0xFFC7D6),
        Preset("green-pear", 0xDEEBB5, 0xF5FAED, 0xF4FEF1, 0xBAC9A7),
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

    /// <summary>内置预设：身份色 = 设计档给的全部颜色（4 或 5 个），与出厂默认同一套派生规则。</summary>
    /// <remarks>
    /// 这里**没有**中性色相参数：表面族一律由"明度最高的成员"决定（即配色里的背景色本身），
    /// 这样色点与背景才是同一个颜色。见 <see cref="Presets"/> 的注释。
    /// </remarks>
    private static ThemeDefinition Preset(string id, params int[] rgb) => new()
    {
        Id = id,
        Source = ThemeSource.BuiltInPreset,
        Palette = Palette(rgb),
        ChromaCap = ChromaCap.Standard,
    };

    private static IReadOnlyList<Argb> Palette(params int[] rgb) =>
        rgb.Select(v => Argb.FromInt(unchecked((int)(0xFF000000u | (uint)v)))).ToArray();
}
