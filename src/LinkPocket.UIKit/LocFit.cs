using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using LinkPocket.I18n;
using LinkPocket.Theming.Fonts;

namespace LinkPocket.Views;

/// <summary>自适应策略（缺省 <see cref="Off"/> = 行为与引入本机制之前逐像素相同）。</summary>
public enum LocFitMode
{
    /// <summary>不参与自适应（<b>用户数据</b>用这一档：书签标题、URL、文件夹名永不参与取词、永不缩字号）。</summary>
    Off = 0,

    /// <summary>只缩字号到下限，不截断（放不下就让它画出去——给"几何本来就有余量"的面用）。</summary>
    Shrink = 1,

    /// <summary>完整降级链：base → 换短式 → 缩字号 → 短式+下限 → <c>CharacterEllipsis</c> 截断 + ToolTip 全文。</summary>
    ShrinkThenEllipsis = 2,
}

/// <summary>
/// <b>几何冻结 + 字号自适应</b>的唯一实现：界面文案在**既有几何内**自己找位置，
/// 绝不撑宽、绝不换行、绝不改 Padding（约束 B「英文零尺寸漂移」的可执行定义之一）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有它</b>：英文普遍比中文宽 30%~150%（「永久删除」4 字 ≈50px vs
/// <c>Delete permanently</c> 19 字母 ≈124px），而控件高度（48 处 <c>Height="32"</c>）与表格列宽
/// 都是冻结几何。放不下时只有两条路：改几何（禁止）或让文字自己让位（本机制）。
/// </para>
/// <para>
/// <b>降级链（顺序固定，不许跳步）</b>：① base 字号 → ② 换短式文案（"换一句更短的话"优先于
/// "把整块字缩小"：日期去年份比缩小整块更可读，也不会让相邻控件字号不齐）→ ③ 缩字号到下限
/// → ④ 短式 + 下限 → ⑤ <c>CharacterEllipsis</c> 截断 + ToolTip 全文。
/// </para>
/// <para>
/// <b>算法是纯函数</b>（<see cref="Fit"/>：文字 + 字体 + 可用宽 → 字号 + 形态 + 是否截断），
/// 只有 <c>FormattedText</c> 那一层碰 WPF；测试因此可以注入 <see cref="Metrics"/>
/// 直接跑完整降级链，不依赖真实渲染。
/// </para>
/// <para>
/// <b>它是纯布局机制，一个字都不写进元素的 Text</b>：文字的唯一写者是<b>绑定</b>
/// （<c>Text="{loc:Fit …}"</c> 的转换器）。<see cref="TryFit"/> 只写字号与截断/ToolTip。
/// 历史教训：让本行为去写 <c>TextBlock.Text</c>，等于让一个属性有两个写者——
/// 绑定重投的值被行为按上一次的判定盖住，切语言后模型已是新语言、屏幕上还留着旧语言
/// （实测：模型侧 <c>09/20/2026 10:50 AM</c>、屏幕上 <c>2026-09-20 10:50</c>）。
/// </para>
/// </remarks>
public static class LocFit
{
    /// <summary>字号下限比例（相对该元素的基准字号）。</summary>
    public const double MinRatio = 0.75;

    /// <summary>字号绝对下限（pt）。下限放到 9.5 就要求降级链真的会走到"短式"那一步。</summary>
    public const double MinPoints = 9.5;

    /// <summary>字号搜索步长（pt）。</summary>
    public const double Step = 0.5;

    /// <summary>"刚好放不下"的容差（px）：小于它不算溢出，避免浮点抖动引发无谓的缩字号。</summary>
    public const double Slack = 0.5;

    // ── 度量 ───────────────────────────────────────────────────────────────

    /// <summary>一次度量的入参（文字 + 字体描述 + 字号 + DPI）。</summary>
    public readonly record struct MetricsRequest(
        string Text, string Family, double Size, FontWeight Weight, FontStretch Stretch, double PixelsPerDip);

    /// <summary>
    /// 排版宽度（设备无关像素）。生产实现走 <see cref="TextWidthProbe"/>（与真实排版同源，
    /// 也是离线文案预算工具用的那一份）；测试可注入假实现，从而在无渲染线程上跑完整降级链。
    /// </summary>
    public static Func<MetricsRequest, double> Metrics { get; set; } = DefaultMetrics;

    private static double DefaultMetrics(MetricsRequest request)
        => TextWidthProbe.Width(new TextWidthProbe.MetricsRequest(
            request.Text, request.Family, request.Size, request.Weight, request.Stretch, request.PixelsPerDip));

    // ── 纯算法 ─────────────────────────────────────────────────────────────

    /// <summary>一次自适应的结论。</summary>
    public readonly record struct FitResult(double Size, bool UseShort, bool Truncate);

