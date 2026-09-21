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

    /// <summary>
    /// **配色应用方式**（外观面板的「自动调整颜色」开关）：
    /// <see cref="PaletteMode.Auto"/>（缺省，开关**打开**）= 按明度档位表自动重排 ⇒ 层感与可读性有保证；
    /// <see cref="PaletteMode.Exact"/>（开关关闭）= 尽量原样用用户给的颜色。
    /// </summary>
    /// <remarks>
    /// 它**影响每一个令牌**，所以随偏好落盘、并在 <see cref="Apply"/> 时统一写进主题定义
    /// （调用方不必各自传一遍，避免"有的入口忘了带"）；开关切换时主题卡色点同步刷新。
    /// </remarks>
    public static PaletteMode PaletteMode { get; private set; } = DefaultPaletteMode;

    /// <summary>
    /// 「自动调整颜色」的**出厂缺省值 = 打开**（唯一事实源：主题定义缺省 / 偏好缺省 / 服务初值都取它）。
    /// </summary>
    public const PaletteMode DefaultPaletteMode = PaletteMode.Auto;

    /// <summary>设置配色应用方式并**立即重新应用当前主题**（界面当场跟随）。</summary>
    public static TokenTable SetPaletteMode(PaletteMode mode, ResourceDictionary? resources = null)
    {
        PaletteMode = mode;
        var applied = Apply(_current with { PaletteMode = mode }, resources);
        SaveCurrentPreferences();
        return applied;
    }

    /// <summary>当前**实际发布**的令牌表（= <see cref="DerivedTable"/> 的缓存）；未 Apply 也可读，便于单测与预览。</summary>
    public static TokenTable Table => _table ??= PaletteSolver.Solve(_current);

    /// <summary>
    /// **最终语义**令牌表：对比度矩阵 / 关键值核对的验收入口（与 <see cref="Table"/> 同源）。
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

        // 配色应用方式由服务统一写入（调用方不必各自记得带 → 不存在"某个入口忘了传"的漂移）
        definition = definition with { PaletteMode = PaletteMode };
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
    public static string CurrentUiFont { get; private set; } = Fonts.FontCatalog.DefaultUiFamily;

    /// <summary>当前等宽字体族名。</summary>
    public static string CurrentMonoFont { get; private set; } = Fonts.FontCatalog.DefaultMonoFamily;

    /// <summary>
    /// 应用字体（发布 <c>App.Font.Ui</c> / <c>App.Font.Mono</c> 两个令牌）。
    /// </summary>
    /// <remarks>
    /// 界面侧对这两个令牌的引用必须是 <c>DynamicResource</c>：<c>StaticResource</c> 在解析时就固化了值，
    /// 换字体会毫无反应（这是"字体系统"的物理前提）。
    /// </remarks>
    public static void ApplyFonts(string? uiFamily = null, string? monoFamily = null, ResourceDictionary? resources = null)
    {
        CurrentUiFont = string.IsNullOrWhiteSpace(uiFamily) ? Fonts.FontCatalog.DefaultUiFamily : uiFamily.Trim();
        CurrentMonoFont = string.IsNullOrWhiteSpace(monoFamily) ? Fonts.FontCatalog.DefaultMonoFamily : monoFamily.Trim();

        var target = resources ?? Application.Current?.Resources;
        if (target is not null)
            ThemePublisher.PublishFonts(target, CurrentUiFont, CurrentMonoFont);

        LpLog.Info($"已应用字体：界面「{CurrentUiFont}」/ 等宽「{CurrentMonoFont}」", LogCategory);
    }

    /// <summary>
    /// 已删除的字体若是**当前正在用的那一支**，把它退回默认字体（并落盘）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须有这一步</b>：删掉导入字体后，偏好里仍留着那个族名，而 <see cref="CurrentUiFont"/> 也还指着它 ——
    /// 于是 ① 重启时会走 <see cref="ApplyFromPreferences"/> 的"已不可用"分支回退并弹提示（提示的触发时机不可解释），
    /// ② 同一次运行里再点「应用字体」，会把一个**已经不存在的族名**重新落盘，
    /// 渲染端静默走回退链、界面却写着"已应用字体「X」"（本仓明令禁止的静默失败）。
    /// </para>
    /// <para>
    /// 判据 = 族名与实际生效值比较（大小写不敏感）；命中就当场退回默认并保存。
    /// </para>
    /// </remarks>
    /// <returns>被退回默认的字体族名（没命中返回 <c>null</c>）。</returns>
    public static string? ResetFontIfDeleted(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return null;

        var hit = false;
        if (string.Equals(CurrentUiFont, family, StringComparison.OrdinalIgnoreCase))
        {
            CurrentUiFont = Fonts.FontCatalog.DefaultUiFamily;
            hit = true;
        }
        if (string.Equals(CurrentMonoFont, family, StringComparison.OrdinalIgnoreCase))
        {
            CurrentMonoFont = Fonts.FontCatalog.DefaultMonoFamily;
            hit = true;
        }
        if (!hit) return null;

        var target = Application.Current?.Resources;
        if (target is not null) ThemePublisher.PublishFonts(target, CurrentUiFont, CurrentMonoFont);
        SaveCurrentPreferences();
        LpLog.Info($"已删除的字体「{family}」正是当前生效的字体 → 已回退默认并落盘", LogCategory);
        return family;
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

        // 「自动调整颜色」开关先于主题应用生效（Apply 会把它写进定义）
        PaletteMode = prefs.Theme.AutoAdjustColors ? PaletteMode.Auto : PaletteMode.Exact;

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
                AutoAdjustColors = PaletteMode == PaletteMode.Auto,
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
    /// <remarks>
    /// <para>
    /// <b>判据 = <see cref="Fonts.FontCatalog.All"/>（"这个字体能不能选"的唯一事实来源）</b>：
    /// 已导入文件 + 系统已装字体。启动路径与「外观」面板的候选列表读的是同一份，
    /// 不会出现"面板里能选、重启后判成不可用"这种两套判据的分歧。
    /// </para>
    /// <para>
    /// ⚠️ 这里会触发系统字体的**首次全量枚举**（开销与机器上装的字体数量成正比；本机实测 17ms /
    /// 88 个族，装了几百个族的机器上是秒级）。这是**必须付**的成本：判据正确性优先于省这一步 ——
    /// "省掉它"的写法（只认一张手写白名单）会把用户机器上真实存在的字体误判成不可用，
    /// 而那正是"换字体没反应"这类静默失败的开端。代价由缓存兜住
    /// （<see cref="Fonts.WpfSystemFontSource"/> 每实例只枚举一次），且本方法只在启动时走一次。
    /// </para>
    /// </remarks>
    private static (string Ui, string Mono, string? Reason) ResolveFonts(Preferences.FontPreference pref)
    {
        var available = Fonts.FontCatalog.All()
            .Select(f => f.Family)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 两条理由**都**要如实说（旧实现用 `reason ??=` 让等宽那条被静默吞掉：
        // 用户只看到"界面字体已不可用"，等宽那份悄悄回退 = 观测面缺陷）。
        var reasons = new List<string>();

        var ui = pref.Ui;
        var mono = pref.Mono;
        if (!string.IsNullOrWhiteSpace(ui) && !available.Contains(ui))
        {
            reasons.Add($"界面字体「{ui}」已不可用（文件缺失或未安装），已回退默认字体");
            ui = Fonts.FontCatalog.DefaultUiFamily;
        }
        if (!string.IsNullOrWhiteSpace(mono) && !available.Contains(mono))
        {
            reasons.Add($"等宽字体「{mono}」已不可用（文件缺失或未安装），已回退默认字体");
            mono = Fonts.FontCatalog.DefaultMonoFamily;
        }

        var reason = reasons.Count == 0 ? null : string.Join("；", reasons);
        if (reason is not null) LpLog.Warn(reason, category: LogCategory);
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
    /// 测试/宿主收尾复位（主题、字体、缓存表、事件订阅全部回默认，**并清掉偏好文件**）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 必须连偏好文件一起清：<see cref="ApplyFromPreferences"/> 会从文件恢复外观，
    /// 若不清文件，上一个用例（或用例外的真实运行）落下的主题会漏进下一个用例 ——
    /// 表现为"测试结果取决于执行顺序"的偶发红（同族教训见 WARNINGS 63 的时序敏感项）。
    /// </remarks>
    public static void ResetForTests() => ResetCore(clearPreferences: true);

    /// <summary>
    /// **模拟进程重启**：清进程内的主题/字体状态与事件订阅，但**保留偏好文件**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 存在的理由 = 可测性（"导入 → 应用 → 重启保持"这条链路必须能自动化验证），
    /// 与 <see cref="ResetForTests"/> 同属**测试收尾/夹具**一类公开成员 ——
    /// 不放进 <c>internal</c> 是因为本仓的红线是"程序集内部可见性零残留"（架构测试
    /// <c>LayerRulesTests</c> 卡住），不允许为了一个测试给程序集开后门。
    /// </para>
    /// <para>
    /// 必须是**独立**方法：<see cref="ResetForTests"/> 会连偏好文件一起清，
    /// 用它模拟重启会让 <see cref="ApplyFromPreferences"/> 什么都读不到 ——
    /// 那条用例就退化成"断言默认值"，测试看着绿、其实没覆盖（典型的假绿）。
    /// 生产路径的重启是真正的进程重启，不经过这里。
    /// </para>
    /// </remarks>
    public static void ResetInMemoryForRestartTests() => ResetCore(clearPreferences: false);

    private static void ResetCore(bool clearPreferences)
    {
        _current = ThemeCatalog.Default;
        _table = null;
        PaletteMode = DefaultPaletteMode;   // 开关回缺省（打开 = 自动调色）—— 否则会漏进下一个用例
        CurrentUiFont = Fonts.FontCatalog.DefaultUiFamily;
        CurrentMonoFont = Fonts.FontCatalog.DefaultMonoFamily;
        if (clearPreferences) Preferences.UiPreferenceStore.Clear();
        // 字体来源与缓存也要复位：用例可能注入了假字体列表（FontCatalog.SystemSource），
        // 留着会让下一个用例继续看到它 —— 同一类"测试结果取决于执行顺序"的偶发红。
        Fonts.FontCatalog.ResetForTests();
        Changed = null;
    }

    /// <summary>应用主题这一动作的日志分类（供设置页与诊断对齐）。</summary>
    public const string LogCategory = "app.theme";
}
