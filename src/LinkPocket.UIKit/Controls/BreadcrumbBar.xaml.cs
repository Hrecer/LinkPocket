using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using LinkPocket.Theming.Fonts;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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
        /// <summary>命中区域（条级拖放后 = 整个胶囊外壳）：宿主判断“指针是否还在这一区”用。</summary>
        public FrameworkElement? Source { get; init; }
    }

    /// <summary>拖拽窗口滚动后，指针下方的目标段发生变化（没有新的 OLE DragOver 也要更新落点）。</summary>
    public class CrumbTargetChangedEventArgs : EventArgs
    {
        public object? Segment { get; init; }
    }

    /// <summary>
    /// 可复用面包屑地址栏（浏览页与回收站共用）：
    /// 浏览页绑定 VM 的面包屑/路径编辑态属性与命令（编辑、候选弹窗、键盘导航全部内聚在本控件），
    /// 回收站只塞一个静态段、不进编辑态。
    /// 事件：EditRequested（点胶囊空白请求进入路径编辑）/ CandidateMoveRequested（键盘上下选候选）/
    /// EditFocusLost / EditPopupClosed（宿主取消编辑）/ CandidateChosen（点选候选）/ CrumbDrag*（条级拖放）。
    ///
    /// <para>**横向滚动（2026-11-04 方案 B）**：路径不再折叠——全部段都留在一条虚拟轨道里，
    /// 只物化“可见窗口 + 左右各 2 段”；底部 6px 命中带里的 3px 拇指只改颜色不改粗细；
    /// 当前段在导航/尺寸变化后自动回到右端；向左滚开即可回看祖先（不隐内容、无占位动画）。</para>
    /// </summary>
    public partial class BreadcrumbBar : UserControl
    {
        private const int WindowBuffer = 4;
        private const double RailInset = 14;
        private const double RailThumbMinWidth = 36;
        private const double RailEdgeZone = 28;
        private const double RailHoldDelayMs = 120;
        /// <summary>内容裁剪半径 = 胶囊圆角 22 − 描边 2（裁剪的是胶囊内腔的真实圆头）。</summary>
        private const double ContentClipRadius = 20;

        private enum RailDragMode { None, Thumb, Track, End }

        /// <summary>VM/宿主塞进来的全部段（顺序 = 路径序）。</summary>
        private IReadOnlyList<object> _all = Array.Empty<object>();

        /// <summary>段集合版本：每次源集合变化 +1（仅用于诊断/幂等判断）。</summary>
        private int _allVersion;

        /// <summary>当前物化的可见窗口（ItemsSource）。</summary>
        private List<object> _visibleItems = new();

        /// <summary>估算/实测宽度（含尾分隔符）；索引 = _all 的下标。</summary>
        private double[] _widths = Array.Empty<double>();

        /// <summary>已知段的真实容器宽（精确窗口后回填）：后续导航优先用它，避免重复估算。</summary>
        private readonly Dictionary<(string Name, bool IsRoot, bool IsLast), double> _segmentWidths = new();

        /// <summary>TextWidthProbe 的文本宽缓存（与真实排版同源；按名称 + 是否当前段）。</summary>
        private readonly Dictionary<(string Text, bool Bold), double> _textWidths = new();

        private double _offset;
        private double _viewportWidth;
        /// <summary>当前物化窗口在 <c>_all</c> 里的起始下标（等值刷新后显示实例可能仍是旧的）。</summary>
        private int _windowStart;
        private bool _pinToEnd = true;
        private bool _relayoutPending;

        private double _railTrackWidth;
        private double _thumbLeft;
        private double _thumbWidth;

        private RailDragMode _railDragMode;
        private Point _railDragStart;
        private double _railStartOffset;

        private DispatcherTimer? _railAutoTimer;
        private DateTime _railAutoEligible;
        private DateTime _railAutoLast;
        private bool _railAutoRight;

        private object? _lastDragTarget;
        private bool _dragOverBar;

        /// <summary>INotifyCollectionChanged 订阅的当前源（换源退订；Unloaded 退订）。</summary>
        private INotifyCollectionChanged? _observedSource;

        public BreadcrumbBar()
        {
            InitializeComponent();

            // 换路径时在当次消息里同步收敛（只量这一段 ItemsControl，不量整页）：落帧即完成态，
            // 不隐内容、不闪占位；尺寸变化仍排到首帧之后再精确校正。
            Loaded += (_, _) =>
            {
                UpdateContentClip();
                _viewportWidth = CrumbViewport.ActualWidth;
                ApplySource(revealEnd: _pinToEnd);
            };
            CrumbContent.SizeChanged += (_, _) => UpdateContentClip();
            SizeChanged += (_, _) =>
            {
                if (!IsLoaded) return;
                var wasAtEnd = _pinToEnd || _offset >= MaxScroll - 2;
                _viewportWidth = CrumbViewport.ActualWidth;
                _pinToEnd = wasAtEnd;
                if (wasAtEnd) _offset = MaxScroll;
                else _offset = Math.Clamp(_offset, 0, MaxScroll);
                UpdateWindow();
                RequestRelayout();
            };
            Unloaded += (_, _) =>
            {
                StopObservingSource();
                StopRailAuto();
            };

            // 进入编辑态 = 输入框就绪（**可输入**）：聚焦全选挂在"编辑框真的变可见"这个事件上，
            // 绝不挂在宿主的属性通知上——通知链里可视状态由绑定/触发器稍后才落地，
            // 聚焦动作会打在还没可见的编辑框上**静默失败**（实测：点一下只出现地址栏外观、要点第二下才能输入）。
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

        /// <summary>
        /// 内容层按**胶囊内腔的真实圆头**裁剪：滚动时左侧祖先段是被药丸轮廓切掉，
        /// 而不是在内容视口处再切一条直边（“左边一条粗边框”的根因）。
        /// </summary>
        private void UpdateContentClip()
        {
            var width = CrumbContent.ActualWidth;
            var height = CrumbContent.ActualHeight;
            CrumbContent.Clip = width > 0 && height > 0
                ? new RectangleGeometry(new Rect(0, 0, width, height), ContentClipRadius, ContentClipRadius)
                : null;
        }

        public static readonly DependencyProperty BreadcrumbsProperty = DependencyProperty.Register(
            nameof(Breadcrumbs), typeof(System.Collections.IEnumerable), typeof(BreadcrumbBar),
            new PropertyMetadata(null, OnBreadcrumbsChanged));

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

        public static readonly DependencyProperty CrumbDropEnabledProperty = DependencyProperty.Register(
            nameof(CrumbDropEnabled), typeof(bool), typeof(BreadcrumbBar), new PropertyMetadata(true));

        public static readonly DependencyProperty RootSegmentWidthProperty = DependencyProperty.Register(
            nameof(RootSegmentWidth), typeof(double), typeof(BreadcrumbBar), new PropertyMetadata(double.NaN));

        public System.Collections.IEnumerable? Breadcrumbs
        {
            get => (System.Collections.IEnumerable?)GetValue(BreadcrumbsProperty);
            set => SetValue(BreadcrumbsProperty, value);
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

        /// <summary>段落点使能（缺省 true = 浏览页现役）。false = 只读页（回收站）：整条不接拖放。</summary>
        public bool CrumbDropEnabled
        {
            get => (bool)GetValue(CrumbDropEnabledProperty);
            set => SetValue(CrumbDropEnabledProperty, value);
        }

        /// <summary>
        /// 根段（`@root` / `@trash`，显示为本地化根名）的**冻结宽度**；<c>NaN</c>（缺省）= 按内容自适应。
        /// 值由各页显式给（浏览页 84 / 回收站 71），不放进共享模板做统一约束。
        /// </summary>
        public double RootSegmentWidth
        {
            get => (double)GetValue(RootSegmentWidthProperty);
            set => SetValue(RootSegmentWidthProperty, value);
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

        /// <summary>拖拽经过条（宿主决定 Effects：合法段 = 落点候选）。</summary>
        public event EventHandler<CrumbDragEventArgs>? CrumbDragOver;

        /// <summary>拖拽离开条（宿主清落点高亮）。</summary>
        public event EventHandler<CrumbDragEventArgs>? CrumbDragLeave;

        /// <summary>落到段上（宿主只记“待执行意图”，执行在 OLE 循环退出后）。</summary>
        public event EventHandler<CrumbDragEventArgs>? CrumbDrop;

        /// <summary>拖拽自动滚后指针下方的段变了（没有新的 OLE DragOver 也要更新落点）。</summary>
        public event EventHandler<CrumbTargetChangedEventArgs>? CrumbDragTargetChanged;

        private static void OnBreadcrumbsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var bar = (BreadcrumbBar)d;
            bar.StopObservingSource();
            var next = ToSegmentList(e.NewValue as System.Collections.IEnumerable);
            var samePath = bar.HasSamePath(next);
            bar._all = next;
            bar._allVersion++;
            bar._pinToEnd = true;
            if (e.NewValue is INotifyCollectionChanged incc)
            {
                incc.CollectionChanged += bar.OnSourceCollectionChanged;
                bar._observedSource = incc;
            }
            if (samePath)
            {
                bar.AdoptSamePath();
                return;
            }
            bar.ApplySource(revealEnd: true);
        }

        private static void OnIsPathEditingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var bar = (BreadcrumbBar)d;
            if ((bool)e.NewValue)
            {
                bar.StopRailDrag();
                bar.CrumbViewport.Visibility = Visibility.Collapsed;
                bar.UpdateChrome();
                return;
            }
            bar.CrumbViewport.Visibility = Visibility.Visible;
            bar.RequestRelayout();
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
            var next = ToSegmentList(_observedSource as System.Collections.IEnumerable);
            if (HasSamePath(next))
            {
                // 路径没变（移动/删除后引擎会连刷新多次，每次都 ReplaceAll 新实例）：
                // 只换实例、不重排——否则每一次后台刷新都会先隐掉整条路径再显示（用户实测的“闪两次”）。
                _all = next;
                _allVersion++;
                AdoptSamePath();
                return;
            }
            _all = next;
            _allVersion++;
            _pinToEnd = true;
            ApplySource(revealEnd: true);
        }

        /// <summary>新集合与当前路径**逐段等值**（名称 / 层级身份 / 是否当前段）：刷新换了实例但路径没变。</summary>
        private bool HasSamePath(IReadOnlyList<object> next) => SamePath(_all, next);

        /// <summary>两条段序列是否代表同一路径（按值比较；刷新产生的新 VM 实例不算变化）。</summary>
        public static bool SamePath(IReadOnlyList<object> current, IReadOnlyList<object> next)
        {
            if (current.Count == 0 || next.Count != current.Count) return false;
            for (var i = 0; i < next.Count; i++)
                if (!SameSegment(current[i], next[i])) return false;
            return true;
        }

        /// <summary>
        /// 路径等值刷新：保持现有可见窗口与偏移不动（不重排、不把滚开查看祖先的人拉回末端）。
        /// 只有在窗口还没物化（首次绑定/尚未布局）时才补一次窗口计算。
        /// </summary>
        private void AdoptSamePath()
        {
            if (_visibleItems.Count == 0 && _all.Count > 0 && IsLoaded && _viewportWidth > 0)
            {
                UpdateWindow();
                return;
            }
            if (_visibleItems.Count > 0) UpdateChrome();
        }

        /// <summary>
        /// 新路径落到控件：估算窗口 → 同一次消息里实测收敛（不隐藏内容、不闪占位）。
        /// 不把 100 段塞进 ItemsControl（WARNINGS 225）：窗口只物化“可见 + 缓冲”。
        /// </summary>
        private void ApplySource(bool revealEnd)
        {
            StopRailDrag();
            RebuildWidths();
            if (revealEnd) _offset = MaxScroll;
            else _offset = Math.Clamp(_offset, 0, MaxScroll);
            _pinToEnd = revealEnd || _offset >= MaxScroll - 2;

            if (_all.Count == 0)
            {
                _visibleItems.Clear();
                CrumbItems.ItemsSource = null;
                UpdateChrome();
                return;
            }

            if (_viewportWidth <= 0)
            {
                _visibleItems.Clear();
                CrumbItems.ItemsSource = null;
                UpdateChrome();
                RequestRelayout();
                return;
            }

            // 估算窗口先物化，紧接着**同一次消息里**用实测宽收敛到最终位置：
            // Windows 的地址栏换目录从不隐内容/不闪省略号，落帧就是完成态。
            UpdateWindow();
            if (IsLoaded) RelayoutNow();
        }

        public static (int Start, int Count, double OffsetWithinWindow) ComputeWindow(
            IReadOnlyList<double> widths, double offset, double viewport, int buffer)
        {
            if (widths.Count == 0) return (0, 0, 0);
            var total = 0d;
            for (var i = 0; i < widths.Count; i++) total += widths[i];
            if (total <= viewport) return (0, widths.Count, 0);

            var maxOffset = Math.Max(0, total - viewport);
            offset = Math.Clamp(offset, 0, maxOffset);

            var prefix = new double[widths.Count + 1];
            for (var i = 0; i < widths.Count; i++) prefix[i + 1] = prefix[i] + widths[i];

            var rawStart = 0;
            for (var i = 0; i < widths.Count; i++)
            {
                if (prefix[i] <= offset + 0.01) rawStart = i;
                else break;
            }
            var rawEnd = rawStart;
            while (rawEnd < widths.Count && prefix[rawEnd] < offset + viewport - 0.01) rawEnd++;

            var start = Math.Max(0, rawStart - Math.Max(0, buffer));
            var end = Math.Min(widths.Count, rawEnd + Math.Max(0, buffer));
            if (end <= start) end = Math.Min(widths.Count, start + 1);
            var inside = Math.Max(0, offset - prefix[start]);
            return (start, end - start, inside);
        }

        public static (double Width, double Left) ComputeThumb(
            double offset, double viewport, double total, double track)
        {
            if (track <= 0 || total <= 0 || viewport <= 0) return (0, 0);
            var width = Math.Max(RailThumbMinWidth, track * Math.Min(1, viewport / total));
            width = Math.Min(track, width);
            var maxOffset = Math.Max(0, total - viewport);
            var left = maxOffset <= 0
                ? 0
                : (track - width) * Math.Clamp(offset / maxOffset, 0, 1);
            return (width, left);
        }

        public static double EdgeScrollSpeed(double distanceFromEdge, double edgeZone, double maxSpeed)
        {
            if (edgeZone <= 0) return maxSpeed;
            var strength = Math.Clamp(distanceFromEdge / edgeZone, 0, 1);
            return 220 + strength * Math.Max(0, maxSpeed - 220);
        }

        private double TotalWidth
        {
            get
            {
                var total = 0d;
                for (var i = 0; i < _widths.Length; i++) total += _widths[i];
                return total;
            }
        }

        private double MaxScroll => Math.Max(0, TotalWidth - _viewportWidth);

        private void RebuildWidths()
        {
            _widths = new double[_all.Count];
            for (var i = 0; i < _all.Count; i++)
                _widths[i] = EstimateSegmentWidth(i, SegmentIsLast(_all[i]));
        }

        private double EstimateSegmentWidth(int index, bool isLast)
        {
            var item = _all[index];
            var name = SegmentName(item);
            var isRoot = SegmentIsRoot(item);
            var separator = isLast ? 0 : MeasureTextWidth("›", bold: false) + 4;
            var rootWidth = RootSegmentWidth;
            if (isRoot && !double.IsNaN(rootWidth) && rootWidth > 0)
                return rootWidth + separator;

            if (_segmentWidths.TryGetValue((name, isRoot, isLast), out var measured))
                return measured;

            return MeasureTextWidth(name, isLast) + 28 + separator + 1;
        }

        /// <summary>
        /// 与真实排版同源的字宽：用 <see cref="TextWidthProbe"/> 量，而不是按字符猜。
        /// 猜宽的误差会在长路径上累加，末尾就会出现“只剩一段空白”的假视口。
        /// </summary>
        private double MeasureTextWidth(string text, bool bold)
        {
            if (_textWidths.TryGetValue((text, bold), out var cached)) return cached;
            double width;
            try
            {
                var family = FontFamily?.Source;
                if (string.IsNullOrWhiteSpace(family)) family = "Segoe UI";
                width = TextWidthProbe.Width(new TextWidthProbe.MetricsRequest(
                    text, family, FontSize <= 0 ? 13 : FontSize,
                    bold ? FontWeights.SemiBold : FontWeights.Normal,
                    FontStretches.Normal,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip));
            }
            catch
            {
                width = EstimateTextWidthFallback(text, bold);
            }
            _textWidths[(text, bold)] = width;
            return width;
        }

        /// <summary>FormattedText 不可用时的最后兜底（只在极早初始化/无 DPI 时命中）。</summary>
        private static double EstimateTextWidthFallback(string text, bool bold)
        {
            double width = 0;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch)) width += 4;
                else if (ch >= 0x2E80) width += 13;
                else if (char.IsUpper(ch)) width += 8.6;
                else if (char.IsDigit(ch)) width += 7.4;
                else width += 7.2;
            }
            return (width + 1) * (bold ? 1.04 : 1.0);
        }

        /// <summary>
        /// 只布局面包屑条（**不**对整个 UserControl 调 UpdateLayout）：精确窗口不把主表/树的布局提前。
        /// </summary>
        private void RelayoutNow()
        {
            if (!IsLoaded) return;
            var actual = CrumbViewport.ActualWidth;
            if (actual > 0) _viewportWidth = actual;
            if (_viewportWidth <= 0) return;

            if (_visibleItems.Count == 0 && _all.Count > 0) UpdateWindow();
            if (_visibleItems.Count > 0)
            {
                CrumbItems.UpdateLayout();
                CacheMeasuredWidths();
                RebuildWidths();
            }

            if (_pinToEnd) _offset = MaxScroll;
            else _offset = Math.Clamp(_offset, 0, MaxScroll);
            UpdateWindow();
            CrumbItems.UpdateLayout();
            AlignTailIfNeeded();
            CrumbViewport.UpdateLayout();   // 让末尾对齐的偏移在本次消息里真正落到视口（落帧即完成态）
        }

        /// <summary>在路径末端把窗口右缘贴到视口右缘：估算/实测宽度有差时不留下右侧空白。</summary>
        private void AlignTailIfNeeded()
        {
            if (MaxScroll <= 0.5 || _offset < MaxScroll - 1 || _visibleItems.Count == 0
                || _windowStart + _visibleItems.Count < _all.Count)
                return;
            if (CrumbItems.ActualWidth <= _viewportWidth + 0.5) return;
            CrumbViewport.ScrollToHorizontalOffset(CrumbItems.ActualWidth - _viewportWidth);
        }

        /// <summary>精确窗口后把可见行的真实容器宽存起来：下一次导航的首帧估算直接命中。</summary>
        private void CacheMeasuredWidths()
        {
            for (var i = 0; i < CrumbItems.Items.Count; i++)
            {
                if (CrumbItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
                var item = CrumbItems.Items[i];
                _segmentWidths[(SegmentName(item), SegmentIsRoot(item), SegmentIsLast(item))]
                    = container.ActualWidth;
            }
        }

        private void UpdateWindow()
        {
            if (_all.Count == 0)
            {
                _visibleItems.Clear();
                CrumbItems.ItemsSource = null;
                UpdateChrome();
                return;
            }
            var actualViewport = CrumbViewport.ActualWidth;
            if (actualViewport > 0 && Math.Abs(actualViewport - _viewportWidth) > 0.5)
            {
                var wasAtEnd = _pinToEnd || _offset >= MaxScroll - 2;
                _viewportWidth = actualViewport;
                _pinToEnd = wasAtEnd;
            }
            if (_viewportWidth <= 0) return;

            // 视口/总量变化后先夹住偏移：否则“旧窗口宽度对应的 offset”会把窗口算在半路，
            // 右侧就出现一段没有段落的空白（用户实测的尾部空缺）。
            if (_pinToEnd) _offset = MaxScroll;
            else _offset = Math.Clamp(_offset, 0, MaxScroll);

            var total = TotalWidth;
            int start;
            int count;
            double inside;
            if (total <= _viewportWidth + 0.5)
            {
                start = 0;
                count = _all.Count;
                inside = 0;
            }
            else
            {
                (start, count, inside) = ComputeWindow(_widths, _offset, _viewportWidth, WindowBuffer);

                // 防御性补齐：窗口右缘必须真的填满视口（缓冲/估算偏差都不许留下空白）。
                var used = -inside;
                for (var i = start; i < start + count && i < _all.Count; i++) used += _widths[i];
                while (used < _viewportWidth - 0.5 && start + count < _all.Count)
                {
                    used += _widths[start + count];
                    count++;
                }
            }

            var next = new List<object>(count);
            for (var i = start; i < start + count && i < _all.Count; i++) next.Add(_all[i]);

            _windowStart = start;
            if (!SameSequence(_visibleItems, next))
            {
                _visibleItems = next;
                CrumbItems.ItemsSource = next;
            }
            CrumbViewport.ScrollToHorizontalOffset(inside);
            UpdateChrome();
        }

        private static bool SameSequence(IReadOnlyList<object> a, IReadOnlyList<object> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        /// <summary>窗口/尺寸变化后的精确收敛：等首帧真正画出来再量。</summary>
        private void RequestRelayout()
        {
            if (_relayoutPending) return;
            _relayoutPending = true;

            EventHandler? onRendered = null;
            DispatcherTimer? fallback = null;
            void Run()
            {
                if (onRendered is not null) CompositionTarget.Rendering -= onRendered;
                fallback?.Stop();
                // 再等 30ms 才做精确窗口：Rendering 只是“帧即将开始”，Background 仍可能赶在
                // 这一帧真正上屏之前；用计时器把精确布局明确排到首帧之后（尾随 ≤150ms）。
                var settle = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(30),
                };
                settle.Tick += (_, _) =>
                {
                    settle.Stop();
                    _relayoutPending = false;
                    RelayoutNow();
                };
                settle.Start();
            }

            var frames = 0;
            onRendered = (_, _) =>
            {
                if (++frames < 2) return;   // 等这一帧真正上屏，再量实测宽
                Run();
            };
            CompositionTarget.Rendering += onRendered;
            fallback = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            fallback.Tick += (_, _) => Run();
            fallback.Start();
        }

        private void UpdateChrome()
        {
            var overflow = !IsPathEditing && _viewportWidth > 0
                           && _all.Count > 0 && TotalWidth > _viewportWidth + 0.5;

            RailLayer.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;

            if (!overflow) return;

            var track = RailLayer.ActualWidth;
            if (track <= 1) track = Math.Max(1, CapsuleShell.ActualWidth - RailInset * 2);
            _railTrackWidth = track;
            var (width, left) = ComputeThumb(_offset, _viewportWidth, TotalWidth, track);
            _thumbWidth = width;
            _thumbLeft = left;
            RailThumb.Width = width;
            RailThumbTranslate.X = left;
        }

        private void ScrollToOffset(double offset)
        {
            var max = MaxScroll;
            offset = Math.Clamp(offset, 0, max);
            if (Math.Abs(offset - _offset) < 0.01)
            {
                UpdateChrome();
                return;
            }
            _offset = offset;
            _pinToEnd = offset >= max - 2;
            UpdateWindow();
        }

        // —— 鼠标滚轮：面包屑内部横滑，不让页面抢滚轮 ——

        private void Capsule_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (IsPathEditing || MaxScroll <= 0) return;
            ScrollToOffset(_offset - e.Delta);
            e.Handled = true;
        }

        /// <summary>拖拽期间来自 Raw Input 的滚轮（OLE 循环吞掉 WPF MouseWheel）：横向滚动路径并按指针刷新落点。</summary>
        public void ScrollByWheel(int delta)
        {
            if (delta == 0 || IsPathEditing || MaxScroll <= 0) return;
            ScrollToOffset(_offset - delta);
            RefreshDragTargetFromScreen();
        }

        private void RefreshDragTargetFromScreen()
        {
            if (!_dragOverBar || _all.Count == 0) return;
            if (!GetCursorPos(out var screen)) return;
            Point point;
            try
            {
                point = CrumbViewport.PointFromScreen(new Point(screen.X, screen.Y));
            }
            catch (InvalidOperationException)
            {
                return;
            }
            var segment = SegmentAt(point);
            if (ReferenceEquals(segment, _lastDragTarget)) return;
            _lastDragTarget = segment;
            CrumbDragTargetChanged?.Invoke(this, new CrumbTargetChangedEventArgs { Segment = segment });
        }

        // —— 6px 轨道命中带：拇指拖动 / 两端按住自动滚 / 中段按比例跳转 ——

        private void RailLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || IsPathEditing || MaxScroll <= 0) return;
            e.Handled = true;

            StopRailAuto();
            var p = e.GetPosition(RailLayer);
            _railTrackWidth = Math.Max(1, RailLayer.ActualWidth);
            _railDragStart = p;
            _railStartOffset = _offset;
            _railDragMode = RailDragMode.None;

            if (_thumbWidth > 0 && p.X >= _thumbLeft && p.X <= _thumbLeft + _thumbWidth)
            {
                _railDragMode = RailDragMode.Thumb;
                SetRailThumbTone(hot: true);
            }
            else if (p.X <= RailEdgeZone && _offset > 0)
            {
                _railDragMode = RailDragMode.End;
                _railAutoRight = false;
                StartRailAuto();
            }
            else if (p.X >= _railTrackWidth - RailEdgeZone && _offset < MaxScroll)
            {
                _railDragMode = RailDragMode.End;
                _railAutoRight = true;
                StartRailAuto();
            }
            else
            {
                _railDragMode = RailDragMode.Track;
                ScrollToOffset((p.X / _railTrackWidth) * MaxScroll);
            }

            RailLayer.CaptureMouse();
        }

        private void RailLayer_MouseMove(object sender, MouseEventArgs e)
        {
            if (_railDragMode != RailDragMode.Thumb) return;
            var p = e.GetPosition(RailLayer);
            var usable = Math.Max(1, _railTrackWidth - _thumbWidth);
            var next = _railStartOffset + ((p.X - _railDragStart.X) / usable) * MaxScroll;
            ScrollToOffset(next);
            e.Handled = true;
        }

        private void RailLayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            StopRailDrag();
            e.Handled = true;
        }

        private void StopRailDrag()
        {
            StopRailAuto();
            _railDragMode = RailDragMode.None;
            if (RailLayer.IsMouseCaptured) RailLayer.ReleaseMouseCapture();
            SetRailThumbTone(RailLayer.IsMouseOver);
        }

        private void RailLayer_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_railDragMode == RailDragMode.None) SetRailThumbTone(hot: true);
        }

        private void RailLayer_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_railDragMode == RailDragMode.None) SetRailThumbTone(hot: false);
        }

        /// <summary>拇指只做“同色深浅/透明度”反馈，绝不换成强调蓝（用户实测明确否决拖动变蓝）。</summary>
        private void SetRailThumbTone(bool hot)
        {
            RailThumb.SetResourceReference(Border.BackgroundProperty,
                hot ? "App.Text.Secondary" : "App.Text.Muted");
            RailThumb.Opacity = hot ? 1 : 0.78;
        }

        private void StartRailAuto()
        {
            StopRailAuto();
            _railAutoEligible = DateTime.UtcNow.AddMilliseconds(RailHoldDelayMs);
            _railAutoLast = DateTime.UtcNow;
            _railAutoTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            _railAutoTimer.Tick += RailAutoTick;
            _railAutoTimer.Start();
        }

        private void RailAutoTick(object? sender, EventArgs e)
        {
            if (_railDragMode != RailDragMode.End)
            {
                StopRailAuto();
                return;
            }
            var now = DateTime.UtcNow;
            if (now < _railAutoEligible)
            {
                _railAutoLast = now;
                return;
            }
            var dt = Math.Min(0.05, Math.Max(0.001, (now - _railAutoLast).TotalSeconds));
            _railAutoLast = now;
            var max = MaxScroll;
            if (max <= 0)
            {
                StopRailAuto();
                return;
            }
            var speed = EdgeScrollSpeed(RailEdgeZone, RailEdgeZone, 900);
            var before = _offset;
            ScrollToOffset(before + (_railAutoRight ? speed : -speed) * dt);
            if ((_railAutoRight && _offset >= max) || (!_railAutoRight && _offset <= 0))
                StopRailAuto();
        }

        private void StopRailAuto()
        {
            if (_railAutoTimer != null)
            {
                _railAutoTimer.Stop();
                _railAutoTimer.Tick -= RailAutoTick;
                _railAutoTimer = null;
            }
        }

        // —— 条级拖放：滚动时容器会重建，因此落点由指针坐标解析，而不是绑在段按钮上 ——

        private object? SegmentAt(Point pointInViewport)
        {
            if (_all.Count == 0 || _viewportWidth <= 0) return null;
            if (pointInViewport.X < 0 || pointInViewport.X > _viewportWidth) return null;

            var x = _offset + pointInViewport.X;
            var acc = 0d;
            for (var i = 0; i < _widths.Length; i++)
            {
                acc += _widths[i];
                if (x < acc) return _all[i];
            }
            return x <= TotalWidth + 0.5 && _all.Count > 0 ? _all[_all.Count - 1] : null;
        }

        private void Capsule_DragOver(object sender, DragEventArgs e)
        {
            if (!CrumbDropEnabled || IsPathEditing) return;
            var p = e.GetPosition(CrumbViewport);
            _dragOverBar = true;

            var segment = SegmentAt(p);
            if (segment == null)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            _lastDragTarget = segment;
            CrumbDragOver?.Invoke(this, new CrumbDragEventArgs
            {
                Args = e,
                Segment = segment,
                Source = CapsuleShell,
            });
            e.Handled = true;
        }

        private void Capsule_DragLeave(object sender, DragEventArgs e)
        {
            _dragOverBar = false;
            CrumbDragLeave?.Invoke(this, new CrumbDragEventArgs
            {
                Args = e,
                Segment = null,
                Source = CapsuleShell,
            });
            e.Handled = true;
        }

        private void Capsule_Drop(object sender, DragEventArgs e)
        {
            _dragOverBar = false;
            var segment = SegmentAt(e.GetPosition(CrumbViewport));
            if (segment != null)
            {
                CrumbDrop?.Invoke(this, new CrumbDragEventArgs
                {
                    Args = e,
                    Segment = segment,
                    Source = CapsuleShell,
                });
            }
            e.Handled = true;
        }

        /// <summary>拖拽悬停段的高亮：**半透明强调色叠层**（不实心、不反白，落点提示由浮层说明）。
        /// 传 null = 熄灭。只写 <c>Tag=DropTarget</c>，底色/文字仍归样式状态机管。</summary>
        public void SetDropHighlight(object? segment)
        {
            if (_dropHighlighted != null)
            {
                _dropHighlighted.ClearValue(TagProperty);
                _dropHighlighted = null;
            }
            if (segment == null) return;

            // 按“路径值”匹配而不是引用：后台等值刷新后 Items 里可能还是旧实例，_all 已是新实例。
            for (var i = 0; i < _visibleItems.Count; i++)
            {
                if (!SameSegment(_visibleItems[i], segment)) continue;
                if (CrumbItems.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter cp)
                {
                    var btn = FindVisualChild<Button>(cp);
                    if (btn != null)
                    {
                        btn.Tag = "DropTarget";   // 模板触发器据此出半透明强调叠层
                        _dropHighlighted = btn;
                    }
                }
                break;
            }
        }

        private FrameworkElement? _dropHighlighted;

        private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
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
        /// “输入一个字符 → 自动补全整段路径 → 再输入…”的闭环。SelectedIndex 双向绑定仅用于
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
                hit = VisualTreeHelper.GetParent(hit);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out ScreenPoint point);

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenPoint
        {
            public int X;
            public int Y;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> NameProps = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> RootProps = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> LastProps = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> IdProps = new();

        /// <summary>两段是否代表**同一条路径位置**（刷新会换 VM 实例，所以不能比引用）。</summary>
        private static bool SameSegment(object a, object b)
            => SegmentName(a) == SegmentName(b)
               && SegmentIsRoot(a) == SegmentIsRoot(b)
               && SegmentIsLast(a) == SegmentIsLast(b)
               && SegmentId(a) == SegmentId(b);

        private static string SegmentName(object item)
        {
            var prop = NameProps.GetOrAdd(item.GetType(), t => t.GetProperty("Name"));
            return prop?.GetValue(item) as string ?? item.ToString() ?? string.Empty;
        }

        private static bool SegmentIsRoot(object item)
        {
            var prop = RootProps.GetOrAdd(item.GetType(), t => t.GetProperty("IsRoot"));
            return prop?.GetValue(item) is true;
        }

        private static bool SegmentIsLast(object item)
        {
            var prop = LastProps.GetOrAdd(item.GetType(), t => t.GetProperty("IsLast"));
            return prop?.GetValue(item) is true;
        }

        /// <summary>段标识：控件段是 <c>Id</c>，浏览页 VM 是 <c>FolderId</c>（根段为空）。</summary>
        private static string? SegmentId(object item)
        {
            var prop = IdProps.GetOrAdd(item.GetType(), t => t.GetProperty("Id") ?? t.GetProperty("FolderId"));
            return prop?.GetValue(item) as string;
        }
    }
}
