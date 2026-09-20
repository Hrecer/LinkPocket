using LinkPocket.Theming.Color;
using Material3.Core;

namespace LinkPocket.Theming.Themes;

/// <summary>
/// **主题目录**：出厂默认 1 套 + 内置预设 10 套（方案附录 A 的实测派生表，共 11 套可选）。
/// </summary>
/// <remarks>
/// <para>
/// 身份色 = 一个主题的"颜色本身"（「外观」面板的主题卡与再编辑都展示它们）。界面里出现的
/// 是这些原色的**色调板档位**（例如定稿紫 <c>#A18EB0</c> T61.7 → 填充档 <c>#6A567C</c> T40）。
/// </para>
/// <para>
/// <b>出厂默认钉住中性色相</b>（<see cref="ThemeDefinition.ReferenceNeutralHue"/>）→ 表面旋转角恰为 0
/// → 全部 53 个键逐字节等于今天，满足"默认主题保留目前的背景色"。其它主题为 <c>null</c>，按配色里的
/// 中性池派生（实测：抹茶 203.5° / 焦糖 61.2° / 樱花 357.8° …，与附录 A 一致）。
/// </para>
/// </remarks>
public static class ThemeCatalog
{
    /// <summary>出厂默认主题的 id。</summary>
    public const string DefaultId = "factory-default";

    /// <summary>出厂默认主题（保留紫色身份，按新规则重建）。</summary>
    public static ThemeDefinition Default { get; } = new()
    {
        Id = DefaultId,
        Name = "默认（紫罗兰）",
        Source = ThemeSource.FactoryDefault,
        Palette = Palette(0x3F3448, 0x6E5A80, 0xA18EB0, 0xD5C7DE, 0xF2EEF5),
        ChromaCap = ChromaCap.Standard,
        NeutralHueOverride = ThemeDefinition.ReferenceNeutralHue,
    };

    /// <summary>内置预设（10 套；顺序 = 方案附录 A 的顺序）。</summary>
    /// <remarks>
    /// <para>
    /// <b>身份色由附录 A 的目标反算</b>（标定脚本一次跑出，值已固化）：每套取「强调族色 + 支撑族色」
    /// 两个彩色成员，中性色相由 <see cref="ThemeDefinition.NeutralHueOverride"/> 钉住（= 附录 A 表面底的色相）。
    /// 于是每套的派生结果与附录 A **逐项对齐**：强调族 H/C ≤1.8°、支撑族 H/C ≤1.3°、表面底 RGB ≤2/255。
    /// </para>
    /// <para>
    /// <b>为什么中性色相要钉住</b>：预设的身份色里若放一个低彩度浅色，它会同时决定中性池色相，
    /// 使表面族随身份色漂移（实测会让表面底偏 1–3/255 且"层感"跟着挪）。钉住它之后，
    /// 身份色只承载"强调/支撑两支主色"，表面族完全由中性色相决定——自由度从 3 个降到 2 个，
    /// 标定才能收敛。出厂默认同理（钉 298.7°），这是**同一条规则**而不是特例。
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
