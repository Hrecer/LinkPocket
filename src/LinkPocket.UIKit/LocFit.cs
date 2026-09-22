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
/// <remarks>
/// <b>只有两档</b>：`Off` 与 `Shrink`。曾经有过第三档 `ShrinkThenEllipsis`（放不下就 <c>CharacterEllipsis</c> 截断），
/// 已按定稿口径**删除**（2026-09-22）：**任何情况下都不截断**——放不下就继续缩小字号，直到放得下。
/// 截断的文案是错的文案（日期截成 `09/21/2026 1:4…` 就是错的日期），而"把字缩小"永远给出完整内容。
/// </remarks>
public enum LocFitMode
{
    /// <summary>不参与自适应（<b>用户数据</b>用这一档：书签标题、URL、文件夹名永不参与取词、永不缩字号）。</summary>
    Off = 0,

    /// <summary>唯一的自适应档：只缩字号（无下限），**永不截断**。</summary>
    Shrink = 1,
}

/// <summary>
/// **文字格宽的声明口**：壳的宽度由外部数据决定（表格列宽）时，由壳把"这一格有多少"报给 <c>LocFit</c>。
/// </summary>
/// <remarks>
/// <para>
/// 存在理由：<c>LocFit.AvailableWidth</c> 的两条既有来源都失效于这类壳——
/// ① 壳自己没有显式 <c>Width</c>（宽度来自 Grid 列定义）⇒ 找不到"冻结控件壳"；
/// ② 元素实测宽在水平 <c>StackPanel</c> 里恒等于它需要的宽 ⇒ 自指。
/// 结果是可用宽算成 0、字号一个都不缩，文字照原字号画到相邻列上（实测：智能列表表头溢出 36px、
/// 被右缘裁掉半句）。
/// </para>
/// <para>
/// 实现方只报数、不缓存：数值的唯一事实源仍在宿主那边（例如 <c>SortableDataTable.ColumnWidths</c>），
/// 值一变就调一次 <see cref="LocFit.Project"/> 重新投影。
/// </para>
/// </remarks>
public interface ITextWidthHost
{
    /// <summary>本格留给文字的可画宽度（已扣掉内距与同行其它元素）；<c>NaN</c> / ≤0 = 不声明。</summary>
    double TextWidth { get; }
}

/// <summary>
/// <b>几何冻结 + 字号自适应</b>的唯一实现：界面文案在**既有几何内**自己找位置，
/// 绝不撑宽、绝不换行、绝不改 Padding、**绝不截断**（约束 B「英文零尺寸漂移」的可执行定义之一）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有它</b>：英文普遍比中文宽 30%~150%（「永久删除」4 字 ≈50px vs
/// <c>Delete permanently</c> 19 字母 ≈124px），而控件高度（48 处 <c>Height="32"</c>）与表格列宽
/// 都是冻结几何。放不下时只有两条路：改几何（禁止）或让文字自己让位（本机制）。
/// </para>
/// <para>
/// <b>降级链（顺序固定）</b>：① base 字号 → ② 换短式文案（`#short`，属**文案质量**手段：
/// 让常用短语在基准字号下就放得下，也不会让相邻控件字号不齐）→ ③ 缩字号**直到放得下**（无下限）。
/// <b>到此为止：没有截断这一步。</b>
/// </para>
/// <para>
/// <b>算法是纯函数</b>（<see cref="Fit"/>：文字 + 字体 + 可用宽 → 字号 + 形态），
/// 只有 <c>FormattedText</c> 那一层碰 WPF；测试因此可以注入 <see cref="Metrics"/>
/// 直接跑完整降级链，不依赖真实渲染。
/// </para>
/// <para>
/// <b>它是纯布局机制，一个字都不写进元素的 Text</b>：文字的唯一写者是<b>绑定</b>
/// （<c>Text="{loc:Fit …}"</c> 的转换器）。<see cref="Project"/> 只写字号。
/// 历史教训：让本行为去写 <c>TextBlock.Text</c>，等于让一个属性有两个写者——
/// 绑定重投的值被行为按上一次的判定盖住，切语言后模型已是新语言、屏幕上还留着旧语言
/// （实测：模型侧 <c>09/20/2026 10:50 AM</c>、屏幕上 <c>2026-09-20 10:50</c>）。
/// </para>
/// </remarks>
public static class LocFit
{
    /// <summary>
    /// 字号搜索的**退化边界**（pt）——不是设计上的下限，只是让搜索有界。
    /// </summary>
    /// <remarks>
    /// <b>字号没有下限</b>：放不下就继续缩，直到在冻结几何里放得下（几何一个像素都不动，让位的一律是字）。
    /// 本常量只防"可用宽接近 0 时无限往下试"这种退化情形；实测最深的需求是 13pt → 9.0pt（命令栏「重命名」的
    /// 英文 `Rename`），离它很远。
    /// <para>
    /// 曾经有过 `max(base×0.75, 9.5pt)` 的**设计下限**，已按实测撤销——它挡在缩的路上，
    /// "放不下"就成了它自己造成的：实测命令栏「重命名」在中文 base 13pt 下就已溢出 6px，
    /// 而下限不许缩到能放下的 11pt。决策与撤销理由见 `国际化规划.md §6` 决策 6 的撤销说明。
    /// </para>
    /// </remarks>
    public const double DegenerateFloor = 4.0;

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
    public readonly record struct FitResult(double Size);