    /// <summary>
    /// 降级链本体（纯函数）：在 <paramref name="available"/> 内挑最合适的字号与形态。
    /// </summary>
    /// <param name="full">全长文案（空 = 无文案，直接返回不动）。</param>
    /// <param name="shortText">短式文案；<c>null</c> = 这条文案没有短式变体。</param>
    /// <param name="baseSize">基准字号（元素本来要用的那个）。</param>
    /// <param name="available">可用宽（元素实测宽 − 内距）。</param>
    /// <param name="allowTruncate">到下限仍放不下时是否允许截断（<c>false</c> = 停在 <see cref="MinFloor"/> 档）。</param>
    /// <param name="desc">字体描述（族 / 字重 / 字宽 / DPI）。</param>
    public static FitResult Fit(
        string? full,
        string? shortText,
        double baseSize,
        double available,
        bool allowTruncate,
        in FontDescriptor desc)
    {
        var size = Math.Max(baseSize, MinPoints);
        if (string.IsNullOrEmpty(full)) return new FitResult(size, UseShort: false, Truncate: false);

        var floor = MinFloor(baseSize);
        // 可用宽为 0 或负（还没参与布局 / 被压成 0）＝ 一点位置都没有：直接收敛到下限。
        // 调用方（转换器 / TryFit）另有"宽度为 0 时先不判定"的守卫，不会因为这条过早把稳定态钉死。
        if (available <= 0) return new FitResult(floor, UseShort: false, Truncate: allowTruncate);

        var hasShort = !string.IsNullOrEmpty(shortText);

        // ① 全长 @ base —— 绝大多数中文文案与短英文文案走到这里就结束
        if (Fits(full!, size, available, desc)) return new FitResult(size, UseShort: false, Truncate: false);

        // ② 短式 @ base —— "换一句更短的话"优先于"把字缩小"（日期/计数这类结构化文本的出路）
        if (hasShort && Fits(shortText!, size, available, desc)) return new FitResult(size, UseShort: true, Truncate: false);

        // ③④ 从 base 往下取"能放下的最大档"：取最大（而不是逐档试到第一个能放下）
        //     让结果是稳定解——重算必然得到同一个值，布局回环因此不成立。
        for (var candidate = SnapDown(size - Step); candidate > floor; candidate = SnapDown(candidate - Step))
        {
            var text = hasShort && Fits(shortText!, candidate, available, desc) ? shortText : null;
            if (text != null || Fits(full!, candidate, available, desc))
                return new FitResult(candidate, UseShort: text != null, Truncate: false);
        }

        // ⑤ 触底：有短式就"用短式再截断"（同样宽度下短式留得住更多信息）
        return new FitResult(floor, UseShort: hasShort, Truncate: allowTruncate);
    }

    /// <summary>该元素在当前字号下的可用宽（实测宽 − 内距；负值收敛到 0）。</summary>
    public static double AvailableWidth(FrameworkElement element)
    {
        var width = element.ActualWidth;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return 0;
        return Math.Max(0, width - HorizontalInsets(element));
    }

    /// <summary>左右内距之和（含描边）：可用宽必须减掉它，否则文字会被内距挤出去。</summary>
    public static double HorizontalInsets(FrameworkElement element)
    {
        var insets = 0.0;
        if (element is Control control)
        {
            insets += control.Padding.Left + control.Padding.Right;
            insets += control.BorderThickness.Left + control.BorderThickness.Right;
        }
        if (element is Border border)
        {
            insets += border.Padding.Left + border.Padding.Right;
            insets += border.BorderThickness.Left + border.BorderThickness.Right;
        }
        return insets;
    }

    /// <summary>字号下限：<c>max(base×0.75, 9.5pt)</c>。</summary>
    public static double MinFloor(double baseSize) => Math.Max(baseSize * MinRatio, MinPoints);

    /// <summary>按步长取整（向下，落在 0.5pt 网格上）。</summary>
    public static double SnapDown(double size) => Math.Floor(size / Step + 1e-6) * Step;

    private static bool Fits(string text, double size, double available, in FontDescriptor desc)
        => Metrics(new MetricsRequest(text, desc.Family, size, desc.Weight, desc.Stretch, desc.PixelsPerDip)) <= available + Slack;

    /// <summary>字体描述（度量缓存的键；族 / 字重 / 字宽 / DPI 任一变化都算另一次度量）。</summary>
    public readonly record struct FontDescriptor(string Family, FontWeight Weight, FontStretch Stretch, double PixelsPerDip)
    {
        public override string ToString()
            => $"{Family}|{Weight.ToOpenTypeWeight()}|{Stretch.ToOpenTypeStretch()}|{PixelsPerDip:F2}";
    }

