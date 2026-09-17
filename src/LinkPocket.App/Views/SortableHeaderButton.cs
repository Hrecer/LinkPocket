using System.Windows;
using System.Windows.Controls;

namespace LinkPocket.Views;

/// <summary>
/// 可复用的排序表头按钮（MD3E）：数据驱动 —— Label 显示列名，Field 标识排序键，
/// Direction 三态（null=未激活、true=升序、false=降序）由样式模板驱动 ▲/▼ 指示器。
/// 任何列表都能用它：把列定义做成数据（Label/Field/Width），表头与行共用同一份列宽即可。
/// 点击行为由使用方统一处理（读 Field + Direction），控件本身不含业务。
/// </summary>
public class SortableHeaderButton : Button
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SortableHeaderButton), new PropertyMetadata(string.Empty));

    /// <summary>排序键（数据驱动：与数据模型的字段名一致，如 title / updated_at）。</summary>
    public static readonly DependencyProperty FieldProperty = DependencyProperty.Register(
        nameof(Field), typeof(string), typeof(SortableHeaderButton), new PropertyMetadata(string.Empty));

    /// <summary>null = 未激活（不显示指示器）；true = 升序 ▲；false = 降序 ▼。</summary>
    public static readonly DependencyProperty DirectionProperty = DependencyProperty.Register(
        nameof(Direction), typeof(bool?), typeof(SortableHeaderButton), new PropertyMetadata(null));

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

    static SortableHeaderButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SortableHeaderButton),
            new FrameworkPropertyMetadata(typeof(SortableHeaderButton)));
    }
}
