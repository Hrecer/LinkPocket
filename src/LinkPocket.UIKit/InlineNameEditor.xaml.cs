using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LinkPocket.Views
{
    /// <summary>
    /// 行内就地改名编辑框（主栏行 / 目录树节点共用，视图零业务逻辑）：
    /// <see cref="IsEditing"/> 由宿主 VM 的**重命名态投影**驱动——进入编辑即显示、自动聚焦并全选；
    /// Enter 提交 / Esc 取消（XAML InputBindings，属控件级编辑语义）/ 失焦提交（Windows 口径）。
    ///
    /// <para>提交与取消都只是**发命令**：谁在重命名、改成什么名、要不要编号，全归 VM（单一事实来源），
    /// 本控件不持有任何业务状态。</para>
    /// </summary>
    public partial class InlineNameEditor : UserControl
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(InlineNameEditor),
            new FrameworkPropertyMetadata(string.Empty) { BindsTwoWayByDefault = true });

        public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
            nameof(IsEditing), typeof(bool), typeof(InlineNameEditor),
            new PropertyMetadata(false, (d, _) => ((InlineNameEditor)d).OnIsEditingChanged()));

        public static readonly DependencyProperty CommitCommandProperty = DependencyProperty.Register(
            nameof(CommitCommand), typeof(ICommand), typeof(InlineNameEditor), new PropertyMetadata(null));

        public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(
            nameof(CancelCommand), typeof(ICommand), typeof(InlineNameEditor), new PropertyMetadata(null));

        public InlineNameEditor()
        {
            InitializeComponent();
            IsVisibleChanged += (_, _) => { if (IsVisible) FocusAndSelect(); };
            // Loaded 兜底：行/节点在刷新中重建时，控件可能"一出生就可见"（IsVisibleChanged 不一定派发）
            Loaded += (_, _) => FocusAndSelect();
        }

        /// <summary>编辑中的文本（TwoWay：输入即回写 VM 的 EditingName）。</summary>
        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>是否处于编辑态（宿主投影：行/树节点的 IsRenaming）。</summary>
        public bool IsEditing
        {
            get => (bool)GetValue(IsEditingProperty);
            set => SetValue(IsEditingProperty, value);
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

        /// <summary>
        /// 进入编辑态：等一次布局（此时文本绑定已生效、控件已可见）再聚焦并全选——
        /// Windows 口径：进入改名即整名选中，可直接覆盖输入。
        /// </summary>
        private void OnIsEditingChanged()
        {
            if (IsEditing) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new System.Action(FocusAndSelect));
        }

        private void FocusAndSelect()
        {
            if (!IsEditing || !IsVisible) return;
            EditBox.Focus();
            EditBox.SelectAll();
        }

        /// <summary>
        /// 失焦 = 提交（Windows 口径），但**只在我仍是当前编辑面时**才提交——
        /// <paramref name="IsEditing"/> 就是宿主重命名会话的投影：会话已被结束或已切到别的实体时，
        /// 我这一侧的 IsEditing 早已变 false，此刻迟到的失焦**必须忽略**
        /// （曾实测：右键菜单关闭的失焦晚于"新建第二个文件夹"的会话启动，旧编辑框的失焦把**新会话**误提交掉了，
        /// 表现为"第二个新建的文件夹不进入改名"；控件自己按投影做归属校验，不赌事件时序）。
        /// </summary>
        private void EditBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!IsEditing) return;   // 迟到失焦（已不属于当前会话）→ 空操作
            if (CommitCommand?.CanExecute(null) == true) CommitCommand.Execute(null);
        }

        /// <summary>
        /// 命中元素是否落在某个改名编辑框内部。
        /// 行/树的手势处理器据此**让位**：编辑框内的鼠标操作（定位光标、选词、双击选词）
        /// 归编辑框自己，绝不参与行选择 / 拖拽 / 双击打开（否则双击编辑框会把目录打开）。
        /// </summary>
        public static bool IsWithin(DependencyObject? source)
        {
            for (var d = source; d != null;
                 d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                if (d is InlineNameEditor) return true;
            }
            return false;
        }
    }
}
