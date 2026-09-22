using System.Windows;
using System.Windows.Controls;

namespace LinkPocket.Views;

/// <summary>
/// 可复用的排序表头按钮（MD3E）：数据驱动 —— Label 显示列名，Field 标识排序键，
/// Direction 三态（null=未激活、true=升序、false=降序）由样式模板驱动 ▲/▼ 指示器。
/// 任何列表都能用它：把列定义做成数据（Label/Field/Width），表头与行共用同一份列宽即可。
/// 点击行为由使用方统一处理（读 Field + Direction），控件本身不含业务。
/// </summary>
public class SortableHeaderButton : Button, ITextWidthHost
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SortableHeaderButton), new PropertyMetadata(string.Empty));

    /// <summary>排序键（数据驱动：与数据模型的字段名一致，如 title / updated_at）。</summary>
    public static readonly DependencyProperty FieldProperty = DependencyProperty.Register(
        nameof(Field), typeof(string), typeof(SortableHeaderButton), new PropertyMetadata(string.Empty));

    /// <summary>null = 未激活（不显示指示器）；true = 升序 ▲；false = 降序 ▼。</summary>
    public static readonly DependencyProperty DirectionProperty = DependencyProperty.Register(
        nameof(Direction), typeof(bool?), typeof(SortableHeaderButton), new PropertyMetadata(null));

    /// <summary>
    /// 表头文字可用的宽度（= 本列宽 − 左右内距），由 <c>SortableDataTable</c> 绑到列宽单一数据源上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么表头文字需要这一个"外部给的限宽"</b>：列宽是冻结几何（用户可拖、按列定义给），
    /// 而文案宽度随语言变——中文「按查看次数排序」13pt 需 84px，而该列只有 72px。
    /// 没有这个数时 <c>LocFit</c> 的可用宽是 0（按钮自己没有显式 <c>Width</c>，宽度来自 Grid 列），
    /// 于是文字照原字号画出去，**压在相邻列上并被右缘裁掉**（实测：溢出 36px，屏幕上只看得见后半句）。
    /// </para>
    /// <para>
    /// 值一列一变（拖拽列宽 / 换语言后的列宽重算）就重新投影一次字号，机制与
    /// <c>UI-SPEC §3</c> 的"几何冻结 + 字号自适应"完全同一条：**绝不截断，放不下就缩字号**。
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty TextWidthProperty = DependencyProperty.Register(
        nameof(TextWidth), typeof(double), typeof(SortableHeaderButton),
        new PropertyMetadata(double.NaN, OnTextWidthChanged));

    private static void OnTextWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 可用宽变了 = 本列的几何约束变了（拖列宽 / 换语言后重算）→ 重新投影字号
        if (d is SortableHeaderButton header) LocFit.Project(header);
    }

    public double TextWidth
    {
        get => (double)GetValue(TextWidthProperty);
        set => SetValue(TextWidthProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Field
    {
        get => (string)GetValue(FieldProperty);
        set => SetValue(FieldProperty, value);
    }

    public bool? Direction
    {
        get => (bool?)GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }

    /// <summary>本列左右内距之和（药丸内距，与 <c>SortableDataTable</c> 给 <see cref="TextWidth"/> 的扣减一致）。</summary>
    public const double TextInsets = 24;

    static SortableHeaderButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SortableHeaderButton),
            new FrameworkPropertyMetadata(typeof(SortableHeaderButton)));
    }
}