    /// <summary>
    /// 自适应本体（纯函数）：在 <paramref name="available"/> 内挑**能放下的最大字号**。
    /// </summary>
    /// <param name="full">要显示的文字（空 = 无文案，直接返回不动）。</param>
    /// <param name="baseSize">基准字号（元素本来要用的那个）。</param>
    /// <param name="available">可用宽（冻结壳里真正留给这段文字的那一格）。</param>
    /// <param name="desc">字体描述（族 / 字重 / 字宽 / DPI）。</param>
    /// <remarks>
    /// <b>只有一条降级路径：把字号缩小。</b>不换文案、**不截断**（口径 2026-09-22）。
    /// 曾经的"先换短式（`#short`）再缩字号"已删除：短式会让**中文侧**也被缩成短语
    /// （实测「恢复默认外观」在冻结宽度里被换成「恢复默认」——那是产品文案，不该由版式机制改）。
    /// 表里的 `#short` 键保留备查，但**自动链不使用它们**。
    /// </remarks>
    public static FitResult Fit(
        string? full,
        double baseSize,
        double available,
        in FontDescriptor desc)
    {
        if (string.IsNullOrEmpty(full)) return new FitResult(baseSize);

        // 可用宽为 0 或负（还没参与布局 / 被压成 0）＝ 一点位置都没有：
        // 不缩字号（缩了也没有意义——宽度不因字号而变），保持 base 让调用方另有守卫去处理。
        if (available <= 0) return new FitResult(baseSize);

        // ① base 放得下就是它（绝大多数中文文案与短英文文案走到这里就结束）
        if (Fits(full, baseSize, available, desc)) return new FitResult(baseSize);

        // ② 步进缩小，直到放得下。**取"能放下的最大档"而不是"第一个放得下的档"**：
        //    结果是稳定解（重算必然同值），布局回环因此不成立。
        for (var candidate = SnapDown(baseSize - Step); candidate >= DegenerateFloor; candidate = SnapDown(candidate - Step))
        {
            if (Fits(full, candidate, available, desc))
                return new FitResult(candidate);
        }

        // ③ 退化边界：可用宽被压到几乎为零之类的情形。停在边界档——宁可字小，也绝不截断内容。
        return new FitResult(DegenerateFloor);
    }

