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
    public static string CurrentUiFont { get; private set; } = Fonts.FontCatalog.DefaultUiFamily(null);

    /// <summary>当前等宽字体族名。</summary>
    public static string CurrentMonoFont { get; private set; } = Fonts.FontCatalog.DefaultMonoFamily;

    // ── 语言（透传：持久化在本层，语义在 LinkPocket.I18n）───────────────────
    // 为什么不让 I18n 自己开一个偏好文件：偏好文件只有一份、要对用户可读，而 I18n 不能引 Theming
    // （两边互引即成环）。中间以纯字符串为通货，本层只存不解释。
    // ⚠️ 唯一的例外是"默认字体族按语言给"（决策 5）：那需要知道当前语言码，
    //    但语义仍在 I18n —— 本层只读一个字符串，不解释它。

    /// <summary>偏好里的语言模式（<c>auto</c> / <c>fixed</c>）；出厂缺省 = 跟随系统。</summary>
    public static string LanguageMode { get; private set; } = LocalePreference.ModeAuto;

    /// <summary>固定语言时的语言码；<c>auto</c> 时为 null。</summary>
    public static string? LanguageOverride { get; private set; }

    /// <summary>
    /// 当前**生效**的语言码，由组合根在 <see cref="ApplyFromPreferences"/> 之前登记
    /// （<c>auto</c> 的解析归 I18n，本层只接收结论）。
    /// </summary>
    public static string? ActiveLanguageCode { get; private set; }

    /// <summary>
    /// 登记当前生效的语言码（组合根在启动序里调用，先于 <see cref="ApplyFromPreferences"/>）。
    /// </summary>
    /// <remarks>
    /// <b>为什么不在这里自己解析 <c>auto</c></b>：那需要读系统 UI 语言并套 I18n 的别名表 ——
    /// 等于把语言语义抄进 Theming（双份事实源，见 <c>AppLocales.Resolve</c>）。
    /// 本层只要一个字符串，用来选默认字体族。
    /// </remarks>
    public static void SetActiveLanguage(string? code) => ActiveLanguageCode = code;

    /// <summary>
    /// 本层理解的默认界面字体族：**偏好里显式选过的族优先，没选过就按当前语言给默认族**（决策 5）。
    /// </summary>
    public static string DefaultUiFont => Fonts.FontCatalog.DefaultUiFamily(ActiveLanguageCode);

    /// <summary>
    /// 应用字体（发布 <c>App.Font.Ui</c> / <c>App.Font.Mono</c> 两个令牌）。
    /// </summary>
    /// <remarks>
    /// 界面侧对这两个令牌的引用必须是 <c>DynamicResource</c>：<c>StaticResource</c> 在解析时就固化了值，
    /// 换字体会毫无反应（这是"字体系统"的物理前提）。
    /// </remarks>
    public static void ApplyFonts(string? uiFamily = null, string? monoFamily = null, ResourceDictionary? resources = null)
    {
        CurrentUiFont = string.IsNullOrWhiteSpace(uiFamily) ? DefaultUiFont : uiFamily.Trim();
        CurrentMonoFont = string.IsNullOrWhiteSpace(monoFamily) ? Fonts.FontCatalog.DefaultMonoFamily : monoFamily.Trim();

        var target = resources ?? Application.Current?.Resources;
        if (target is not null)
            ThemePublisher.PublishFonts(target, CurrentUiFont, CurrentMonoFont);

        LpLog.Info($"Applied fonts: UI '{CurrentUiFont}' / mono '{CurrentMonoFont}'", LogCategory);
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
            CurrentUiFont = DefaultUiFont;
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
        LpLog.Info($"The deleted font '{family}' was the active one -> fell back to the default and persisted", LogCategory);
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
        string? reason = loadFailed ? "the UI preference file is unreadable (fell back to the default appearance)" : null;

        // 「自动调整颜色」开关先于主题应用生效（Apply 会把它写进定义）
        PaletteMode = prefs.Theme.AutoAdjustColors ? PaletteMode.Auto : PaletteMode.Exact;

        // 语言偏好只是**透传**：本层负责把它存下来/交出去，"auto 是什么、语言码指向谁"由 I18n 解释。
        // 组合根据此调 LocaleService.Apply，切换后再写回这里并落盘。
        SetLanguagePreference(prefs.Language.Mode, prefs.Language.Override);

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
            // 字体偏好**空 = 当前语言的默认族**（决策 5）：落盘时把"就是默认族"的写法归一成空，
            // 否则"恢复默认字体"会写下一个**具体族名**——用户之后切语言时，它就被当成
            // "显式选过的族"（用户选择优先于语言），默认族永远不跟着语言走。
            // 落盘仍是同一个文件、同一条路径，只是把"没选过"这个事实写成"没选过"。
            Fonts = new Preferences.FontPreference
            {
                Ui = UiPreferenceOrNull(CurrentUiFont),
                Mono = string.Equals(CurrentMonoFont, Fonts.FontCatalog.DefaultMonoFamily, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : CurrentMonoFont,
            },
            Language = new Preferences.LanguagePreference { Mode = LanguageMode, Override = LanguageOverride },
        });
    }

    /// <summary>
    /// 记下用户的语言选择（**不落盘**——落盘由调用方走 <see cref="SaveCurrentPreferences"/>，
    /// 与主题/字体同一条路，避免出现第二套写文件的路径）。
    /// </summary>
    /// <remarks>
    /// 设置模式与固定码之后，<see cref="ActiveLanguageCode"/> 由组合根用
    /// <c>AppLocales.Resolve</c> 的结论登记（本层不解释 <c>auto</c>）。
    /// </remarks>
    public static void SetLanguagePreference(string? mode, string? @override)
    {
        LanguageMode = string.IsNullOrWhiteSpace(mode) ? LocalePreference.ModeAuto : mode.Trim();
        LanguageOverride = string.IsNullOrWhiteSpace(@override) ? null : @override.Trim();
    }

    /// <summary>
    /// 界面字体写成偏好值时归一：**等于当前语言的默认族就是"没选过"（null）**，否则原样记下。
    /// </summary>
    /// <remarks>
    /// 判据用 <see cref="DefaultUiFont"/>（当前语言）而不是某个固定族名：
    /// "恢复默认字体"在英文界面下回的是 <c>Segoe UI</c>，那也要落成"没选过"。
    /// </remarks>
    private static string? UiPreferenceOrNull(string family)
        => string.Equals(family, DefaultUiFont, StringComparison.OrdinalIgnoreCase) ? null : family;
    private static Themes.ThemeDefinition ResolveDefinition(Preferences.ThemePreference pref)
    {
        if (!pref.IsCustom)
            return ThemeCatalog.FindOrDefault(pref.Id);

        var palette = new List<Material3.Core.Argb>();
        foreach (var hex in pref.Colors!)
        {
            if (!TryParseHex(hex, out var argb))
            {
                LpLog.Warn($"the custom palette in preferences contains an invalid color '{hex}' -> fell back to the factory default theme", category: LogCategory);
                return ThemeCatalog.Default;
            }
            palette.Add(argb);
        }

        var candidate = new Themes.ThemeDefinition
        {
            Id = "user-custom",
            Source = Themes.ThemeSource.UserDefined,
            Palette = palette,
            NeutralHueOverride = pref.NeutralHue,
        };

        var issues = Themes.ThemeValidator.Validate(candidate);
        var blocking = issues.Where(i => i.Severity == Themes.ThemeIssueSeverity.Error).ToList();
        if (blocking.Count > 0)
        {
            LpLog.Warn($"the custom palette in preferences is invalid ({string.Join("；", blocking.Select(b => b.Message))}) -> fell back to the factory default theme", category: LogCategory);
            return ThemeCatalog.Default;
        }
        return candidate;
    }

    /// <summary>偏好里的字体 → 族名（导入文件已不存在时如实报告，但**保留**偏好条目）。</summary>
    /// <remarks>
    /// <para>
    /// <b>偏好为空 = "没选过" = 按当前语言给默认族</b>（决策 5）：用户显式选过的族跨语言不变
    /// （用户选择优先于语言），没选过的才跟着语言走。
    /// </para>
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

        // 空 = 没选过 → 当前语言的默认族（用户显式选过的族不会被这里改掉）
        var ui = string.IsNullOrWhiteSpace(pref.Ui) ? DefaultUiFont : pref.Ui.Trim();
        var mono = pref.Mono;
        if (!string.IsNullOrWhiteSpace(ui) && !available.Contains(ui))
        {
            reasons.Add($"the UI font '{ui}' is unavailable (file missing or not installed), fell back to the default font");
            ui = DefaultUiFont;
        }
        if (!string.IsNullOrWhiteSpace(mono) && !available.Contains(mono))
        {
            reasons.Add($"the monospace font '{mono}' is unavailable (file missing or not installed), fell back to the default font");
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
        // ⚠️ 语言登记也要回出厂缺省：留着上一轮的语言码会让"默认字体族"漏进下一个用例
        //    （同一类"测试结果取决于执行顺序"的偶发红）。
        ActiveLanguageCode = null;
        CurrentUiFont = DefaultUiFont;
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