    /// <summary>读一个元素的字体描述（度量与缓存的入参）。</summary>
    public static FontDescriptor Describe(FrameworkElement element)
    {
        var family = element.GetValue(TextElement.FontFamilyProperty) as FontFamily;
        var weight = element.GetValue(TextElement.FontWeightProperty) is FontWeight w ? w : FontWeights.Normal;
        var stretch = element.GetValue(TextElement.FontStretchProperty) is FontStretch s ? s : FontStretches.Normal;
        var pixelsPerDip = element is Visual ? VisualTreeHelper.GetDpi(element).PixelsPerDip : 1.0;
        return new FontDescriptor(family?.Source ?? string.Empty, weight, stretch, pixelsPerDip);
    }

    /// <summary>元素当前生效的字号（<c>FontSize</c> 是 <see cref="TextElement"/> 的继承附加属性）。</summary>
    public static double FontSizeOf(DependencyObject element)
        => element.GetValue(TextElement.FontSizeProperty) is double size ? size : double.NaN;

    // ── 度量缓存 ───────────────────────────────────────────────────────────

    private const int CacheLimit = 4096;

    /// <summary>
    /// <c>(文案, 可用宽, 基准字号, 字体描述, 是否允许截断) → 结论</c>。
    /// 键里已经含全部输入，因此不需要任何失效逻辑：字体换了、语言换了、宽度变了都是新键。
    /// </summary>
    private static readonly Dictionary<string, FitResult> Cache = new(StringComparer.Ordinal);

    /// <summary>清缓存（测试与"整个世界都变了"的场景用；键已含全部输入，正常路径不需要调）。</summary>
    public static void ClearCache()
    {
        Cache.Clear();
        FitEvaluations = 0;
        FontSizeWrites = 0;
    }

    /// <summary>自适应计算次数（诊断读数；用例用它证明收敛后不再计算）。</summary>
    public static int FitEvaluations { get; private set; }

    /// <summary>
    /// 字号写入次数（诊断读数）。<b>稳态下必须停止增长</b>——它就是"值不变不写"这条回环防线的
    /// 可观测判据：布局一直跑、这个数一直不涨，才说明没有 measure→写值→再 measure 的回环。
    /// </summary>
    public static int FontSizeWrites { get; private set; }

    /// <summary>带缓存求值（键含全部输入 ⇒ 不需要失效逻辑）。</summary>
    public static FitResult Evaluate(
        string full, string? shortText, double baseSize, double available, bool allowTruncate, in FontDescriptor desc)
    {
        var key = string.Create(CultureInfo.InvariantCulture,
            $"{desc}|{baseSize:F2}|{available:F2}|{(allowTruncate ? 1 : 0)}|{shortText ?? "\u0000"}|{full}");
        if (Cache.TryGetValue(key, out var cached)) return cached;

        if (Cache.Count >= CacheLimit) Cache.Clear();   // 有界：满了整体清，不引 LRU 的复杂度
        FitEvaluations++;
        var result = Fit(full, shortText, baseSize, available, allowTruncate, desc);
        Cache[key] = result;
        return result;
    }

    // ── 每元素状态 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 每元素自适应状态。<b>弱键指向元素本身、值里绝不反向持有元素</b>
    /// （否则这个表会变成"键弱、值强"的常驻泄漏——本仓踩过一次同类坑，见 <c>WARNINGS</c> 92）。
    /// </summary>
    private sealed class FitState
    {
        /// <summary>基准字号是否已经锁定过一次（锁定前不许写任何值）。</summary>
        public bool Seeded;

        /// <summary>元素自己的基准字号（第一次拿到真实可用宽时锁定；外部改字号会让它重新学习）。</summary>
        public double BaseSize;

        /// <summary>本行为上一次写进去的字号（用于区分"外部改的"与"自己写的"）。</summary>
        public double WrittenSize;

        /// <summary>文字生成侧上一次选了哪种形态（只读状态，供布局侧决定截断与 ToolTip）。</summary>
        public bool UseShort;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, FitState> States = new();

    private static FitState StateOf(FrameworkElement element)
        => States.TryGetValue(element, out var state) ? state : States.GetValue(element, _ => new FitState());

    // ── 两个附加属性：接入通道 + 当前该显示的文本 ─────────────────────────

    /// <summary>
    /// 接入通道：绑 <see cref="LocText"/>（或 <c>string</c>，或文案键），
    /// 并触发 <see cref="ChosenProperty"/> 的重算。
    /// </summary>
    /// <remarks>
    /// 它与 <see cref="ModeProperty"/> 是<b>并列的一条通道</b>：
    /// <c>loc:LocFit.Text="{loc:FitValue ModifiedText}" loc:LocFit.Mode="ShrinkThenEllipsis"</c>。
    /// XAML 的属性顺序不保证，所以谁后到都要能把链路接起来——两边都调 <see cref="Wire"/>。
    /// </remarks>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text", typeof(object), typeof(LocFit),
            new PropertyMetadata(null, (d, _) => { if (d is FrameworkElement fe) Wire(fe); }));