    /// <summary>该元素在当前字号下的可用宽（冻结宽度的**控件壳**里 = 壳内容区 − 同行其它子级；否则 = 实测宽 − 内距）。</summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不能只用"元素自己的实测宽"</b>（实测根因）：药丸里的文字在一根**水平 <c>StackPanel</c></b> 里，
    /// 而 StackPanel 会把子级想要的宽度**照给** ⇒ <c>text.ActualWidth</c> 恒等于它自己需要的宽，
    /// "放不放得下"变成自己跟自己比、**永远成立**。后果不是"少缩一点"，而是**根本不缩**：
    /// 实测面包屑根段（壳 80px）的 `Bookmarks` 停在 13.0pt、`ActualWidth=73.7=需要 73.7`，
    /// 最后由壳把文字**裁掉**（屏幕上是 `Bookma`）。
    /// </para>
    /// <para>
    /// <b>⚠️ 只认 <see cref="Control"/> 壳</b>（按钮 / 勾选 / 输入框）：模板内部的 <c>Border</c>/<c>ContentPresenter</c>
    /// 也常带 <c>Width</c>，认错一层就会把可用宽算到接近 0——这条试过，把面包屑压到 **4.0pt** 并已回滚一次
    /// （见 `WARNINGS` 102）。控件壳是"有人刻意定过尺寸"的那一层，语义明确。
    /// </para>
    /// <para>
    /// 找不到控件壳（内容自适应的行内文本、表格单元格）时才回落到"实测宽 − 内距"。
    /// </para>
    /// </remarks>
    public static double AvailableWidth(FrameworkElement element)
    {
        // ⚠️ **宿主显式声明的文字格宽优先**（见 ITextWidthHost）：有些壳的宽度不是自己 `Width` 给的，
        //    而是由外部数据决定（表格列宽）——那些壳自己没有 `Width`，`FrozenControlShell` 找不到，
        //    于是"可用宽"算成 0、字号一个都不缩，文字照原字号画到相邻列上去（实测：表头溢出 36px）。
        //    这类宿主自己知道"这一格有多少"，由它把数报上来（唯一事实源仍在宿主那边）。
        var declared = DeclaredTextWidth(element);
        if (declared > 0) return declared;

        var shell = FrozenControlShell(element);
        if (shell is not null)
        {
            // 可用宽 = **壳的内容区 − 同行兄弟 − 自身外边距**（与字号无关的那一格）。
            var content = shell.ActualWidth - HorizontalInsets(shell);
            content -= InlineSiblingsWidth(element, shell);
            content -= element.Margin.Left + element.Margin.Right;   // 自己的外边距同样占地方
            content = Math.Max(0, content);

            // ⚠️ **只有"外部显式限宽"才进一步收紧它**（实测踩到，见 WARNINGS 109）。
            //    早期无条件取 `min(元素实得, 壳内容区)`，而"元素实得"在两种情形下都会骗人：
            //    ① 元素自己的宽是本行为**缩过字号**之后算出来的 ⇒ 可用宽跟着字号一起变小，
            //       形成**只缩不涨的死循环**（实测指纹：可用宽恰好 = 壳宽 ÷ 2 − 内距；
            //       症状：把壳从 100 加宽到 155，字号一点不长）；
            //    ② 元素被**内容自适应的中间容器**（水平 StackPanel / ContentPresenter）包着时，
            //       "实得"只是那个容器的内容宽，而不是"这一格真正空着多少"。
            //    真正需要收紧的只有一种：有人显式给了 MaxWidth（元素自己或它到壳之间的包装）。
            var cap = MaxWidthCap(element, shell);
            return cap > 0 ? Math.Min(content, cap) : content;
        }

        var own = element.ActualWidth;
        if (double.IsNaN(own) || double.IsInfinity(own) || own <= 0) return 0;
        var ownSlot = Math.Max(0, own - HorizontalInsets(element));
        var ownCap = MaxWidthCap(element, null);
        return ownCap > 0 ? Math.Min(ownSlot, ownCap) : ownSlot;
    }

