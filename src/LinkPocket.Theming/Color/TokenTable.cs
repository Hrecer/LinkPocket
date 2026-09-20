using Material3.Core;

namespace LinkPocket.Theming.Color;

/// <summary>
/// **色相锚定族**：共用同一色相与彩度的若干键。用于让"同一支主色"在多个键上自动一致
/// （强调填充与强调图标必然同色——同一件事同一色）。
/// </summary>
/// <param name="Token">应用令牌名（<see cref="Tokens.AppTokens"/> 里的常量）。</param>
/// <param name="Hue">族色相（度）。</param>
/// <param name="Chroma">族彩度（已按彩度上限档钳制）。</param>
/// <param name="Tone">该键的明度档。</param>
/// <param name="Alpha">该键的 α（HCT 不含 α，必须显式携带）。</param>
public readonly record struct HueAnchor(string Token, double Hue, double Chroma, double Tone, byte Alpha = 0xFF);

/// <summary>一个主题派生出的**完整令牌表**：唯一产物，发布、预览、测试断言都只认它。</summary>
/// <remarks>
/// 两条来源不同的通道：
/// <list type="bullet">
/// <item><b>锚定键</b>（<see cref="Anchored"/>）：库的 49 个资源键（43 角色 + 6 个 <c>SurfaceElevation</c>）
/// —— 逐键按锚定表旋转色相，保证层感恒定。</item>
/// <item><b>应用令牌</b>（<see cref="Tokens"/>）：我们的语义令牌（<c>App.*</c>）—— 由色相锚定族派生，
/// 并与对应的库键**同值**（同一个语义只有一个真值）。</item>
/// </list>
/// </remarks>
public sealed class TokenTable
{
    /// <summary>库资源键 → 值（锚定/旋转结果）。</summary>
    public required IReadOnlyDictionary<string, Argb> Anchored { get; init; }

    /// <summary>应用令牌名 → 值（语义层，供界面与样式消费）。</summary>
    public required IReadOnlyDictionary<string, Argb> Tokens { get; init; }

    /// <summary>表面旋转角（度）：本主题中性色相相对今天背景色相的位移。</summary>
    public required double SurfaceRotation { get; init; }

    /// <summary>分族结果（诊断面板与"派生结果"摘要用）。</summary>
    public required Themes.ThemeFamilies Families { get; init; }

    /// <summary>取应用令牌值（缺失即抛——令牌表必须自洽，不允许静默兜底）。</summary>
    public Argb Token(string name) => Tokens.TryGetValue(name, out var v)
        ? v
        : throw new KeyNotFoundException($"令牌表中不存在应用令牌「{name}」（令牌完整性由 ThemeContrastTests 卡住）");

    /// <summary>取库键值（缺失即抛）。</summary>
    public Argb Key(string name) => Anchored.TryGetValue(name, out var v)
        ? v
        : throw new KeyNotFoundException($"令牌表中不存在库键「{name}」");
}