    /// <summary>
    /// <b>当前该显示的那一形态</b>——文字的唯一来源；模板把它绑到元素的 <c>Text</c>：
    /// <c>Text="{Binding Path=(views:LocFit.Chosen), RelativeSource={RelativeSource Self}}"</c>。
    /// </summary>
    /// <remarks>
    /// 为什么需要它这一层：形态（全长 / 短式）要在<b>布局拿到可用宽之后</b>才定，
    /// 而绑定求值发生在布局之前（此刻 <c>ActualWidth</c> 还是 0）。于是把"文字"放进一个
    /// **附加属性**、由布局侧按需改写它，模板那条绑定只管把它画出来 ——
    /// 这样仍然是"绑定 → 元素文本"一条路，没有第二个写者去碰 <c>TextBlock.Text</c>。
    /// </remarks>
    public static readonly DependencyProperty ChosenProperty =
        DependencyProperty.RegisterAttached(
            "Chosen", typeof(string), typeof(LocFit), new PropertyMetadata(string.Empty));

    public static void SetText(DependencyObject element, object? value) => element.SetValue(TextProperty, value);
    public static object? GetText(DependencyObject element) => element.GetValue(TextProperty);
    public static string GetChosen(DependencyObject element) => (string)element.GetValue(ChosenProperty);

    // ── 语言版本 → 强制重取 ────────────────────────────────────────────────