    /// <summary>元素自己（或它到壳之间的包装）显式给的 <c>MaxWidth</c>；没有则 0。</summary>
    /// <remarks>
    /// 这是"外部把这一格又切窄了"的合法来源之一（表格单元格给文字挂 <c>MaxWidth</c>、窄栏里的文字块等）。
    /// 容器**照内容给宽**（水平 <c>StackPanel</c> / <c>ContentPresenter</c>）不算约束——
    /// 那正是"元素实得"会骗人的第二种情形。
    /// </remarks>
    private static double MaxWidthCap(DependencyObject element, DependencyObject? stopAt)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { MaxWidth: > 0 and < double.PositiveInfinity } capped)
                return capped.MaxWidth;
            if (stopAt is not null && ReferenceEquals(node, stopAt)) break;
        }
        return 0;
    }

    /// <summary>
    /// 元素自己（或它到壳之间的包装）**声明**的文字格宽；没有则 0。
    /// </summary>
    /// <remarks>
    /// 给"壳宽由外部数据决定、壳自己不带 <c>Width</c>"的那类宿主用（表格表头 = 列宽决定宽度）。
    /// 这类宿主知道"这一格有多少"，由它经 <see cref="ITextWidthHost"/> 报上来——
    /// 唯一事实源仍在宿主那边（列宽单一数据源），这里只读不推。
    /// </remarks>
    private static double DeclaredTextWidth(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ITextWidthHost { TextWidth: > 0 and < double.PositiveInfinity } host)
                return host.TextWidth;
        return 0;
    }

    /// <summary>最近的**冻结宽度的控件壳**（显式 <c>Width</c> 的 <see cref="Control"/> 祖先）。</summary>
    private static Control? FrozenControlShell(DependencyObject element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Control { Width: > 0 } control) return control;
        return null;
    }

    /// <summary>同一行里**其它**子级占掉的宽（只对水平 <c>StackPanel</c> 求和：Grid 的子级会重叠，不能相加）。</summary>
    private static double InlineSiblingsWidth(DependencyObject element, Control shell)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, shell)) return 0;
            if (node is not StackPanel { Orientation: Orientation.Horizontal } panel) continue;

            var sum = 0.0;
            foreach (var child in panel.Children)
            {
                if (child is not FrameworkElement sibling) continue;
                if (IsSelfOrAncestorOf(sibling, element)) continue;
                sum += sibling.ActualWidth + sibling.Margin.Left + sibling.Margin.Right;
            }
            return sum;
        }
        return 0;
    }

    /// <summary><paramref name="candidate"/> 是不是 <paramref name="element"/> 本身或其祖先。</summary>
    private static bool IsSelfOrAncestorOf(DependencyObject candidate, DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, candidate)) return true;
        return false;
    }

    /// <summary>左右内距之和（含描边）：可用宽必须减掉它，否则文字会被内距挤出去。</summary>
    /// <remarks>
    /// ⚠️ <b>不要在这里再扣 <c>Margin</c></b>：试过一次，结果是全面缩过头——命令栏药丸从
    /// 11.5/12.5/10.0 一路掉到 6.5/5.5/4.0，连"重命名"都撞上退化边界并露出缺键标记。
    /// 原因是 <c>TextBlock.ActualWidth</c> 在受 <c>MaxWidth</c> 约束时**本身就是"可用宽 + 外边距"**
    /// （实测 47.3 = 40.3 + 7），再扣一次等于把同一段间距算了两次。
    /// </remarks>
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
        string full, double baseSize, double available, in FontDescriptor desc)
    {
        var key = string.Create(CultureInfo.InvariantCulture,
            $"{desc}|{baseSize:F2}|{available:F2}|{full}");
        if (Cache.TryGetValue(key, out var cached)) return cached;

        if (Cache.Count >= CacheLimit) Cache.Clear();   // 有界：满了整体清，不引 LRU 的复杂度
        FitEvaluations++;
        var result = Fit(full, baseSize, available, desc);
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
        /// <summary>上一次投影时的语言代数（见 <see cref="ObserveLanguage"/>）。</summary>
        public int ObservedLangVersion = -1;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, FitState> States = new();

    private static FitState StateOf(FrameworkElement element)
        => States.TryGetValue(element, out var state) ? state : States.GetValue(element, _ => new FitState());

    // ── 两个附加属性：接入通道 + 当前该显示的文本 ─────────────────────────

    /// <summary>
    /// 接入通道：绑 <see cref="LocText"/>（或 <c>string</c>，或文案键）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>它同时是"文案变了"这个入口</b>：值一变就调 <see cref="Project"/>。
    /// 这是必须的，因为接入值来自一条普通绑定（<c>{loc:FitValue ModifiedText}</c> —— 注意它<b>没有转换器</b>，
    /// <c>FitValueResolver</c> 只是把 <see cref="LocText"/> 原样投出来），绑定重读只会换掉这个附加属性，
    /// <b>不会</b>顺手重算显示文字。曾经"顺手重算"这件事是由一个外部驱动循环（<c>RefreshAll</c>）
    /// 代劳的——那正是被删掉的补丁；本回调接管它，于是"文案变了"与"几何变了"都汇进同一个
    /// <see cref="Project"/>，一进一出。
    /// </para>
    /// <para>
    /// 它与 <see cref="ModeProperty"/> 是并列的两条通道，XAML 属性顺序不保证，
    /// 所以谁后到都要能把链路接起来——两边都调 <see cref="Wire"/>，由 <see cref="Wire"/> 调 <see cref="Project"/>。
    /// </para>
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
            "Chosen", typeof(string), typeof(LocFit),
            new PropertyMetadata(string.Empty, (d, e) =>
            {
                // 谁画本通道的产物，谁就进登记表——这条保证与"元素怎么被创建、什么时候拿到 Mode"无关：
                // 模板里那句 Text="{Binding (views:LocFit.Chosen), RelativeSource=Self}" 是**唯一**
                // 把结果显示出来的方式（代码侧走 BuildChosenBinding，同样落到这里）。
                // 没有它，"登记"要靠另一条附加属性（Text/Mode）被赋值的时机，而那个时机不受本类控制
                // （实测：Text 绑定先求值、Mode 还是默认 Off；等 Mode 到位时元素早已错过登记）。
                if (d is FrameworkElement fe) _ = StateOf(fe);
            }));

    public static void SetText(DependencyObject element, object? value) => element.SetValue(TextProperty, value);
    public static object? GetText(DependencyObject element) => element.GetValue(TextProperty);
    public static string GetChosen(DependencyObject element) => (string)element.GetValue(ChosenProperty);

    // ── 唯一投影入口 ───────────────────────────────────────────────────────

    static LocFit()
    {
        // 主题 / 字体一变，基准字号与度量缓存都可能失效：清掉每元素状态让它们重新学一次
        // （字体族是度量缓存键的一部分，所以缓存本身不需要清）。
        Theming.ThemeService.Changed += (_, _) => States.Clear();
    }

    /// <summary>
    /// <b>唯一投影入口</b>：把当前的 <see cref="TextProperty"/> 值在**当前</b>可用宽与基准字号下
    /// 投成 <b>显示文字 + 字号 + 截断/ToolTip</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么只有一个入口</b>：显示文字曾经由布局侧单独维护（一个"二级缓存"），
    /// 于是"切语言之后它还新不新"变成一个需要人记住、并且要靠外部驱动去续命的问题——
    /// 仓库为此长出了 <c>AfterApply</c> 钩子、<c>RefreshAll</c> 遍历、<c>DispatcherPriority</c> 排队
    /// 三件补丁，而实测它们<b>一件都没生效</b>（<c>ConvertCalls 0 → 0</c>：那套"强制重取"连转换器都没碰到）。
    /// 现在两个触发源（文案变了 / 几何变了）走的是同一个方法、读的是同一份来源，
    /// 值由比较决定写不写 ⇒ 谁先到都收敛，不存在"两套时序"这回事。
    /// </para>
    /// <para>
    /// <b>为什么不需要外部驱动</b>：语言一变，WPF 自己会把 <c>{loc:FitValue …}</c> 那条 MultiBinding 重算一遍；
    /// 但**重算不等于取到新值**——第二路指向的是模型上的普通属性，WPF 会复用该子绑定的缓存值、
    /// 不再调 getter。所以新语言是**由 <c>FitValueResolver</c> 烙进产出值**的（<c>LocText.LangVersion</c>），
    /// 属性系统的变更推送自己会把这件事传到底：产出值变了 ⇒ <see cref="TextProperty"/> 的变更回调 ⇒
    /// 本方法。整条链没有任何"强制重取 / 排优先级 / 遍历可视树"的环节。
    /// </para>
    /// <para>
    /// 幂等：稳态下重算得到同一个结论、不写任何属性（"值不变不写"——布局回环的唯一防线）。
    /// </para>
    /// </remarks>
    public static void Project(FrameworkElement element)
    {
        if (GetMode(element) == LocFitMode.Off) return;
        if (Normalize(GetText(element)) is not LocText text || text.IsEmpty) return;

        var available = AvailableWidth(element);

        // 可用宽为 0 = 还没参与布局：只落全长、不动字号。
        // 在这里缩字号会把字号钉在下限上，而"基准字号"就照着压过的值学错了（一次就再也回不去）。
        if (available <= 0)
        {
            PlaceChosen(element, text.Resolve());
            return;
        }

        var baseSize = BaseSizeFor(element);
        var desc = Describe(element);
        var result = Evaluate(text.Resolve(), baseSize, available, desc);

        PlaceFontSize(element, result.Size);   // 值不变不写（回环防线）

        // ⚠️ 到这里就结束：**不写 TextTrimming、不截断**（2026-09-22 口径）。
        //    元素自己的 TextTrimming（行样式里给用户数据列设的省略号）不被本机制碰——
        //    本机制只管"把字号缩到放得下"，内容的完整性由缩字号保证。
        PlaceChosen(element, text.Resolve());
    }

    /// <summary>
    /// 落<b>显示文字</b>——本类里写 <see cref="ChosenProperty"/> 的<b>唯一</b>一处。
    /// </summary>
    /// <remarks>
    /// "值不变不写"在这里不只是性能：<c>SetValue</c> 会打断该附加属性上的绑定，
    /// 所以稳定态下一次都不该写。调用方只有 <see cref="Project"/>，它每次都用现读的来源重算。
    /// </remarks>
    private static void PlaceChosen(FrameworkElement element, string shown)
    {
        if (!string.Equals(GetChosen(element), shown, StringComparison.Ordinal))
            element.SetValue(ChosenProperty, shown);
    }

    /// <summary>接上通道：登记进每元素状态，并按需挂三个几何触发源 + 立刻投影一次。</summary>
    /// <remarks>
    /// <para>
    /// <b>登记与 <see cref="ModeProperty"/> 无关</b>（这条是踩出来的）：<c>Mode</c> 缺省是 <see cref="LocFitMode.Off"/>，
    /// 而 XAML 里 <c>LocFit.Text</c> 的绑定<b>先求值</b>——那一刻 <c>Mode</c> 还没被赋值。
    /// 旧实现遇到 <c>Off</c> 直接 <c>return</c>，连登记都没做。登记还另有一道保证在
    /// <see cref="ChosenProperty"/> 上（谁画本通道的产物谁就进表），两道加起来才不会漏。
    /// </para>
    /// <para>
    /// 两条通道（<see cref="TextProperty"/> / <see cref="ModeProperty"/>）谁后到都走这里，先摘后挂避免重复订阅。
    /// </para>
    /// </remarks>
    private static void Wire(FrameworkElement element)
    {
        _ = StateOf(element);   // 登记

        if (GetMode(element) == LocFitMode.Off) return;

        element.LayoutUpdated -= OnLayoutUpdated;   // 先摘后挂：避免重复订阅
        element.LayoutUpdated += OnLayoutUpdated;
        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        element.Loaded -= OnLoaded;
        element.Loaded += OnLoaded;
        Project(element);
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
    /// 读数：本元素当前画出来的文字<b>在最终字号下还是放不进可用宽</b>（= 已经缩到退化边界仍放不下）。
    /// </summary>
    /// <remarks>
    /// <b>注意它不再等于"被截断"</b>（2026-09-22 口径）：机制**不写 <c>TextTrimming</c>**，
    /// 所以放不下时不会出现省略号——它只说明"字已经缩到最小了还是不够"，
    /// 这时内容会由容器按自己的口径裁切。用它做诊断，不要用它当"截断"的判据。
    /// </remarks>
    public static bool IsTruncated(FrameworkElement element)
    {
        var applied = GetChosen(element);
        if (string.IsNullOrEmpty(applied)) return false;
        var available = AvailableWidth(element);
        return available > 0 && !Fits(applied, FontSizeOf(element), available, Describe(element));
    }

    /// <summary>把接入通道的值归一成 <see cref="LocText"/>（<c>string</c> = 只有全长；认不出就拒绝）。</summary>
    private static LocText? Normalize(object? value) => value switch
    {
        LocText text => text,
        string raw when raw.Length > 0 => LocText.Of(LocValue.Literal(raw)),
        _ => null,
    };

    // ── 附加属性 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 自适应策略（<b>文字由绑定写，本属性只管布局侧的行为</b>）。
    /// </summary>
    /// <remarks>
    /// 用法：<c>Text="{loc:Fit some.key}" views:LocFit.Mode="Shrink"</c>。
    /// 转换器负责选形态（全长 / 短式），本属性负责挂布局事件、在尺寸变化后重算字号。
    /// <b>不写 <c>TextTrimming</c>、不加 ToolTip</b>：没有截断，也就不需要"截断 + 全文提示"那一对。
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

        // ⚠️ 此刻元素常常还没参与布局（可用宽 0）：Wire 里的 Project 只会落一个全长、不动字号，
        // 基准字号留到首次拿到真实可用宽时再学（否则会照着被压过的下限值学错，一次就回不去）。
        Wire(element);
    }

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement element) Project(element);
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
        if (sender is FrameworkElement element) Project(element);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) Project(element);
    }
}

