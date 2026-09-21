using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LinkPocket.I18n;
using LinkPocket.Theming;
using LinkPocket.Theming.Fonts;

namespace LinkPocket.Views;

/// <summary>自适应策略（缺省 <see cref="Off"/> = 行为与引入本机制之前逐像素相同）。</summary>
public enum LocFitMode
{
    /// <summary>不参与自适应。<b>用户数据与表格行内一律用这一档</b>（行内同行字号必须一致，且受 10k 性能门槛约束）。</summary>
    Off = 0,

    /// <summary>只缩字号到下限，不截断（放不下就让它画出去——给"几何本来就有余量"的面用）。</summary>
    Shrink = 1,

    /// <summary>完整降级链：base → 缩字号 → 换短式 → <c>CharacterEllipsis</c> 截断 + ToolTip 全文。</summary>
    ShrinkThenEllipsis = 2,
}

/// <summary>
/// <b>几何冻结 + 字号自适应</b>的唯一实现：界面文案在**既有几何内**自己找位置，
/// 绝不撑宽、绝不换行、绝不改 Padding（约束 B「英文零尺寸漂移」的可执行定义）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有它</b>：英文普遍比中文宽 30%~150%（「永久删除」4 字 ≈50px vs
/// <c>Delete permanently</c> 19 字母 ≈124px），而控件高度（48 处 <c>Height="32"</c>）与
/// 表格列宽都是冻结几何。放不下时只有两条路：改几何（禁止）或让文字自己让位（本机制）。
/// </para>
/// <para>
/// <b>降级链（顺序固定，不许跳步）</b>：① base 字号 →（中文侧/放得下时到此为止）
/// ② 换短式文案 → ③ 缩字号到下限 → ④ 短式 + 下限 → ⑤ <c>CharacterEllipsis</c> 截断 + ToolTip 全文。
/// 第 ② 步排在缩字号<b>之前</b>：短式是"同一件事的另一种写法"（日期去年份），
/// 比"把整块字缩小"更可读，也不会让相邻控件字号不齐。
/// </para>
/// <para>
/// <b>算法是纯函数</b>（<see cref="Fit"/>：文字 + 字体 + 可用宽 → 字号 + 是否截断），
/// 只有 <c>FormattedText</c> 那一层碰 WPF；测试因此可以注入 <see cref="Metrics"/>
/// 直接跑完整降级链，不依赖真实渲染。
/// </para>
/// <para>
/// <b>唯一的性能/稳定性雷区 = 布局回环</b>：在布局过程中改 <c>FontSize</c> 会再触发一次布局。
/// 三道防线：① 判定与写入都在 <c>LayoutUpdated</c>（布局之后的通知），不在 Measure/Arrange 里；
/// ② 计算结果是<b>稳定解</b>——“取能放下的最大档”，重算必然得到同一个值；
/// ③ 写值前比一次，<b>值不变不写</b>。三条合起来保证回环在第一次就收敛（见 <c>LocFitTests</c> 的收敛用例）。
/// </para>
/// <para>
/// <b>可用宽度 = 元素实测宽 − 内距</b>，不是"某个祖先给我的约束"：实测宽就是 WPF 真正用来排这些字的那个宽度
/// （固定宽的元素 = 它的宽；自适应宽的元素 = 它自己长出来的宽，此时文字按定义放得下、结果恒为"不改"）。
/// 这样"缩字号"只可能发生在**几何真的被钉住**的地方，与"绝不改几何"是同一条判据。
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

    /// <summary>一次度量的入参（字体描述 + 字号 + 文字 + DPI）——与 <see cref="TextWidthProbe"/> 同形。</summary>
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
    /// <param name="desc">字体描述（族 / 字重 / 字宽）。</param>
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
        // 这里**不返回基准字号**：那会让"压到底"的元素一直画着放不下的字，而调用方（TryFit）
        // 另有"宽度为 0 时先不判定"的守卫，不会因为这条过早把稳定态钉死。
        if (available <= 0) return new FitResult(floor, UseShort: false, Truncate: allowTruncate);

        var hasShort = !string.IsNullOrEmpty(shortText);

        // ① 全长 @ base —— 绝大多数中文文案与短英文文案走到这里就结束
        if (Fits(full!, size, available, desc)) return new FitResult(size, UseShort: false, Truncate: false);

        // ② 短式 @ base —— "换一句更短的话"优先于"把字缩小"（日期/计数这类结构化文本的出路）
        if (hasShort && Fits(shortText!, size, available, desc)) return new FitResult(size, UseShort: true, Truncate: false);

        // ③④ 从 base 往下取"能放下的最大档"：取最大（而不是逐档试到第一个能放下）
        //     让结果是稳定解——重算必然得到同一个值，回环因此不成立。
        for (var candidate = SnapDown(size - Step); candidate > floor; candidate = SnapDown(candidate - Step))
        {
            var text = hasShort && Fits(shortText!, candidate, available, desc) ? shortText : null;
            if (text != null || Fits(full!, candidate, available, desc))
                return new FitResult(candidate, UseShort: text != null, Truncate: false);
        }

        // ⑤ 触底：短式 + 下限 → 截断。
        // 有短式时**用短式再截断**：同样宽度下短式留得住更多信息（"Delete perman…" 比 "Delete…" 差）。
        if (hasShort) return new FitResult(floor, UseShort: true, Truncate: allowTruncate);
        return new FitResult(floor, UseShort: false, Truncate: allowTruncate);
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

    /// <summary>字体描述（度量缓存的键；族/字重/字宽/DPI 任一变化都算另一次度量）。</summary>
    public readonly record struct FontDescriptor(string Family, FontWeight Weight, FontStretch Stretch, double PixelsPerDip)
    {
        public override string ToString()
            => $"{Family}|{Weight.ToOpenTypeWeight()}|{Stretch.ToOpenTypeStretch()}|{PixelsPerDip:F2}";
    }

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

    private static FitResult Evaluate(string full, string? shortText, double baseSize, double available, bool allowTruncate, in FontDescriptor desc)
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

    // ── 附加属性 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 文案通道（<b>本行为唯一允许的取词通道</b>）：绑 <see cref="LocText"/> 或 <c>string</c>，
    /// 由本行为把当前该显示的那一形态写进元素的文字属性。
    /// </summary>
    /// <remarks>
    /// 为什么不让 <c>{loc:Loc}</c> 直接绑到 <c>TextBlock.Text</c>：那条通道的产物是<b>一个字符串</b>，
    /// 而自适应需要随时在"全长 / 短式"两者之间换（长度随可用宽与字号变），
    /// 一次性字符串到不了第 ③ 步。所以取词权归本属性，<c>Text</c>/<c>Content</c> 由本行为写。
    /// <para>
    /// 用法：<c>loc:LocFit.Text="{loc:Fit appearance.card.title}"</c>（字面键）、
    /// <c>loc:LocFit.Text="{loc:FitKey LabelKey}"</c>（键来自模型）、
    /// <c>loc:LocFit.Text="{Binding Name}"</c>（模型直接给 <see cref="LocText"/>）。
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text", typeof(object), typeof(LocFit),
            new PropertyMetadata(null, OnTextChanged, CoerceText));

    /// <summary>自适应策略。</summary>
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached(
            "Mode", typeof(LocFitMode), typeof(LocFit),
            new PropertyMetadata(LocFitMode.Off, OnModeChanged));

    /// <summary>本行为上一次真正写进 Text/Content 的文本（用于"值不变不写"，也是回环防线的一部分）。</summary>
    private static readonly DependencyProperty AppliedTextProperty =
        DependencyProperty.RegisterAttached(
            "AppliedText", typeof(string), typeof(LocFit), new PropertyMetadata(null));

    /// <summary><c>string</c> 入参一律当"只有全长形态"；认不出的类型一律拒绝（不静默兜底成空串）。</summary>
    private static object? CoerceText(DependencyObject d, object? value) => value switch
    {
        null => null,
        LocText => value,
        string text => LocText.Of(LocValue.Literal(text)),
        _ => DependencyProperty.UnsetValue,
    };

    public static void SetText(DependencyObject element, object? value) => element.SetValue(TextProperty, value);
    public static object? GetText(DependencyObject element) => element.GetValue(TextProperty);

    public static void SetMode(DependencyObject element, LocFitMode value) => element.SetValue(ModeProperty, value);
    public static LocFitMode GetMode(DependencyObject element) => (LocFitMode)element.GetValue(ModeProperty);

    private static string? GetAppliedText(DependencyObject element) => (string?)element.GetValue(AppliedTextProperty);
    private static void SetAppliedText(DependencyObject element, string? value) => element.SetValue(AppliedTextProperty, value);

    // ── 每元素状态 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 每元素自适应状态。<b>弱键指向元素本身、值里绝不反向持有元素</b>
    /// （否则这个表会变成"键弱、值强"的常驻泄漏——本仓踩过一次同类坑，见 <c>WARNINGS</c> 92 的弱引用教训）。
    /// </summary>
    private sealed class FitState
    {
        /// <summary>基准字号是否已经锁定过一次（锁定前不许写任何值——见 <see cref="TryFit"/> 的早退）。</summary>
        public bool Seeded;

        /// <summary>元素自己的基准字号（第一次拿到真实可用宽时锁定；外部改字号会让它重新学习）。</summary>
        public double BaseSize;

        /// <summary>本行为上一次写进去的字号（用于区分"外部改的"与"自己写的"）。</summary>
        public double WrittenSize;

        public bool IsTruncated;
        public bool IsShort;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, FitState> States = new();

    private static FitState StateOf(FrameworkElement element)
        => States.TryGetValue(element, out var state) ? state : States.GetValue(element, _ => new FitState());

    static LocFit()
    {
        // 主题/字体一变，既有元素的基准字号与度量都可能失效（字体族是度量缓存键的一部分，
        // 但"元素当前的基准字号"得让它重新学一次）。只清状态，不做任何重排——
        // 重排由取词绑定（版本失效）与下一次布局照常驱动。
        ThemeService.Changed += (_, _) => States.Clear();
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if ((LocFitMode)e.NewValue == LocFitMode.Off)
        {
            States.Remove(element);
            element.LayoutUpdated -= OnLayoutUpdated;
            element.SizeChanged -= OnSizeChanged;
            element.Loaded -= OnLoaded;
            return;
        }
        element.LayoutUpdated -= OnLayoutUpdated;   // 先摘后挂：避免重复订阅
        element.LayoutUpdated += OnLayoutUpdated;
        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        element.Loaded -= OnLoaded;
        element.Loaded += OnLoaded;
        // ⚠️ **不在这里就自适应**：此刻元素常常还没参与布局（可用宽 0），
        // 一自适应就会把字号钉在下限上，而"基准字号"是照着这个被压过的值学的 —— 从此再也回不去。
        // 基准字号必须在**首次拿到真实可用宽**时学（见 TryFit），所以这里只挂订阅。
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        TryFit(element);
    }

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement element) TryFit(element);
    }

    /// <summary>
    /// 尺寸真的变了 = 这是元素**已经参与过布局**的最确定信号（<c>ActualWidth</c> 此刻可用）。
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

    // ── 自适应本体 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 就当前文字与可用宽重算一次并落值。<b>本方法是幂等的</b>：稳态下重算得到同一个结论、不写任何属性
    /// （这就是"值不变不写"——布局回环的唯一防线）。
    /// </summary>
    public static void TryFit(FrameworkElement element)
    {
        var mode = GetMode(element);
        if (mode == LocFitMode.Off) return;

        if (GetText(element) is not LocText text || text.IsEmpty) return;

        var state = StateOf(element);

        // 可用宽为 0 = 还没参与布局：<b>先什么都不做</b>。
        // 这里必须早退，否则会把字号压到下限、还会照着压过的值把"基准字号"学错（一次就再也回不去）。
        var available = AvailableWidth(element);
        if (available <= 0) return;

        // 学基准字号（只在首次拿到真实可用宽时锁定一次）：
        // 只有"没人显式给过（NaN）"或"外部改过（与本行为上次写的不一致）"才重新学。
        var current = FontSizeOf(element);
        if (double.IsNaN(current) || current <= 0) return;
        if (!state.Seeded || Math.Abs(current - state.WrittenSize) > 1e-6)
        {
            state.BaseSize = current;
            state.Seeded = true;
            state.WrittenSize = current;   // 认下这个值：它此刻就是"元素本来要用的字号"
        }

        var desc = Describe(element);
        var allowTruncate = mode == LocFitMode.ShrinkThenEllipsis;
        var result = Evaluate(text.Resolve(), text.HasShort ? text.ResolveShort() : null, state.BaseSize, available, allowTruncate, desc);

        Apply(element, state, text, result);
    }

    /// <summary>元素当前生效的字号（<c>FontSize</c> 是 <see cref="TextElement"/> 的继承附加属性，不在 <see cref="FrameworkElement"/> 上）。</summary>
    private static double FontSizeOf(DependencyObject element)
    {
        var value = element.GetValue(TextElement.FontSizeProperty);
        return value is double size ? size : double.NaN;
    }

    private static void Apply(FrameworkElement element, FitState state, in LocText text, in FitResult result)
    {
        // 文案：只有结论与上一次不同才写（表里两条 LocValue 各自取词，都活着）
        var wanted = result.UseShort ? text.ResolveShort() : text.Resolve();
        if (!string.Equals(GetAppliedText(element), wanted, StringComparison.Ordinal))
        {
            WriteText(element, wanted);
            SetAppliedText(element, wanted);
        }

        // 字号：值不变不写（回环防线 ③）
        if (Math.Abs(FontSizeOf(element) - result.Size) > 1e-6)
        {
            element.SetValue(TextElement.FontSizeProperty, result.Size);
            state.WrittenSize = result.Size;
            FontSizeWrites++;
        }

        // 截断 + ToolTip 全文：只在"状态真的翻转"时写，不做每帧赋值
        if (state.IsTruncated != result.Truncate)
        {
            state.IsTruncated = result.Truncate;
            element.SetValue(TextBlock.TextTrimmingProperty, result.Truncate ? TextTrimming.CharacterEllipsis : TextTrimming.None);
            if (result.Truncate) ToolTipService.SetToolTip(element, text.Resolve());
            else ToolTipService.SetToolTip(element, null);
        }
        else if (result.Truncate && ToolTipService.GetToolTip(element) is null)
        {
            // 截断态下 ToolTip 被外部清掉（如绑定换源）：补回全文——截断必须配全文，缺一不可
            ToolTipService.SetToolTip(element, text.Resolve());
        }

        state.IsShort = result.UseShort;
    }

    /// <summary>把结论写进元素真正承载文字的那个属性（TextBox / ContentControl / TextBlock 三选一）。</summary>
    private static void WriteText(FrameworkElement element, string value)
    {
        switch (element)
        {
            case TextBox box:
                box.Text = value;
                break;
            case ContentControl content:
                content.Content = value;
                break;
            case TextBlock block:
                block.Text = value;
                break;
        }
    }

    private static FontDescriptor Describe(FrameworkElement element)
    {
        var family = element.GetValue(TextElement.FontFamilyProperty) as FontFamily;
        var weight = element.GetValue(TextElement.FontWeightProperty) is FontWeight w ? w : FontWeights.Normal;
        var stretch = element.GetValue(TextElement.FontStretchProperty) is FontStretch s ? s : FontStretches.Normal;
        var pixelsPerDip = element is Visual ? VisualTreeHelper.GetDpi(element).PixelsPerDip : 1.0;
        return new FontDescriptor(family?.Source ?? string.Empty, weight, stretch, pixelsPerDip);
    }

    /// <summary>本元素当前是不是"被截断"状态（诊断与用例读数）。</summary>
    public static bool IsTruncated(FrameworkElement element)
        => States.TryGetValue(element, out var state) && state.IsTruncated;

    /// <summary>本元素当前用的是不是短式（诊断与用例读数）。</summary>
    public static bool IsShortForm(FrameworkElement element)
        => States.TryGetValue(element, out var state) && state.IsShort;
}
