using System.Windows;
using System.Windows.Controls;

namespace LinkPocket.Views;

/// <summary>
/// 轻量提示条（MD3 Snackbar 形状）：底部居中的圆角浮层，用于**一次性**、**看得见**的提示
/// （如"当前目录已被外部进程删除，已回到根目录"）。
/// <para>职责边界：本控件**只负责显示**——"显示什么"（<see cref="Text"/>，由调用方经 i18n 取好）
/// 与"是否显示"（<see cref="IsOpen"/>）都由宿主 VM 决定；自动消失的计时器由**宿主视图**持有
/// （视图可持计时器，VM 保持可单测——同 AiView 的既有做法）。</para>
/// <para>颜色全部走 <c>App.*</c> 语义令牌，不写颜色字面量（架构测试 <c>ThemeRulesTests</c> 卡死）。</para>
/// </summary>
public partial class NoticeBar : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(NoticeBar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(NoticeBar), new PropertyMetadata(false));

    public NoticeBar() => InitializeComponent();

    /// <summary>要显示的文案（调用方经 <c>Loc.K</c> 取好；控件不做本地化）。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>是否显示。</summary>
    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }
}
