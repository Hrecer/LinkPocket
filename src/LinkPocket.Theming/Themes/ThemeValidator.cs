using LinkPocket.Theming.Color;
using Material3.Core;

namespace LinkPocket.Theming.Themes;

/// <summary>诊断级别。</summary>
public enum ThemeIssueSeverity
{
    /// <summary>可以继续（自动处置 + 提示）。</summary>
    Info,

    /// <summary>必须拒绝（色数不对 / HEX 非法）——不猜意图。</summary>
    Error,
}

/// <summary>一条主题诊断。</summary>
/// <param name="Severity">级别。</param>
/// <param name="Code">机器可读代码（供 UI 分组/测试断言）。</param>
/// <param name="Message">给用户看的一句话。</param>
public readonly record struct ThemeIssue(ThemeIssueSeverity Severity, string Code, string Message);

/// <summary>
/// 主题校验与诊断（方案 §5.3 末段）：**不拒绝能自动处置的，明确拒绝不能猜的**。
/// </summary>
/// <remarks>
/// <para>
/// 四类诊断的边界很清楚：
/// <list type="bullet">
/// <item><b>拒绝</b>（<see cref="ThemeIssueSeverity.Error"/>）：颜色数不是 4/5、HEX 非法 ——
/// 这类"猜"必然猜错（本仓零兼容口径：拿不准就报错）。</item>
/// <item><b>提示</b>（<see cref="ThemeIssueSeverity.Info"/>）：无深色 / 无浅色 / 两色太接近 ——
/// 派生管线自己有正确处置（明度会被档位化重建），只需**如实告知**。</item>
/// </list>
/// </para>
/// <para>
/// "无浅色"特别说明：面层按**锚定表**生成（与配色无关），所以"没有浅色"并不影响背景——
/// 提示的措辞必须说清这一点，否则用户会以为背景会出问题。
/// </para>
/// </remarks>
public static class ThemeValidator
{
    /// <summary>判定"两色几乎一样"的三个阈值（方案 §5.3）。</summary>
    public const double NearDuplicateChromaDelta = 4.0;

    /// <summary>明度差阈值。</summary>
    public const double NearDuplicateToneDelta = 6.0;

    /// <summary>色相差阈值（度）。</summary>
    public const double NearDuplicateHueDelta = 10.0;

    /// <summary>判定"深色"的明度上界（tone 高于它 = 不算深）。</summary>
    public const double DarkToneLimit = 60.0;

    /// <summary>判定"浅色"的明度下界（tone 低于它 = 不算浅）。</summary>
    public const double LightToneLimit = 85.0;

    /// <summary>校验一个主题定义。返回全部诊断（空 = 完全没问题）。</summary>
    public static IReadOnlyList<ThemeIssue> Validate(ThemeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = new List<ThemeIssue>();

        // ① 色数（拒绝级）
        if (!definition.HasValidPaletteSize)
            issues.Add(new ThemeIssue(ThemeIssueSeverity.Error, "palette-size",
                $"主题需要 4 或 5 个颜色（当前 {definition.Palette.Count} 个）"));

        if (definition.Palette.Count == 0) return issues;

        var measured = definition.Palette.Select(c => (Color: c, Hct: ColorMath.Measure(c))).ToList();

        // ② 无深色 → 提示（T40 档会自动加深出强调色）
        if (measured.All(m => m.Hct.T > DarkToneLimit))
            issues.Add(new ThemeIssue(ThemeIssueSeverity.Info, "no-dark",
                "配色里没有深色 —— 已自动加深出强调色（界面里的主色会比你所选的颜色更深）"));

        // ③ 无浅色 → 提示（面层按锚定表生成，与配色无关；说清这一点免得用户误会背景会坏）
        if (measured.All(m => m.Hct.T < LightToneLimit))
            issues.Add(new ThemeIssue(ThemeIssueSeverity.Info, "no-light",
                "配色里没有浅色 —— 背景色由主题色相派生（层感与默认主题一致）"));

        // ④ 两色几乎一样 → 提示（不是错误：用户可能就是想微调）
        for (var i = 0; i < measured.Count; i++)
        {
            for (var j = i + 1; j < measured.Count; j++)
            {
                var a = measured[i].Hct;
                var b = measured[j].Hct;
                if (Math.Abs(a.C - b.C) < NearDuplicateChromaDelta
                    && Math.Abs(a.T - b.T) < NearDuplicateToneDelta
                    && ColorMath.HueDistance(a.H, b.H) < NearDuplicateHueDelta)
                {
                    issues.Add(new ThemeIssue(ThemeIssueSeverity.Info, "near-duplicate",
                        $"第 {i + 1} 个与第 {j + 1} 个颜色几乎一样，可以去掉一个"));
                }
            }
        }

        return issues;
    }

    /// <summary>是否有阻断级问题。</summary>
    public static bool HasErrors(IReadOnlyList<ThemeIssue> issues) =>
        issues.Any(i => i.Severity == ThemeIssueSeverity.Error);

    /// <summary>解析 HEX（<c>#RRGGBB</c> / <c>RRGGBB</c> / <c>#RGB</c>）；非法返回 false。</summary>
    public static bool TryParseHex(string? text, out Argb argb)
    {
        argb = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s.Select(ch => new string(ch, 2)));
        if (s.Length != 6) return false;
        if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return false;
        argb = Argb.FromInt(unchecked((int)(0xFF000000u | rgb)));
        return true;
    }

    /// <summary>格式化为 <c>#RRGGBB</c>（大写）。</summary>
    public static string ToHex(Argb c) => $"#{(c.ToInt() & 0x00FFFFFF):X6}";
}
