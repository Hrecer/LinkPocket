using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LinkPocket.Views
{
    /// <summary>面包屑路径段：Name 显示文本；IsLast = 当前级（高亮、无分隔箭头）。</summary>
    public class BreadcrumbSegment
    {
        public string Name { get; init; } = string.Empty;
        public bool IsLast { get; init; }
        /// <summary>段标识（浏览页 = 文件夹 ID；纯展示模式可为 null）。</summary>
        public string? Id { get; init; }
    }

    public class CandidateMoveEventArgs : EventArgs
    {
        public int Delta { get; init; }
    }

    /// <summary>面包屑段的拖放转发参数：宿主在 Args 上设置 Effects/Handled 决定拖放行为（FolderTreePanel 同款模式）。</summary>
    public class CrumbDragEventArgs : EventArgs
    {
        public DragEventArgs Args { get; init; } = null!;
        /// <summary>命中段的 DataContext（BreadcrumbSegment / BrowserCrumbViewModel 等段类型）。</summary>
        public object? Segment { get; init; }
    }

    /// <summary>
    /// 可复用面包屑地址栏（浏览页与回收站共用）：
    /// 浏览页绑定 VM 的面包屑/路径编辑态属性与命令（编辑、候选弹窗、键盘导航全部内聚在本控件），
    /// 回收站只塞一个静态段、不进编辑态。
    /// 事件：EditRequested（点胶囊空白请求进入路径编辑）/ CandidateMoveRequested（键盘上下选候选）/
    /// EditFocusLost / EditPopupClosed（宿主取消编辑）/ CandidateChosen（点选候选）。
    ///
    /// <para>**溢出折叠（Windows 11 口径）**：胶囊宽度不足以放下全部段时，**从左折叠**进 « 按钮、
    /// 当前段（最右）永远可见；点 « 列出被折叠的祖先层级；窗口拉宽自动还原。
    /// 折叠由量测驱动（<see cref="RelayoutCrumbs"/>），装箱算法抽成纯函数 <see cref="ComputeFoldCount"/> 可单测。</para>
    /// </summary>
    public partial class BreadcrumbBar : UserControl
    {
        /// <summary>« 折叠按钮的估算占宽（与 XAML 的 Padding/字号一致；偏大一点更安全，避免首算放不下）。</summary>
        private const double OverflowButtonWidth = 44;

        /// <summary>折叠重算合并标志：同一帧内的多次 CollectionChanged 只重算一次。</summary>
        private bool _relayoutPending;

        /// <summary>VM/宿主塞进来的全部段（顺序 = 路径序）。</summary>
        private IReadOnlyList<object> _all = Array.Empty<object>();

        /// <summary>INotifyCollectionChanged 订阅的当前源（换源退订；Unloaded 退订）。</summary>
        private INotifyCollectionChanged? _observedSource;

        public BreadcrumbBar()
        {
            InitializeComponent();
            Loaded += (_, _) => RelayoutCrumbs();
            SizeChanged += (_, _) => RelayoutCrumbs();
            Unloaded += (_, _) => StopObservingSource();

            // 进入编辑态 = 输入框就绪（**可输入**）：聚焦全选挂在"编辑框真的变可见"这个事件上，
            // 绝不挂在宿主的属性通知上——通知链里可视状态由绑定/触发器稍后才落地，
            // 聚焦动作会打在还没可见的编辑框上**静默失败**（实测：点一下只出现地址栏外观、要点第二下才能输入）。
            // 与 InlineNameEditor 同一做法（谁拥有编辑框，谁负责"一显示就聚焦"）。
            PathEditBox.IsVisibleChanged += (_, _) =>
            {
                if (PathEditBox.IsVisible && IsPathEditing) FocusEditBox();
            };
        }

        /// <summary>聚焦路径编辑框并整名全选（Windows 11 口径：点地址栏空白一下即可直接输入/覆盖）。</summary>
        private void FocusEditBox()
        {
            PathEditBox.Focus();
            PathEditBox.SelectAll();
        }

        public static readonly DependencyProperty BreadcrumbsProperty = DependencyProperty.Register(
            nameof(Breadcrumbs), typeof(System.Collections.IEnumerable), typeof(BreadcrumbBar),
            new PropertyMetadata(null, OnBreadcrumbsChanged));

        public static readonly DependencyProperty FoldedCrumbsProperty = DependencyProperty.Register(
            nameof(FoldedCrumbs), typeof(System.Collections.IEnumerable), typeof(BreadcrumbBar),
            new PropertyMetadata(null));

        public static readonly DependencyProperty IsPathEditingProperty = DependencyProperty.Register(
            nameof(IsPathEditing), typeof(bool), typeof(BreadcrumbBar),
            new PropertyMetadata(false, OnIsPathEditingChanged));

        public static readonly DependencyProperty PathEditTextProperty = DependencyProperty.Register(
            nameof(PathEditText), typeof(string), typeof(BreadcrumbBar),
            new FrameworkPropertyMetadata(string.Empty) { BindsTwoWayByDefault = true });

        public static readonly DependencyProperty PathCandidatesProperty = DependencyProperty.Register(
            nameof(PathCandidates), typeof(System.Collections.IEnumerable), typeof(BreadcrumbBar),
            new PropertyMetadata(null));

        public static readonly DependencyProperty SelectedCandidateIndexProperty = DependencyProperty.Register(
            nameof(SelectedCandidateIndex), typeof(int), typeof(BreadcrumbBar),
            new FrameworkPropertyMetadata(-1) { BindsTwoWayByDefault = true });

        public static readonly DependencyProperty HasPathCandidatesProperty = DependencyProperty.Register(
            nameof(HasPathCandidates), typeof(bool), typeof(BreadcrumbBar), new PropertyMetadata(false));

        public static readonly DependencyProperty IsPathInvalidProperty = DependencyProperty.Register(
            nameof(IsPathInvalid), typeof(bool), typeof(BreadcrumbBar), new PropertyMetadata(false));

        public static readonly DependencyProperty CrumbClickCommandProperty = DependencyProperty.Register(
            nameof(CrumbClickCommand), typeof(ICommand), typeof(BreadcrumbBar), new PropertyMetadata(null));

        public static readonly DependencyProperty ConfirmPathCommandProperty = DependencyProperty.Register(
            nameof(ConfirmPathCommand), typeof(ICommand), typeof(BreadcrumbBar), new PropertyMetadata(null));

        public static readonly DependencyProperty CancelPathCommandProperty = DependencyProperty.Register(
            nameof(CancelPathCommand), typeof(ICommand), typeof(BreadcrumbBar), new PropertyMetadata(null));

        public static readonly DependencyProperty CompletePathCommandProperty = DependencyProperty.Register(
            nameof(CompletePathCommand), typeof(ICommand), typeof(BreadcrumbBar), new PropertyMetadata(null));

        public System.Collections.IEnumerable? Breadcrumbs
        {
            get => (System.Collections.IEnumerable?)GetValue(BreadcrumbsProperty);
            set => SetValue(BreadcrumbsProperty, value);
        }

        /// <summary>被折叠进 « 的段（供清单 Popup 绑定）。</summary>
        public System.Collections.IEnumerable? FoldedCrumbs
        {
            get => (System.Collections.IEnumerable?)GetValue(FoldedCrumbsProperty);
            private set => SetValue(FoldedCrumbsProperty, value);
        }

        public bool IsPathEditing
        {
            get => (bool)GetValue(IsPathEditingProperty);
            set => SetValue(IsPathEditingProperty, value);
        }

        public string PathEditText
        {
            get => (string)GetValue(PathEditTextProperty);
            set => SetValue(PathEditTextProperty, value);
        }

        public System.Collections.IEnumerable? PathCandidates
        {
            get => (System.Collections.IEnumerable?)GetValue(PathCandidatesProperty);
            set => SetValue(PathCandidatesProperty, value);
        }

        public int SelectedCandidateIndex
        {
            get => (int)GetValue(SelectedCandidateIndexProperty);
            set => SetValue(SelectedCandidateIndexProperty, value);
        }

        public bool HasPathCandidates
        {
            get => (bool)GetValue(HasPathCandidatesProperty);
            set => SetValue(HasPathCandidatesProperty, value);
        }

        public bool IsPathInvalid
        {
            get => (bool)GetValue(IsPathInvalidProperty);
            set => SetValue(IsPathInvalidProperty, value);
        }

        public ICommand? CrumbClickCommand
        {
            get => (ICommand?)GetValue(CrumbClickCommandProperty);
            set => SetValue(CrumbClickCommandProperty, value);
        }

        public ICommand? ConfirmPathCommand
        {
            get => (ICommand?)GetValue(ConfirmPathCommandProperty);
            set => SetValue(ConfirmPathCommandProperty, value);
        }

        public ICommand? CancelPathCommand
        {
            get => (ICommand?)GetValue(CancelPathCommandProperty);
            set => SetValue(CancelPathCommandProperty, value);
        }

        public ICommand? CompletePathCommand
        {
            get => (ICommand?)GetValue(CompletePathCommandProperty);
            set => SetValue(CompletePathCommandProperty, value);
        }

        public static readonly DependencyProperty CrumbDropEnabledProperty = DependencyProperty.Register(
            nameof(CrumbDropEnabled), typeof(bool), typeof(BreadcrumbBar), new PropertyMetadata(true));

        /// <summary>段落点使能（缺省 true = 浏览页现役）。false = 只读页（回收站）：段不接拖放
        /// （XAML 把段 AllowDrop 绑到本属性；CrumbDragOver/Leave/Drop 事件保留给浏览页）。</summary>
        public bool CrumbDropEnabled
        {
            get => (bool)GetValue(CrumbDropEnabledProperty);
            set => SetValue(CrumbDropEnabledProperty, value);
        }

        /// <summary>键盘上下移动候选（宿主转调 VM.MoveCandidate）。</summary>
        public event EventHandler<CandidateMoveEventArgs>? CandidateMoveRequested;

        /// <summary>路径编辑框失焦（宿主取消编辑态）。</summary>
        public event EventHandler? EditFocusLost;

        /// <summary>候选 Popup 关闭（宿主取消编辑态）。</summary>
        public event EventHandler? EditPopupClosed;

        /// <summary>鼠标点选候选（参数 = 候选文本）。</summary>
        public event EventHandler<string?>? CandidateChosen;

        /// <summary>点胶囊空白 = 请求进入路径编辑（Windows 11 口径，无需按钮）。
        /// 段是 Button（自己吃掉 MouseLeftButtonDown）不会误触发；编辑态由宿主命令的 CanExecute 挡重复进入。</summary>
        public event EventHandler? EditRequested;

        /// <summary>拖拽经过段（宿主决定 Effects：合法段 = 落点候选）。</summary>
        public event EventHandler<CrumbDragEventArgs>? CrumbDragOver;

        /// <summary>拖拽离开段（宿主清落点高亮）。</summary>
        public event EventHandler<CrumbDragEventArgs>? CrumbDragLeave;

        /// <summary>落到段上（宿主只记"待执行意图"，执行在 OLE 循环退出后）。</summary>
        public event EventHandler<CrumbDragEventArgs>? CrumbDrop;

        // 段拖放转发：控件不构造业务数据，只把"哪个段 + 原始参数"交给宿主（与 FolderTreePanel 同一原则）。

        private void Crumb_DragOver(object sender, DragEventArgs e)
        {
            if (sender is FrameworkElement fe)
                CrumbDragOver?.Invoke(this, new CrumbDragEventArgs { Args = e, Segment = fe.DataContext });
            e.Handled = true;
        }

        private void Crumb_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is FrameworkElement fe)
                CrumbDragLeave?.Invoke(this, new CrumbDragEventArgs { Args = e, Segment = fe.DataContext });
            e.Handled = true;
        }

        private void Crumb_Drop(object sender, DragEventArgs e)
        {
            if (sender is FrameworkElement fe)
                CrumbDrop?.Invoke(this, new CrumbDragEventArgs { Args = e, Segment = fe.DataContext });
            e.Handled = true;
        }

        /// <summary>拖拽悬停段的本地高亮（Windows 口径：落点段整体高亮）。
        /// 传 null = 熄灭。用本地值覆盖样式、清值还原触发器（IsLast 段本就同色，视觉天然不打架）。</summary>
        public void SetDropHighlight(object? segment)
        {
            if (_dropHighlighted != null)
            {
                _dropHighlighted.ClearValue(BackgroundProperty);
                _dropHighlighted.ClearValue(ForegroundProperty);
                _dropHighlighted = null;
            }
            if (segment == null) return;

            for (var i = 0; i < CrumbItems.Items.Count; i++)
            {
                if (!Equals(CrumbItems.Items[i], segment)) continue;
                if (CrumbItems.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter cp)
                {
                    var btn = FindVisualChild<Button>(cp);
                    if (btn != null)
                    {
                        // 落点高亮走**资源引用**：一次性取画刷赋值会把当前主题固化成本地值，
                        // 换主题后仍然亮着旧主题的色（与表头底色同一根因）。
                        // 熄灭仍走 ClearValue —— 资源引用也是本地值，整条摘掉即回落到样式触发器。
                        btn.SetResourceReference(Control.BackgroundProperty, "PrimaryContainer");
                        btn.SetResourceReference(Control.ForegroundProperty, "OnPrimaryContainer");
                        _dropHighlighted = btn;
                    }
                }
                break;
            }
        }

        private FrameworkElement? _dropHighlighted;

        private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T hit) return hit;
                var sub = FindVisualChild<T>(child);
                if (sub != null) return sub;
            }
            return null;
        }

        private void CrumbHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsPathEditing) return;   // 编辑态点击归 TextBox / 失焦路径，不再请求进入
            EditRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        // —— 溢出折叠（量测驱动） ——

        private static void OnBreadcrumbsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var bar = (BreadcrumbBar)d;
            bar.StopObservingSource();
            bar._all = ToSegmentList(e.NewValue as System.Collections.IEnumerable);
            if (e.NewValue is INotifyCollectionChanged incc)
            {
                incc.CollectionChanged += bar.OnSourceCollectionChanged;
                bar._observedSource = incc;
            }
            bar.RequestRelayout();
        }

        private static void OnIsPathEditingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var bar = (BreadcrumbBar)d;
            if ((bool)e.NewValue) return;                      // 进编辑态：面包屑行隐藏，无需量测
            bar.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(bar.RelayoutCrumbs));               // 退编辑态：行恢复显示后重算折叠
        }

        private static System.Collections.Generic.List<object> ToSegmentList(System.Collections.IEnumerable? source)
        {
            var list = new System.Collections.Generic.List<object>();
            if (source != null)
                foreach (var item in source) list.Add(item);
            return list;
        }

        private void StopObservingSource()
        {
            if (_observedSource != null)
            {
                _observedSource.CollectionChanged -= OnSourceCollectionChanged;
                _observedSource = null;
            }
        }

        private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // ⚠️ 必须先把集合当前内容同步进 _all 再重算：VM 加载是对已绑定集合做 Clear+Add（原地变更），
            // 不同时步这里，可见集合会永远停在绑定那一刻的空快照上（整片空白、拖放无目标）。
            _all = ToSegmentList(_observedSource as System.Collections.IEnumerable);
            RequestRelayout();
        }

        /// <summary>合并同一帧内的多次段集合变化（VM 刷新 = Clear + N 次 Add）。</summary>
        private void RequestRelayout()
        {
            if (_relayoutPending) return;
            _relayoutPending = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                _relayoutPending = false;
                RelayoutCrumbs();
            }));
        }

        /// <summary>
        /// 折叠装箱（**纯函数，可单测**）：宽度从左到右给出；空间不足时**从左折叠**，
        /// 右侧尽可能多保留（<paramref name="reserved"/> 为 « 按钮预留占宽）；至少保留最后一段（当前段）。
        /// 返回应折叠的段数（0 = 放得下、无折叠）。
        /// </summary>
        public static int ComputeFoldCount(IReadOnlyList<double> widths, double available, double reserved)
        {
            if (widths.Count == 0) return 0;
            double total = 0;
            foreach (var w in widths) total += w;
            if (total <= available) return 0;

            var budget = available - reserved;
            double acc = 0;
            var keepFrom = widths.Count;
            for (var i = widths.Count - 1; i >= 0; i--)
            {
                acc += widths[i];
                if (acc > budget) break;
                keepFrom = i;
            }
            var fold = keepFrom;
            if (fold > widths.Count - 1) fold = widths.Count - 1;   // 至少保留当前段（哪怕它自己超宽）
            return Math.Max(fold, 0);
        }

        /// <summary>
        /// 量测并应用折叠：先全显量总宽（放得下就全显、« 隐藏）；放不下则按
        /// <see cref="ComputeFoldCount"/> 折叠左侧段。个别段名极长导致一轮不准时再迭代一轮兜底。
        /// </summary>
        private void RelayoutCrumbs()
        {
            if (IsPathEditing || CrumbRow.ActualWidth <= 0 || _all.Count == 0)
            {
                ApplyFold(0);
                return;
            }

            var available = CrumbRow.ActualWidth;
            for (var pass = 0; pass < 3; pass++)        // « 占位可能改变需求宽 → 最多迭代 3 轮收敛
            {
                var reserve = OverflowButton.Visibility == Visibility.Visible ? OverflowButtonWidth : 0;
                var widths = MeasureSegmentWidths();
                var fold = ComputeFoldCount(widths, available, reserve);
                if (fold == 0 && reserve == 0)
                {
                    ApplyFold(0);
                    return;
                }
                var before = fold;
                ApplyFold(fold);
                UpdateLayout();
                if (CrumbItems.DesiredSize.Width <= available || fold == before) return;
            }
        }

        /// <summary>全显状态下量每段容器宽（UpdateLayout 后容器 ActualWidth 即真实宽）。</summary>
        private double[] MeasureSegmentWidths()
        {
            ApplyFold(0);
            UpdateLayout();
            var widths = new double[_all.Count];
            for (var i = 0; i < _all.Count; i++)
            {
                if (CrumbItems.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
                    widths[i] = fe.ActualWidth;
            }
            return widths;
        }

        /// <summary>应用折叠：左侧 <paramref name="count"/> 段收进 «（清单同步），其余照常显示。</summary>
        private void ApplyFold(int count)
        {
            var folded = new List<object>();
            var visible = new List<object>();
            for (var i = 0; i < _all.Count; i++)
                (i < count ? folded : visible).Add(_all[i]);

            CrumbItems.ItemsSource = visible;
            FoldedCrumbs = folded;
            OverflowButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (count == 0) OverflowPopup.IsOpen = false;
        }

        private void OverflowButton_Click(object sender, RoutedEventArgs e)
            => OverflowPopup.IsOpen = !OverflowPopup.IsOpen;

        /// <summary>点 « 清单里的折叠段 = 直达该层级（复用宿主的 CrumbClickCommand，与可见段同一条导航路径）。</summary>
        private void OverflowList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var hit = e.OriginalSource as DependencyObject;
            while (hit != null)
            {
                if (hit is ListBoxItem item)
                {
                    if (item.DataContext != null) CrumbClickCommand?.Execute(item.DataContext);
                    OverflowPopup.IsOpen = false;
                    e.Handled = true;
                    return;
                }
                hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
            }
        }

        // —— 路径编辑态（候选补全） ——

        private void PathEditBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                CandidateMoveRequested?.Invoke(this, new CandidateMoveEventArgs { Delta = 1 });
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                CandidateMoveRequested?.Invoke(this, new CandidateMoveEventArgs { Delta = -1 });
                e.Handled = true;
            }
        }

        private void PathEditBox_LostFocus(object sender, RoutedEventArgs e)
            => EditFocusLost?.Invoke(this, EventArgs.Empty);

        private void PathCandidatesPopup_Closed(object? sender, EventArgs e)
            => EditPopupClosed?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// 候选只由<b>鼠标点选</b>触发 Choose：VM 每次输入都会重建候选并把
        /// SelectedCandidateIndex 置 0 —— 若再经 SelectionChanged 自动 Choose，会形成
        /// "输入一个字符 → 自动补全整段路径 → 再输入…"的闭环。SelectedIndex 双向绑定仅用于
        /// 高亮跟随（↑/↓ 与 Tab 补全由 CompletePathCommand 显式触发）。
        /// </summary>
        private void PathCandidatesList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var hit = e.OriginalSource as DependencyObject;
            while (hit != null)
            {
                if (hit is ListBoxItem item)
                {
                    if (item.DataContext is string name)
                        CandidateChosen?.Invoke(this, name);
                    e.Handled = true;
                    return;
                }
                hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
            }
        }
    }
}