    static LocFit()
    {
        // 主题 / 字体一变，基准字号与度量缓存都可能失效：清掉每元素状态让它们重新学一次
        // （字体族是度量缓存键的一部分，所以缓存本身不需要清）。
        Theming.ThemeService.Changed += (_, _) => States.Clear();

        // 语言一变：**强制重取**所有已接入通道的元素的文案绑定。
        //
        // 为什么非要自己驱动：实测（探针 `fitchan` 套件）——把 `{loc:FitValue X}` 挂到
        // `LocFit.Text` 之后，那条 MultiBinding 的两路都正确（`Version@LocTable` +
        // `X@DataContext`）、版本号也确实递增了，但 **WPF 不会因为子绑定变化而重算它**：
        // 屏幕上一直留着上一种语言的文本，而手动 `UpdateTarget()` 立刻就能拿到当前语言
        // （实测 `2026-09-20 10:50` → `09/20 10:50 AM`）。
        // 这条与"模型成员是普通属性、不发通知"是同一族问题：**失效信号不会自己传到底**，
        // 必须由拥有这条通道的那一层显式驱动一次。
        Loc.Table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LocTable.Version)) return;

            // ⚠️ 延迟到**本次派发之后**再驱动，不要在通知里同步做：
            // 通知是在"语言状态刚写入"的那一瞬发出的，此刻整棵可视树与绑定链还在用旧语言的值，
            // 同步驱动会当场读到旧文本、把陈旧形态又写回去（实测：同步重取后屏幕上仍是旧语言，
            // 而同一格随后手动再驱动一次就正常了——这正是"时机"而不是"通道"的问题）。
            // 排到 Background 优先级：等当前这一批输入/绑定/布局消息都跑完，语言切换真正落地。
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) { RefreshAll(); return; }
            dispatcher.BeginInvoke(new Action(RefreshAll), DispatcherPriority.Background);
        };
    }

    /// <summary>
    /// 让所有已接入通道的元素<b>重取一遍文案绑定</b>（取词表版本变化时由 <see cref="LocFit"/> 调用）。
    /// </summary>
    /// <remarks>
    /// <c>BindingOperations.GetMultiBindingExpression(...)?.UpdateTarget()</c> 就是"重新读一次来源"，
    /// 不改动绑定本身；元素被回收后自然不在枚举里（<see cref="States"/> 是弱表）。
    /// 跨线程调用（单测可能从非 UI 线程 <c>LocaleService.Apply</c>）按 <c>CheckAccess</c> 跳过——
    /// 跨线程碰绑定会当场抛"调用线程无法访问此对象"。
    /// </remarks>
    public static void RefreshAll()
    {
        var elements = new List<FrameworkElement>();
        foreach (var entry in States)
            if (entry.Key is FrameworkElement fe) elements.Add(fe);

        RefreshRuns++;
        RefreshElements += elements.Count;

        foreach (var fe in elements)
        {
            if (!fe.Dispatcher.CheckAccess()) continue;
            var multi = BindingOperations.GetMultiBindingExpression(fe, TextProperty);
            if (multi is not null)
            {
                multi.UpdateTarget();
                RefreshMultiHit++;
                AfterRefreshProbe?.Invoke(fe);
                continue;
            }
            RefreshMultiMiss++;
            BindingOperations.GetBindingExpression(fe, TextProperty)?.UpdateTarget();
        }

        foreach (var fe in elements)
        {
            if (fe.Dispatcher.CheckAccess()) TryFit(fe);
        }
    }

    /// <summary>重取时拿到 MultiBinding 的元素数（诊断读数）。</summary>
    public static int RefreshMultiHit { get; private set; }

    /// <summary>重取时**没**拿到 MultiBinding 的元素数（诊断读数：它大就说明绑定不在预期位置上）。</summary>
    public static int RefreshMultiMiss { get; private set; }

    /// <summary>诊断钩子：每次重取后对每个元素调用一次（探针用它看"重取当场读回了什么"）。</summary>
    public static Action<FrameworkElement>? AfterRefreshProbe { get; set; }

    /// <summary>语言版本驱动重取的次数（诊断读数：它必须随切语言增长，否则钩子没接上）。</summary>
    public static int RefreshRuns { get; private set; }

    /// <summary>历次重取触及的元素数合计（诊断读数：为 0 说明元素根本没登记进弱表）。</summary>
    public static int RefreshElements { get; private set; }

    /// <summary>弱表里当前登记的元素数（诊断读数）。</summary>
    public static int TrackedCount
    {
        get
        {
            var count = 0;
            foreach (var _ in States) count++;
            return count;
        }
    }

    /// <summary>这个元素有没有登记进弱表（诊断读数：没登记就永远不会被重取）。</summary>
    public static bool IsTracked(DependencyObject element)
        => element is FrameworkElement fe && States.TryGetValue(fe, out _);

    /// <summary>接入文案通道并立刻算一次形态（幂等；文字与 Mode 谁后到都走这里）。</summary>
    private static void Wire(FrameworkElement element)
    {
        var mode = GetMode(element);
        if (mode == LocFitMode.Off) return;
        element.LayoutUpdated -= OnLayoutUpdated;   // 先摘后挂：避免重复订阅
        element.LayoutUpdated += OnLayoutUpdated;
        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        element.Loaded -= OnLoaded;
        element.Loaded += OnLoaded;
        _ = StateOf(element);
        TryFit(element);
    }

    /// <summary>
    /// 学一次基准字号（只在首次拿到真实可用宽、或外部改过字号时锁定）。
    /// </summary>
    /// <remarks>
    /// 判据 = "当前字号与本行为上次写进的不一致" ⇒ 说明它是别人给的（样式 / XAML / 用户选择），
    /// 那就认它当基准。这条让"外部改字号"自动重新学习，不需要订阅任何字体属性变化通知。
    /// </remarks>
    public static double BaseSizeFor(FrameworkElement element, double fallback = 13)
    {
        var state = StateOf(element);
        var current = FontSizeOf(element);
        if (double.IsNaN(current) || current <= 0) current = fallback;
        if (!state.Seeded || Math.Abs(current - state.WrittenSize) > 1e-6)
        {
            state.BaseSize = current;
            state.Seeded = true;
            state.WrittenSize = current;   // 认下这个值：它此刻就是"元素本来要用的字号"
        }
        return state.BaseSize;
    }

    /// <summary>把字号落到元素上（值不变不写 = 回环防线；返回是否真的写了）。</summary>
    public static bool PlaceFontSize(FrameworkElement element, double size)
    {
        if (Math.Abs(FontSizeOf(element) - size) <= 1e-6) return false;
        element.SetValue(TextElement.FontSizeProperty, size);
        StateOf(element).WrittenSize = size;
        FontSizeWrites++;
        return true;
    }

    /// <summary>
    /// 文字生成的一侧（<c>{loc:Fit …}</c> 的转换器）用它把"这次选了什么形态"记下来，
    /// 供 <see cref="TryFit"/> 决定截断与 ToolTip。<b>只读状态，不是写通道</b>。
    /// </summary>
    public static void RecordVariant(FrameworkElement element, bool useShort)
        => StateOf(element).UseShort = useShort;

    /// <summary>本元素上一次生成的形态是不是短式（诊断与用例读数）。</summary>
    public static bool IsShortForm(FrameworkElement element)
        => States.TryGetValue(element, out var state) && state.UseShort;

    /// <summary>
    /// 本元素当前是不是"文字被截断"：判据 = 最终画出来的那段文字<b>在最终字号下确实放不进可用宽</b>。
    /// </summary>
    public static bool IsTruncated(FrameworkElement element)
    {
        var applied = GetChosen(element);
        if (string.IsNullOrEmpty(applied)) return false;
        var available = AvailableWidth(element);
        return available > 0 && !Fits(applied, FontSizeOf(element), available, Describe(element));
    }

    // ── 布局侧：只写字号与截断 ─────────────────────────────────────────────

    /// <summary>
    /// 就当前文字与可用宽重算一次并落**字号 / 截断 / ToolTip**（<b>不碰文字</b>）。
    /// 幂等：稳态下重算得到同一个结论、不写任何属性（"值不变不写"——布局回环的唯一防线）。
    /// </summary>
    public static void TryFit(FrameworkElement element)
    {
        var mode = GetMode(element);
        if (mode == LocFitMode.Off) return;

        if (Normalize(GetText(element)) is not LocText text || text.IsEmpty)
        {
            if (GetChosen(element).Length > 0) element.SetValue(ChosenProperty, string.Empty);
            return;
        }

        // 可用宽为 0 = 还没参与布局：<b>先只落全长</b>，不动字号。
        // 在这里缩字号会把字号钉在下限上，而"基准字号"就照着压过的值学错了（一次就再也回不去）。
        var available = AvailableWidth(element);
        if (available <= 0)
        {
            var plain = text.Resolve();
            if (!string.Equals(GetChosen(element), plain, StringComparison.Ordinal))
                element.SetValue(ChosenProperty, plain);
            return;
        }

        var baseSize = BaseSizeFor(element);
        var desc = Describe(element);
        var result = Evaluate(text.Resolve(), text.HasShort ? text.ResolveShort() : null,
            baseSize, available, mode == LocFitMode.ShrinkThenEllipsis, desc);

        PlaceFontSize(element, result.Size);   // 值不变不写（回环防线）
        RecordVariant(element, result.UseShort);

        var shown = result.UseShort ? text.ResolveShort() : text.Resolve();
        if (!string.Equals(GetChosen(element), shown, StringComparison.Ordinal))
            element.SetValue(ChosenProperty, shown);

        ApplyOverflow(element, shown, result.Size, available, desc);
    }

    /// <summary>把接入通道的值归一成 <see cref="LocText"/>（<c>string</c> = 只有全长；认不出就拒绝）。</summary>
    private static LocText? Normalize(object? value) => value switch
    {
        LocText text => text,
        string raw when raw.Length > 0 => LocText.Of(LocValue.Literal(raw)),
        _ => null,
    };

    /// <summary>
    /// 触底之后的收尾：<b>自己判"到底截没截"</b>（按最终写入的字号重算一次），
    /// 而不是照抄链路标记——那个标记只说明"到下限仍放不下"，真正会不会画出省略号还取决于渲染。
    /// 截断与全文提示<b>成对出现</b>：截了就必须给得出全文，没截就不加提示。
    /// </summary>
    public static void ApplyOverflow(
        FrameworkElement element, string text, double size, double available, in FontDescriptor desc)
    {
        var truncate = available > 0 && !Fits(text, size, available, desc);
        var wanted = truncate ? TextTrimming.CharacterEllipsis : TextTrimming.None;

        if (element is TextBlock block && block.TextTrimming != wanted)
            block.TextTrimming = wanted;

        var tip = truncate ? text : null;
        if (!ReferenceEquals(ToolTipService.GetToolTip(element), tip))
            ToolTipService.SetToolTip(element, tip);
    }

    // ── 附加属性 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 自适应策略（<b>文字由绑定写，本属性只管布局侧的行为</b>）。
    /// </summary>
    /// <remarks>
    /// 用法：<c>Text="{loc:Fit some.key}" loc:LocFit.Mode="ShrinkThenEllipsis"</c>。
    /// 转换器负责选形态并落字号，本属性负责挂布局事件、在尺寸变化后重算字号与截断。
    /// </remarks>
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached(
            "Mode", typeof(LocFitMode), typeof(LocFit),
            new PropertyMetadata(LocFitMode.Off, OnModeChanged));

    public static void SetMode(DependencyObject element, LocFitMode value) => element.SetValue(ModeProperty, value);
    public static LocFitMode GetMode(DependencyObject element) => (LocFitMode)element.GetValue(ModeProperty);

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if ((LocFitMode)e.NewValue == LocFitMode.Off)
        {
            States.Remove(element);
            element.LayoutUpdated -= OnLayoutUpdated;
            element.SizeChanged -= OnSizeChanged;
            element.Loaded -= OnLoaded;
            element.SetValue(ChosenProperty, string.Empty);
            return;
        }

        // ⚠️ 此刻元素常常还没参与布局（可用宽 0）：Wire 里的 TryFit 只会落一个全长、不动字号，
        // 基准字号留到首次拿到真实可用宽时再学（否则会照着被压过的下限值学错，一次就回不去）。
        Wire(element);
    }

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement element) TryFit(element);
    }

    /// <summary>
    /// 尺寸真的变了 = 元素**已经参与过布局**的最确定信号（<c>ActualWidth</c> 此刻可用）。
    /// </summary>
    /// <remarks>
    /// 三个触发源的分工（都必要，不是冗余）：
    /// <list type="bullet">
    /// <item><c>SizeChanged</c> —— 主触发：元素首次拿到尺寸、窗口缩放、容器变窄都走它；</item>
    /// <item><c>Loaded</c> —— 元素进可视树那一刻兜一次（此时可能还没拿到尺寸，会被早退挡掉）；</item>
    /// <item><c>LayoutUpdated</c> —— 已布局元素的**后排变化**（兄弟撑开、列宽变了但自身尺寸没变）。</item>
    /// </list>
    /// ⚠️ 依赖 <c>LayoutUpdated</c> 单独成事是不行的：它由布局管理器在回合结束时排入，
    /// 对一个<b>新增</b>的元素不保证重新派发（实测：只在首次布局时来一次）。
    /// </remarks>
    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element) TryFit(element);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) TryFit(element);
    }
}

