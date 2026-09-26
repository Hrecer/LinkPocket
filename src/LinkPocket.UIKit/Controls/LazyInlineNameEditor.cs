using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace LinkPocket.Views;

/// <summary>
/// <see cref="InlineNameEditor"/> 的**惰性宿主**：不编辑时它只是自己（一个空的 <see cref="ContentControl"/>），
/// 真正进入编辑（<see cref="IsEditing"/> 为真）才创建编辑框并挂上绑定。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：行模板里给每一行都常驻一个改名编辑框（UserControl + TextBox + 模板 +
/// 5 条绑定 + KeyBinding）是万级列表滚动卡顿的最大单项——实测在 10 001 行上，
/// 单是这一项就让"跳一次滚动"多花 16 ms（`PERF-LARGE-LIBRARY.md` §2.2）。
/// 而一屏里同一时刻**至多一行**在改名，没有理由为每一行预先造一个编辑框。
/// </para>
/// <para>
/// <b>对外语义与 <see cref="InlineNameEditor"/> 完全一致</b>（四个属性同名同义），
/// 所以行模板/树模板只换元素名，宿主 VM 与手势让位（<see cref="InlineNameEditor.IsWithin"/>）都不用改：
/// 编辑框真的出现时就挂在可视树里，命中链照样能认出它。
/// </para>
/// </remarks>
public class LazyInlineNameEditor : ContentControl
{
    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
        nameof(IsEditing), typeof(bool), typeof(LazyInlineNameEditor),
        new PropertyMetadata(false, (d, _) => ((LazyInlineNameEditor)d).SyncEditor()));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LazyInlineNameEditor),
        new FrameworkPropertyMetadata(string.Empty) { BindsTwoWayByDefault = true });

    public static readonly DependencyProperty CommitCommandProperty = DependencyProperty.Register(
        nameof(CommitCommand), typeof(ICommand), typeof(LazyInlineNameEditor), new PropertyMetadata(null));

    public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(
        nameof(CancelCommand), typeof(ICommand), typeof(LazyInlineNameEditor), new PropertyMetadata(null));

    public LazyInlineNameEditor()
    {
        // 与 UserControl 承载单个子元素时的排布一致：编辑框填满宿主给定的宽度（树里由 MinWidth 定下限）。
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;

        // 宿主自己**不参与焦点**：它只是个容器，"可聚焦"的是它里面的编辑框。
        // ⚠️ 必须显式关掉：`Control` 的默认 `IsTabStop=true` + 非空 `FocusVisualStyle` 会让它成为
        //    "点了行/树节点之后焦点的落点"——落在一个没有视觉的容器上，页面看起来就是"焦点丢了"
        //    （实测：焦点不变式两条断言红、焦点视觉扫描把它报成"带默认虚线框的元素"）。
        Focusable = false;
        IsTabStop = false;
        FocusVisualStyle = null;
    }

    /// <summary>是否处于编辑态（宿主投影：行/树节点的 IsRenaming）。</summary>
    public bool IsEditing
    {
        get => (bool)GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    /// <summary>编辑中的文本（TwoWay：输入即回写 VM 的 EditingName）。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Enter / 失焦：提交。</summary>
    public ICommand? CommitCommand
    {
        get => (ICommand?)GetValue(CommitCommandProperty);
        set => SetValue(CommitCommandProperty, value);
    }

    /// <summary>Esc：取消（还原原名，不改数据）。</summary>
    public ICommand? CancelCommand
    {
        get => (ICommand?)GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    /// <summary>当前是否已经造出编辑框（测试用读数；正常路径不读）。</summary>
    internal bool HasEditor => _editor != null;

    private InlineNameEditor? _editor;

    /// <summary>
    /// 编辑态投影 → 造/摘编辑框。退出编辑时**整体摘除**（不是隐藏）：既回收 TextBox 的开销，
    /// 也保证"迟到的失焦"落在已不存在的控件上（<see cref="InlineNameEditor"/> 内部那条
    /// "生效编辑面才提交"的归属校验因此更早成立）。
    /// </summary>
    private void SyncEditor()
    {
        if (!IsEditing)
        {
            if (_editor == null) return;
            Content = null;
            _editor = null;
            return;
        }

        if (_editor != null) return;

        var editor = new InlineNameEditor();
        editor.SetBinding(InlineNameEditor.TextProperty,
            new Binding(nameof(Text)) { Source = this, Mode = BindingMode.TwoWay });
        editor.SetBinding(InlineNameEditor.CommitCommandProperty,
            new Binding(nameof(CommitCommand)) { Source = this });
        editor.SetBinding(InlineNameEditor.CancelCommandProperty,
            new Binding(nameof(CancelCommand)) { Source = this });
        // 一出生就是编辑态：聚焦 + 整名全选由控件自己等一次布局后完成（见其 OnIsEditingChanged/Loaded）。
        editor.IsEditing = true;

        _editor = editor;
        Content = editor;
    }
}