/// <summary>
/// 自适应文案通道的转换器：<c>Text="{loc:Fit …}"</c> 背后的那一步。
/// </summary>
/// <remarks>
/// <para>
/// <b>它只是"文案变了"这个入口</b>，不做任何决定：把当前语言的 <see cref="LocText"/> 交给
/// <see cref="LocFit.TextProperty"/>（唯一事实来源），然后调 <see cref="LocFit.Project"/>——
/// 与"几何变了"那个入口（布局事件）走的是同一个方法。
/// </para>
/// <para>
/// <b>为什么不需要任何人来驱动它</b>：输入里的 <c>[0]</c> 是 <c>LocTable.Version</c>，
/// 语言一变这个子绑定就脏，WPF 自己会重跑本方法（实验实测：
/// <c>attach=1 → versionBump=2 → contextSwap=3</c>，见 <c>FitChannelExperiment</c>）。
/// 此前仓库里的 <c>LocaleService.AfterApply</c> 钩子 + <c>RefreshAll</c> 遍历 +
/// <c>DispatcherPriority</c> 排队三件补丁，是建立在"WPF 不会因为子绑定变化而重算"这个**错误前提**上的，
/// 实测它们连本方法都没碰到（<c>ConvertCalls 0 → 0</c>）——已整段删除。
/// </para>
/// <para>
/// <b>为什么能拿到元素</b>：<see cref="LocFitExtension"/> 在 markup extension 阶段用
/// <c>IProvideValueTarget</c> 拿到目标元素，塞进 <c>ConverterParameter</c>。
/// </para>
/// <para>
/// 输入：<c>[0]</c> = 语言版本（<b>失效触发器</b>：语言一变整条链自动重算）、
/// <c>[1]</c> = <see cref="LocText"/>（全长 + 可选短式）。
/// </para>
/// </remarks>
public sealed class LocFitResolver : IMultiValueConverter
{
    public static LocFitResolver Instance { get; } = new();