/// <summary>
/// 自适应文案通道的转换器：<c>Text="{loc:Fit …}"</c> 背后的那一步。
/// </summary>
/// <remarks>
/// <para>
/// <b>它是文字的唯一写者</b>：binding 求出"该显示哪一形态"的字符串，同时把字号落到目标元素上。
/// 形态（全长/短式）与字号（能不能放下）必须一起决定 —— 分给两个写者就会出现
/// "绑定按新语言投了值、布局按旧判定又改回去"的互相覆盖（这条正是踩过的坑）。
/// </para>
/// <para>
/// <b>为什么能拿到元素</b>：<see cref="LocFitExtension"/> 在 markup extension 阶段用
/// <c>IProvideValueTarget</c> 拿到目标元素，塞进 <c>ConverterParameter</c>。
/// </para>
/// <para>
/// 输入：<c>[0]</c> = 语言版本（<b>仅作失效触发器</b>：语言一变整条链自动重算）、
/// <c>[1]</c> = <see cref="LocText"/>（全长 + 可选短式）。
/// </para>
/// </remarks>
public sealed class LocFitResolver : IMultiValueConverter
{
    public static LocFitResolver Instance { get; } = new();

    public object? Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is not LocText text || text.IsEmpty) return string.Empty;

        var element = parameter as FrameworkElement;
        var mode = element is null ? LocFitMode.Off : LocFit.GetMode(element);
        if (element is null || mode == LocFitMode.Off) return text.Resolve();   // 未启用：全长、不动字号

        var available = LocFit.AvailableWidth(element);
        var desc = LocFit.Describe(element);
        var baseSize = LocFit.BaseSizeFor(element);
        var result = LocFit.Evaluate(text.Resolve(), text.HasShort ? text.ResolveShort() : null,
            baseSize, available, mode == LocFitMode.ShrinkThenEllipsis, desc);

        // 要素还没参与布局（可用宽 0）就不写字号：等布局后的 TryFit 重算——
        // 在这里写会把字号钉在下限上，而"基准字号"就照着压过的值学错了。
        if (available > 0) LocFit.PlaceFontSize(element, result.Size);

        LocFit.RecordVariant(element, result.UseShort);
        var shown = result.UseShort ? text.ResolveShort() : text.Resolve();
        LocFit.ApplyOverflow(element, shown, result.Size, available, desc);
        return shown;
    }

    public object[]? ConvertBack(object? value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException("resolution is one-way: displayed text is not state.");

    /// <summary>
    /// 代码侧建"自适应文案"绑定（XAML 的 <c>Text="{loc:Fit …}"</c> 在同一件事上的等价物）。
    /// </summary>
    /// <remarks>
    /// 单元格工厂（代码建行）借不到 markup extension 的目标元素，所以由调用方把元素显式传进来。
    /// <b>路径 <c>"."</c> = 绑定源就是 DataContext 本身</b>——先把 <see cref="LocText"/> 放进
    /// <c>DataContext</c> 再走这条，可以给"值本身而不是某个成员"上自适应。
    /// </remarks>
    /// <param name="element">目标元素（转换器要从它读可用宽、把字号写回去）。</param>
    /// <param name="path">模型成员路径，或 <c>"."</c>。</param>
    /// <param name="source">显式绑定源；<c>null</c> = 用元素的 <c>DataContext</c>。</param>
    public static MultiBinding BuildBinding(FrameworkElement element, string path, object? source = null)
    {
        var mb = new MultiBinding
        {
            Converter = Instance,
            Mode = BindingMode.OneWay,
        };
        mb.Bindings.Add(new Binding(nameof(LocTable.Version))
        {
            Source = LocTable.Instance,
            Mode = BindingMode.OneWay,
        });
        mb.Bindings.Add(source is null
            ? new Binding(path) { Mode = BindingMode.OneWay }
            : new Binding(path) { Source = source, Mode = BindingMode.OneWay });
        return mb;
    }

    /// <summary>
    /// 建一个"两个长度形态"的文本元素（日期 / 计数这类**结构化数据**：放不下时换短式 + 截断，不缩字号）。
    /// </summary>
    /// <param name="text">文案值（两个形态都在里面）。</param>
    /// <param name="fontSize">基准字号。</param>
    /// <param name="mode">自适应策略（缺省 = 只换短式与截断）。</param>
    public static TextBlock BuildCell(LocText text, double fontSize = 12.5, LocFitMode mode = LocFitMode.ShrinkThenEllipsis)
    {
        var cell = new TextBlock
        {
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center,
            DataContext = text,
        };
        if (mode == LocFitMode.Off) { cell.Text = text.Resolve(); return cell; }

        LocFit.SetMode(cell, mode);
        LocFit.SetText(cell, text);   // 接入通道：文字与形态都由本机制管
        cell.SetBinding(TextBlock.TextProperty, BuildChosenBinding());
        return cell;   // 截断与全文提示成对出现：由 LocFit.ApplyOverflow 在布局后决定写不写
    }

    /// <summary>
    /// 把元素的文本绑到 <see cref="LocFit.ChosenProperty"/> ——
    /// 模板侧那条 <c>Text="{Binding Path=(views:LocFit.Chosen), RelativeSource={RelativeSource Self}}"</c>
    /// 的代码版（单元格工厂用）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 路径必须由 <see cref="DependencyProperty"/> 实例构造，<b>不能写成字符串</b>：
    /// 附加属性的字符串路径要带 XAML 命名空间前缀（<c>(views:LocFit.Chosen)</c>），
    /// 而代码建的绑定没有命名空间作用域，解析不到就<b>静默地一个字都不画</b>
    /// （实测：<c>Chosen</c> 已正确落值、屏幕上却是空串）。
    /// </remarks>
    public static Binding BuildChosenBinding()
    {
        var binding = new Binding
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.Self),
            Mode = BindingMode.OneWay,
        };
        binding.Path = new PropertyPath(LocFit.ChosenProperty);
        return binding;
    }
}

