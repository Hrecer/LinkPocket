using System.Windows;
using LinkPocket.Contracts;
using LinkPocket.Theming.Color;
using LinkPocket.Theming.Publishing;
using LinkPocket.Theming.Themes;
using LinkPocket.Theming.Tokens;

namespace LinkPocket.Theming;

/// <summary>
/// **主题服务**：全站唯一的主题入口（<see cref="Apply"/> / <see cref="Current"/> / <see cref="Changed"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 纪律（方案 §5.8）：<c>M3Theme.Apply</c> + 权威表写入**只在 <see cref="ThemePublisher"/> 里出现一次**；
/// 宿主（<c>App</c>）与探针**都只能经本类**——这是"治双份事实源"的落点
/// （原先 App 与 SmartProbe 各抄了一份"Apply + 3 个表面补丁"）。
/// </para>
/// <para>
/// <b>为什么不提供"只调 Publish 不调 Apply"</b>：库模板自己消费的键（浮层内部等）需要基线，
/// 少一步会出现库默认紫的孤岛。
/// </para>
/// </remarks>
public static class ThemeService
{
    private static ThemeDefinition _current = ThemeCatalog.Default;
    private static TokenTable? _table;

    /// <summary>主题已应用（控件/转换器无需接线：全部走 <c>DynamicResource</c>）。</summary>
    public static event EventHandler<ThemeDefinition>? Changed;

    /// <summary>当前主题定义。</summary>
    public static ThemeDefinition Current => _current;

    /// <summary>当前**实际发布**的令牌表（= <see cref="DerivedTable"/> 的缓存）；未 Apply 也可读，便于单测与预览。</summary>
    public static TokenTable Table => _table ??= PaletteSolver.Solve(_current);

    /// <summary>
    /// **最终语义**令牌表：对比度矩阵 / 定稿值核对的验收入口（与 <see cref="Table"/> 同源）。
    /// </summary>
    public static TokenTable DerivedTable => PaletteSolver.Solve(_current);

    /// <summary>
    /// 应用一个主题定义：求解 → 发布 → 记留痕 → 抛 <see cref="Changed"/>。
    /// </summary>
    /// <param name="definition">主题定义。</param>
    /// <param name="resources">目标资源字典（缺省 = <c>Application.Current.Resources</c>）。</param>
    /// <returns>本次实际发布的令牌表（调用方若要立即读新值，用返回值而不是 <see cref="Table"/>）。</returns>
    public static TokenTable Apply(ThemeDefinition definition, ResourceDictionary? resources = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var table = PaletteSolver.Solve(definition);

        _current = definition;
        _table = table;

        var target = resources ?? Application.Current?.Resources;
        if (target is not null)
            ThemePublisher.Publish(target, table, table.Token(Tokens.AppTokens.AccentFill));

        ThemePublisher.LogApplied(definition, table);
        Changed?.Invoke(null, definition);
        return table;
    }

    /// <summary>按 id 应用（找不到 → 回退出厂默认；启动恢复路径用，不许崩）。</summary>
    public static TokenTable ApplyById(string? id, ResourceDictionary? resources = null) =>
        Apply(ThemeCatalog.FindOrDefault(id), resources);

    /// <summary>回到出厂默认主题。</summary>
    public static TokenTable ApplyDefault(ResourceDictionary? resources = null) =>
        Apply(ThemeCatalog.Default, resources);

    /// <summary>
    /// 只求解不发布（预览用：「外观」面板的实时预览与对比度体检都走它，不污染全局资源）。
    /// </summary>
    public static TokenTable Preview(ThemeDefinition definition) => PaletteSolver.Solve(definition);

    // ── 字体（与主题是两条独立的轴：一个主题不携带字体）──────────────────────

    /// <summary>当前界面字体族名（令牌链的第一段）。</summary>
    public static string CurrentUiFont { get; private set; } = Fonts.FontLoader.DefaultUiFamily;

    /// <summary>当前等宽字体族名。</summary>
    public static string CurrentMonoFont { get; private set; } = Fonts.FontLoader.DefaultMonoFamily;

    /// <summary>
    /// 应用字体（发布 <c>App.Font.Ui</c> / <c>App.Font.Mono</c> 两个令牌）。
    /// </summary>
    /// <remarks>
    /// 界面侧对这两个令牌的引用必须是 <c>DynamicResource</c>：<c>StaticResource</c> 在解析时就固化了值，
    /// 换字体会毫无反应（这是"字体系统"的物理前提）。
    /// </remarks>
    public static void ApplyFonts(string? uiFamily = null, string? monoFamily = null, ResourceDictionary? resources = null)
    {
        CurrentUiFont = string.IsNullOrWhiteSpace(uiFamily) ? Fonts.FontLoader.DefaultUiFamily : uiFamily.Trim();
        CurrentMonoFont = string.IsNullOrWhiteSpace(monoFamily) ? Fonts.FontLoader.DefaultMonoFamily : monoFamily.Trim();

        var target = resources ?? Application.Current?.Resources;
        if (target is not null)
            ThemePublisher.PublishFonts(target, CurrentUiFont, CurrentMonoFont);

        LpLog.Info($"已应用字体：界面「{CurrentUiFont}」/ 等宽「{CurrentMonoFont}」", LogCategory);
    }

