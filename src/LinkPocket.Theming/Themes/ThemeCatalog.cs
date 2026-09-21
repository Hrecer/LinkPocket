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
    /// <summary>出厂默认主题的 id。</summary>
    public const string DefaultId = "factory-default";

    /// <summary>
    /// 出厂默认主题（保留紫色身份，5 个色全部占槽）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>表面族色相 = 配色里最浅的那个成员</b>（不钉中性色相，配色成员优先级最高）；
    /// <b>表面族彩度 = 配色里"浅调成员"的量级</b>（见 `ThemeFamilies.SurfaceChroma`）。
    /// </para>
    /// <para>
    /// ⚠️ 第 5 色 = 背景色成员：表面族色相与页面底由它推出（见上），改它等于改整页底色与卡面层感。
    /// </para>
    /// <para>
    /// 第 4 色（浅调成员）取 <c>#E0CEEC</c>（H310.5 C18.8 T85，与强调填充同一个紫、更清爽，
    /// 原值 `#D5C7DE` C15.1 / T82 偏灰）。彩度刻意保持在 16.6–22.4 之间 ——
    /// 高于就抢支撑槽（#A18EB0）、低于就抢描边槽（#3F3448），只有落在这段里才"只换它自己"。
    /// </para>
    /// </remarks>
    public static ThemeDefinition Default { get; } = new()
    {
        Id = DefaultId,
        Source = ThemeSource.FactoryDefault,
        Palette = Palette(0x3F3448, 0x6E5A80, 0xA18EB0, 0xE0CEEC, 0xE3DDE8),
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
        // 1–4：设计档「5 色配色」（暗 → 浅：暗调 / 主调 / 中间调 / 浅调 / 米白背景色）
        Preset("ochre-rose", 0x4A3322, 0x7E5E40, 0xC09480, 0xDCBFA8, 0xF0E6D6),
        Preset("dusk-rose", 0xC2A0A2, 0xB09295, 0x907884, 0xA3A6B0, 0xE6DEDC),
        Preset("lotus-sage", 0x3E4A40, 0x7A8A78, 0xC896A0, 0xE0C4C4, 0xF2E8E4),
        Preset("caramel-rose", 0x4A3424, 0x7E5C40, 0xC09478, 0xDCBEA0, 0xF0E4D2),
        // 5–10：设计档「4 色配色」（浅色底 / 浅彩 / 近白 / 深彩）
        Preset("uji-matcha", 0xE8F2EF, 0xBCE8D5, 0xD5EBD5, 0x60787A),
        Preset("shine-muscat", 0xDBF9F2, 0xBDF9D8, 0xFDF5DA, 0xEDFFDB),
        Preset("blueberry-yogurt", 0xF2F6FF, 0xB7CBF4, 0xE2ECFF, 0x88ABF2),
        Preset("mint-soda", 0xBCF1E0, 0xEFFFE0, 0xD7FADF, 0x8ED6DE),
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