/// <summary>
/// 自适应文案 markup extension：<c>Text="{loc:Fit some.key}"</c> /
/// <c>Text="{loc:Fit ModelMember}"</c>。
/// </summary>
/// <remarks>
/// <para>
/// 一条 <c>path</c> 两种用法，由<b>路径能不能解析成模型成员</b>决定走哪条：
/// <list type="bullet">
/// <item>能解析（<c>{loc:Fit ModifiedText}</c>）—— 取模型上的 <see cref="LocText"/>（两个长度形态都在里面）；</item>
/// <item>不能解析（<c>{loc:Fit appearance.card.title}</c>）—— 当作文案键，短式键按 <c>key#short</c> 约定推。</item>
/// </list>
/// 之所以不做成两个扩展（<c>Fit</c> / <c>FitValue</c>）：调用点只有"一个成员名或一个键"这一件事，
/// 分成两个只会让写的人多一次判断，而判错的后果（键被当成员）是静默的。
/// </para>
/// <para>
/// <b>产物是 <c>MultiBinding</c></b>：第一路挂 <c>LocTable.Version</c> 当失效触发器
/// （模型成员是普通属性、不发通知，没有这一路切语言就不会重读），第二路是真值。
/// </para>
/// </remarks>
[System.Windows.Markup.MarkupExtensionReturnType(typeof(object))]
public sealed class LocFitExtension : System.Windows.Markup.MarkupExtension
{
    public LocFitExtension() { }