    /// <summary>
    /// 从偏好文件恢复外观（主题 + 字体）。启动路径用。
    /// </summary>
    /// <param name="resources">目标资源字典。</param>
    /// <returns>(是否回退过默认；回退原因)——调用方据此向用户提示**一次**。</returns>
    /// <remarks>
    /// 字体文件缺失（换机 / 被删）时**回退默认并如实报告**，但偏好里的条目**保留**
    /// （文件回来即恢复）——这符合"不替用户丢掉他的选择"。
    /// </remarks>
    public static (bool FellBack, string? Reason) ApplyFromPreferences(ResourceDictionary? resources = null)
    {
        var prefs = Preferences.UiPreferenceStore.Load(out var loadFailed);
        string? reason = loadFailed ? "界面偏好文件无法读取（已回退默认外观）" : null;

        var definition = ResolveDefinition(prefs.Theme);
        Apply(definition, resources);

        var (ui, mono, fontReason) = ResolveFonts(prefs.Fonts);
        if (fontReason is not null) reason ??= fontReason;
        ApplyFonts(ui, mono, resources);

        return (reason is not null, reason);
    }

    /// <summary>保存当前外观（主题 + 字体）到偏好文件。失败**抛出**（写不进去必须让调用方知道）。</summary>
    public static void SaveCurrentPreferences()
    {
        Preferences.UiPreferenceStore.Save(new Preferences.UiPreferences
        {
            Theme = new Preferences.ThemePreference
            {
                Id = _current.Source == Themes.ThemeSource.UserDefined ? null : _current.Id,
                Colors = _current.Source == Themes.ThemeSource.UserDefined
                    ? _current.Palette.Select(ToHex).ToArray()
                    : null,
                NeutralHue = _current.Source == Themes.ThemeSource.UserDefined ? _current.NeutralHueOverride : null,
            },
            Fonts = new Preferences.FontPreference { Ui = CurrentUiFont, Mono = CurrentMonoFont },
        });
    }

    /// <summary>偏好里的主题 → 主题定义（自选配色按需重建；非法一律回退出厂默认）。</summary>
    private static Themes.ThemeDefinition ResolveDefinition(Preferences.ThemePreference pref)
    {
        if (!pref.IsCustom)
            return ThemeCatalog.FindOrDefault(pref.Id);

        var palette = new List<Material3.Core.Argb>();
        foreach (var hex in pref.Colors!)
        {
            if (!TryParseHex(hex, out var argb))
            {
                LpLog.Warn($"偏好里的自选配色含非法色值「{hex}」→ 回退出厂默认主题", category: LogCategory);
                return ThemeCatalog.Default;
            }
            palette.Add(argb);
        }

        var candidate = new Themes.ThemeDefinition
        {
            Id = "user-custom",
            Name = "自选配色",
            Source = Themes.ThemeSource.UserDefined,
            Palette = palette,
            NeutralHueOverride = pref.NeutralHue,
        };

        var issues = Themes.ThemeValidator.Validate(candidate);
        var blocking = issues.Where(i => i.Severity == Themes.ThemeIssueSeverity.Error).ToList();
        if (blocking.Count > 0)
        {
            LpLog.Warn($"偏好里的自选配色不合法（{string.Join("；", blocking.Select(b => b.Message))}）→ 回退出厂默认主题", category: LogCategory);
            return ThemeCatalog.Default;
        }
        return candidate;
    }

    /// <summary>偏好里的字体 → 族名（导入文件已不存在时如实报告，但**保留**偏好条目）。</summary>
    private static (string Ui, string Mono, string? Reason) ResolveFonts(Preferences.FontPreference pref)
    {
        var imported = Fonts.FontLoader.ImportedFonts().Select(f => f.Family).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var system = default(HashSet<string>?);
        string? reason = null;

        bool Available(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return false;
            if (imported.Contains(family)) return true;
            system ??= Fonts.FontLoader.SystemFonts().Select(f => f.Family).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return system.Contains(family);
        }

        var ui = pref.Ui;
        var mono = pref.Mono;
        if (!Available(ui))
        {
            reason = $"界面字体「{ui}」已不可用（文件缺失或未安装），已回退默认字体";
            ui = Fonts.FontLoader.DefaultUiFamily;
        }
        if (!Available(mono))
        {
            reason ??= $"等宽字体「{mono}」已不可用，已回退默认字体";
            mono = Fonts.FontLoader.DefaultMonoFamily;
        }
        return (ui, mono, reason);
    }

    private static string ToHex(Material3.Core.Argb c) => $"#{c.ToInt() & 0x00FFFFFF:X6}";

    private static bool TryParseHex(string? text, out Material3.Core.Argb argb)
    {
        argb = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s.Select(ch => new string(ch, 2)));   // #RGB 展开
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return false;
        argb = Material3.Core.Argb.FromInt(unchecked((int)(0xFF000000u | rgb)));
        return true;
    }

    /// <summary>
    /// 测试/宿主收尾复位（把当前主题、字体与缓存表还原为出厂默认，不发布）。
    /// </summary>
    public static void ResetForTests()
    {
        _current = ThemeCatalog.Default;
        _table = null;
        CurrentUiFont = Fonts.FontLoader.DefaultUiFamily;
        CurrentMonoFont = Fonts.FontLoader.DefaultMonoFamily;
        Changed = null;
    }

    /// <summary>应用主题这一动作的日志分类（供设置页与诊断对齐）。</summary>
    public const string LogCategory = "app.theme";
}
