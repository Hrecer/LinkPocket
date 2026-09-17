using System;
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

    /// <summary>
    /// 可复用面包屑地址栏（浏览页与回收站共用）：
    /// 浏览页绑定 VM 的面包屑/路径编辑态属性与命令（编辑、候选弹窗、键盘导航全部内聚在本控件），
    /// 回收站只塞一个静态段、不进编辑态。
    /// 事件：CandidateMoveRequested（键盘上下选候选）/ EditFocusLost / EditPopupClosed（宿主取消编辑）/
    /// CandidateChosen（点选候选）。
    /// </summary>
    public partial class BreadcrumbBar : UserControl
    {
        private bool _suppressCandidateChoose;

        public BreadcrumbBar()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty BreadcrumbsProperty = DependencyProperty.Register(
            nameof(Breadcrumbs), typeof(System.Collections.IEnumerable), typeof(BreadcrumbBar),
            new PropertyMetadata(null));

        public static readonly DependencyProperty IsPathEditingProperty = DependencyProperty.Register(
            nameof(IsPathEditing), typeof(bool), typeof(BreadcrumbBar), new PropertyMetadata(false));

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

        /// <summary>键盘上下移动候选（宿主转调 VM.MoveCandidate）。</summary>
        public event EventHandler<CandidateMoveEventArgs>? CandidateMoveRequested;

        /// <summary>路径编辑框失焦（宿主取消编辑态）。</summary>
        public event EventHandler? EditFocusLost;

        /// <summary>候选 Popup 关闭（宿主取消编辑态）。</summary>
        public event EventHandler? EditPopupClosed;

        /// <summary>鼠标点选候选（参数 = 候选文本）。</summary>
        public event EventHandler<string?>? CandidateChosen;

        private void PathEditBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                _suppressCandidateChoose = true;
                try { CandidateMoveRequested?.Invoke(this, new CandidateMoveEventArgs { Delta = 1 }); }
                finally { _suppressCandidateChoose = false; }
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                _suppressCandidateChoose = true;
                try { CandidateMoveRequested?.Invoke(this, new CandidateMoveEventArgs { Delta = -1 }); }
                finally { _suppressCandidateChoose = false; }
                e.Handled = true;
            }
        }

        private void PathEditBox_LostFocus(object sender, RoutedEventArgs e)
            => EditFocusLost?.Invoke(this, EventArgs.Empty);

        private void PathCandidatesPopup_Closed(object? sender, EventArgs e)
            => EditPopupClosed?.Invoke(this, EventArgs.Empty);

        private void PathCandidates_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCandidateChoose) return;
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not string name) return;
            CandidateChosen?.Invoke(this, name);
        }
    }
}