    public LocFitExtension(string path) => Path = path;

    [System.Windows.Markup.ConstructorArgument("path")]
    public string? Path { get; set; }

    /// <summary>显式短式键（只对"字面键"那种用法有意义）；缺省 = <c>Path + "#short"</c>。</summary>
    public string? ShortKey { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Path)) return string.Empty;

        var element = (serviceProvider?.GetService(typeof(System.Windows.Markup.IProvideValueTarget))
                          as System.Windows.Markup.IProvideValueTarget)?.TargetObject as FrameworkElement;

        var mb = new MultiBinding { Converter = LocFitResolver.Instance, Mode = BindingMode.OneWay };
        mb.Bindings.Add(new Binding(nameof(LocTable.Version))
        {
            Source = LocTable.Instance,
            Mode = BindingMode.OneWay,
        });

        var member = element is null ? null : ResolveMember(element, Path!);
        if (member is not null)
        {
            mb.Bindings.Add(new Binding(Path) { Mode = BindingMode.OneWay });
            mb.ConverterParameter = element;
            return mb;
        }

        // 不是模型成员 ⇒ 当文案键（短式键按约定推，有没有由表决定）
        mb.Bindings.Add(new Binding { Source = LocText.Key(Path!, ShortKey ?? Path + Loc.ShortSuffix), Mode = BindingMode.OneWay });
        mb.ConverterParameter = element;
        return mb;
    }

    /// <summary>目标元素上有没有这个成员（有 ⇒ 走"模型给 LocText"那条；判错方向的代价见类型注释）。</summary>
    private static object? ResolveMember(FrameworkElement element, string path)
    {
        var property = element.DataContext?.GetType().GetProperty(path);
        return property is null ? null : element;
    }
}
