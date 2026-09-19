using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LinkPocket.Views
{
    /// <summary>树节点拖放转发参数：宿主在 Args 上设置 Effects/Handled 决定拖放行为。</summary>
    public class TreeItemDragEventArgs : EventArgs
    {
        public DragEventArgs Args { get; init; } = null!;
        /// <summary>命中节点的 DataContext（FolderNode / TrashFolderNode）。</summary>
        public object? Node { get; init; }
    }

    /// <summary>
    /// 树节点**拖拽启动**请求参数：面板只报「哪个节点、用哪个源元素」，由宿主构造业务载荷并调 <c>DoDragDrop</c>。
    /// 面板是可复用控件（浏览页与回收站共用），不得在此构造业务数据（否则回收站树也会"会拖"）。
    /// </summary>
    public class TreeItemDragStartEventArgs : EventArgs
    {
        /// <summary>按下并拖动的节点 DataContext（FolderNode / TrashFolderNode）。</summary>
        public object? Node { get; init; }

        /// <summary>承载本次拖拽的源元素（行卡片）：宿主把它交给 <c>DoDragDrop</c>。</summary>
        public DependencyObject? Source { get; init; }
    }

    /// <summary>
    /// 可复用文件夹树面板（浏览页与回收站共用）：
    /// 数据驱动（ItemsSource = FolderNode / TrashFolderNode 节点集合），
    /// 行为由宿主注入 —— NodeSelected 决定选中语义，NodeDragOver/NodeDrop 订阅才有拖放能力。
    /// </summary>
    public partial class FolderTreePanel : UserControl
    {
        public FolderTreePanel()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
            nameof(ItemsSource), typeof(System.Collections.IEnumerable), typeof(FolderTreePanel),
            new PropertyMetadata(null));

        public static readonly DependencyProperty HeaderTextProperty = DependencyProperty.Register(
            nameof(HeaderText), typeof(string), typeof(FolderTreePanel),
            new PropertyMetadata("文件夹"));

        /// <summary>节点集合（FolderNode / TrashFolderNode）。</summary>
        public System.Collections.IEnumerable? ItemsSource
        {
            get => (System.Collections.IEnumerable?)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public string HeaderText
        {
            get => (string)GetValue(HeaderTextProperty);
            set => SetValue(HeaderTextProperty, value);
        }

        /// <summary>节点选中（参数 = 节点对象；虚拟根选中也会触发，宿主自行处理）。</summary>
        public event EventHandler<object?>? NodeSelected;

        /// <summary>点击树面板空白区域（未命中任何节点）：宿主应清空选中。</summary>
        public event EventHandler? TreeBackgroundClicked;

        /// <summary>拖拽经过节点（宿主设置 e.Args.Effects；不订阅 = 不接受拖放）。</summary>
        public event EventHandler<TreeItemDragEventArgs>? NodeDragOver;

        /// <summary>落放到节点。</summary>
        public event EventHandler<TreeItemDragEventArgs>? NodeDrop;

        /// <summary>拖拽离开节点（宿主据此熄灭落点高亮——覆盖式落点状态在离开时必须清零）。</summary>
        public event EventHandler<TreeItemDragEventArgs>? NodeDragLeave;

        /// <summary>请求启动节点拖拽（按下后移动超过系统阈值）：宿主构造业务载荷并调 DoDragDrop。
        /// 与 NodeDragOver/NodeDrop 同一开关口径——**订阅即启用**；回收站不订阅 = 纯展示（树节点不可拖）。</summary>
        public event EventHandler<TreeItemDragStartEventArgs>? NodeDragStartRequested;

        // 树面板按下的手势凭据（见 FolderTree_MouseLeftButtonUp 的归属校验）。
        private bool _pressOnTreeBackground;
        private int _pressTreeClickCount;

        /// <summary>树面板按下（隧道先于行）：记录"按下是否在空白"+ 点击计数。</summary>
        private void FolderTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressOnTreeBackground = HitNode(e) == null;
            _pressTreeClickCount = e.ClickCount;
        }

        /// <summary>树空白点击：命中目标不在 TreeViewItem 内时视为点击空白 → 通知宿主清空选中。
        /// **归属校验**：只有"按下也在空白"的单击才算点空白——双击行进入目录后树会重建，
        /// 第二击抬起可能落在新树空白处（按下却在旧节点上），不得当作"点空白清选中"。</summary>
        private void FolderTree_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_pressOnTreeBackground || _pressTreeClickCount > 1) return;
            if (HitNode(e) == null)
                TreeBackgroundClicked?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>命中测试：返回点击位置所在的 TreeViewItem（不在任何节点内 = null）。</summary>
        private TreeViewItem? HitNode(System.Windows.Input.MouseEventArgs e)
        {
            var hit = System.Windows.Media.VisualTreeHelper.HitTest((Visual)FolderTreeControl, e.GetPosition(FolderTreeControl));
            var el = hit?.VisualHit;
            while (el != null && el is not TreeViewItem)
                el = System.Windows.Media.VisualTreeHelper.GetParent(el);
            return el as TreeViewItem;
        }

        // 树行按下的手势凭据（见 FolderTreeItem_MouseLeftButtonUp 的归属校验）。
        private object? _pressNode;
        private int _pressNodeClickCount;

        /// <summary>行拖拽的按下起点与"本次手势已进入拖拽"标记（与主栏行同一套阈值口径）。</summary>
        private Point _nodeDragStart;
        private bool _nodeDragStarted;

        /// <summary>树行按下（隧道先于行主体）：记录命中的节点 + 点击计数 + 拖拽起点。
        /// 就地改名编辑框内的鼠标操作归编辑框自己 → 不记节点（后续选择/进入/拖拽一律让位）。</summary>
        private void FolderTreeItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressNode = InlineNameEditor.IsWithin(e.OriginalSource as DependencyObject)
                ? null
                : (sender as FrameworkElement)?.DataContext;
            _pressNodeClickCount = e.ClickCount;
            _nodeDragStart = e.GetPosition(this);
            _nodeDragStarted = false;
        }

        /// <summary>
        /// 行主体拖动（按下 + 移动超过系统阈值）：**请求宿主启动拖拽**。
        /// 面板只报"哪个节点、哪个源元素"——选中语义（拖未选中项先单选、拖已选中项拖整个集合）与载荷构造
        /// 都在宿主/VM 侧（可复用控件不碰业务数据，回收站树因此天然不可拖）。
        /// chevron 由 ToggleButton 自捕获鼠标 → 落到 chevron 上的拖动不经这里（展开/收起绝不拖出节点）。
        /// </summary>
        private void FolderTreeItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (_pressNode == null || _nodeDragStarted) return;   // 按下不在行主体（改名编辑框内）→ 不进入拖拽
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _nodeDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _nodeDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            if ((sender as FrameworkElement) is not { DataContext: { } node } source) return;
            _nodeDragStarted = true;
            NodeDragStartRequested?.Invoke(this, new TreeItemDragStartEventArgs { Node = node, Source = source });
        }

        /// <summary>
        /// 行主体单击（chevron 由 ToggleButton 自捕获鼠标、绝不进入此路径）：
        /// 选中/进入语义由宿主（NodeSelected）决定 —— 文件夹 = 选中并进入；链接叶子 = 定位到父目录；
        /// 「全部书签」虚拟根 = 进入根目录（不写选中）。行单击与 chevron 展开物理分离，
        /// 不经容器 SelectedItemChanged（键盘/展开同通道 = 耦合）。回收站页不订阅 = 纯展示。
        /// **归属校验**：抬起必须与按下命中同一节点、且按下是单击——双击的第二击落在导航重建后
        /// 新位置节点上时绝不能再触发一次"选中+进入"（会误进入/误选另一个文件夹，与主栏同根）。
        /// **拖拽守卫**：本次手势若已进入拖拽，拖拽结束的抬起不得再补一次"选中+进入"（与主栏 <c>_dragStarted</c> 同口径）。
        /// </summary>
        private void FolderTreeItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var node = (sender as FrameworkElement)?.DataContext;
            var pressed = _pressNode;
            var clicks = _pressNodeClickCount;
            var dragged = _nodeDragStarted;
            _pressNode = null;
            _nodeDragStarted = false;

            if (node == null) return;
            if (!ReferenceEquals(node, pressed) || dragged || clicks > 1) return;
            NodeSelected?.Invoke(this, node);
        }

        private void FolderTreeItem_DragOver(object sender, DragEventArgs e)
        {
            var args = new TreeItemDragEventArgs { Args = e, Node = (sender as TreeViewItem)?.DataContext };
            NodeDragOver?.Invoke(this, args);
        }

        private void FolderTreeItem_DragLeave(object sender, DragEventArgs e)
        {
            NodeDragLeave?.Invoke(this, new TreeItemDragEventArgs
            {
                Args = e,
                Node = (sender as TreeViewItem)?.DataContext,
            });
        }

        private void FolderTreeItem_Drop(object sender, DragEventArgs e)
        {
            var args = new TreeItemDragEventArgs { Args = e, Node = (sender as TreeViewItem)?.DataContext };
            NodeDrop?.Invoke(this, args);
        }


    }
}