    public object? Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var element = parameter as FrameworkElement;
        var text = values.Length > 1 ? values[1] as LocText? : null;

        // 未接入 / 空文案：显示为空，不留上一种语言的残影。
        if (element is null || text is null || text.Value.IsEmpty) return string.Empty;

        // 供值：LocText 进唯一事实来源。它是**活引用**（Resolve 每次现取当前语言），
        // 所以后续任何一次投影读到的都必然是当前语言。
        LocFit.SetText(element, text.Value);
        LocFit.Project(element);
        return LocFit.GetChosen(element);
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
    /// 建一个接了自适应通道的文本单元格（日期 / 计数这类**结构化数据**：放不下就缩字号，**永不截断**）。
    /// </summary>
    /// <param name="text">文案值（语言一变自己重算）。</param>
    /// <param name="fontSize">基准字号。</param>
    /// <param name="mode">自适应策略（缺省 = <see cref="LocFitMode.Shrink"/>：唯一的一档）。</param>
    public static TextBlock BuildCell(LocText text, double fontSize = 12.5, LocFitMode mode = LocFitMode.Shrink)
    {
        var cell = new TextBlock
        {
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center,
            DataContext = text,
            // ⚠️ 显式 `None`：行样式给用户数据列设了 `CharacterEllipsis`，而**结构化单元格不许出现省略号**
            //    （口径 2026-09-22：放不下就缩字号）。实测：不显式清掉时，1808 个日期格都带着省略号。
            TextTrimming = TextTrimming.None,
        };
        if (mode == LocFitMode.Off) { cell.Text = text.Resolve(); return cell; }

        LocFit.SetMode(cell, mode);
        LocFit.SetText(cell, text);   // 接入通道：文字与字号都由本机制管
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
