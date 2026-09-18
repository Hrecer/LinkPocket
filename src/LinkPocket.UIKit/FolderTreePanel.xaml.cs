using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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

        /// <summary>拖拽经过节点（宿主设置 e.Args.Effects；不订阅 = 不接受拖放）。</summary>
        public event EventHandler<TreeItemDragEventArgs>? NodeDragOver;

        /// <summary>落放到节点。</summary>
        public event EventHandler<TreeItemDragEventArgs>? NodeDrop;

        private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
            => NodeSelected?.Invoke(this, e.NewValue);

        private void FolderTreeItem_DragOver(object sender, DragEventArgs e)
        {
            var args = new TreeItemDragEventArgs { Args = e, Node = (sender as TreeViewItem)?.DataContext };
            NodeDragOver?.Invoke(this, args);
        }

        private void FolderTreeItem_Drop(object sender, DragEventArgs e)
        {
            var args = new TreeItemDragEventArgs { Args = e, Node = (sender as TreeViewItem)?.DataContext };
            NodeDrop?.Invoke(this, args);
        }

        /// <summary>
    /// 按 folderId 选中节点并展开沿途祖先（浏览页 CurrentFolderId 同步）；folderId 也可为根级链接叶子的
    /// 链接 ID（链接叶子 FolderId=null、Id=linkId，见 MatchesId）——点击链接叶子定位后树保持高亮该叶子。
    /// 程序化选中会触发 NodeSelected，宿主自行用守卫抑制。
    /// 注意：树容器是惰性生成的——未展开的子级无法程序化选中（与旧版行为一致）。
    /// </summary>
    public bool SelectNodeById(string? folderId)
    {
        return SelectAmong(FolderTreeControl.Items, folderId, FolderTreeControl.ItemContainerGenerator);
    }

        private bool SelectAmong(ItemCollection items, string? folderId, ItemContainerGenerator generator)
        {
            foreach (var item in items)
            {
                if (generator.ContainerFromItem(item) is not TreeViewItem container)
                    continue;

                if (MatchesId(item, folderId))
                {
                    container.IsSelected = true;
                    return true;
                }

                // 递归子级：子级容器由父容器的 generator 管理
                if (SelectAmong(container.Items, folderId, container.ItemContainerGenerator))
                {
                    container.IsExpanded = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>ID 属性反射缓存：只按类型缓存一次（避免每次遍历都反射），
    /// 且不引入 UIKit → UI.Browser/UI.Trash 的引用（审阅建议的 switch 模式匹配会强引用这两个类型，
    /// 破坏分层，故采用缓存反射实现同等优化）。三元组 = (FolderId, TrashFolderId, Id)。
    /// 匹配优先级：FolderId（文件夹）→ TrashFolderId（回收站单元）→ Id（根级链接叶子 FolderId=null）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type,
        (System.Reflection.PropertyInfo? Folder, System.Reflection.PropertyInfo? Trash, System.Reflection.PropertyInfo? Link)> IdPropsCache = new();

    private static bool MatchesId(object item, string? id)
    {
        if (id == null) return false;
        var props = IdPropsCache.GetOrAdd(item.GetType(), t => (
            t.GetProperty("FolderId"),
            t.GetProperty("TrashFolderId"),
            t.GetProperty("Id")));
        var folder = props.Folder?.GetValue(item) as string;
        if (folder != null) return string.Equals(folder, id, StringComparison.Ordinal);
        var trash = props.Trash?.GetValue(item) as string;
        if (trash != null) return string.Equals(trash, id, StringComparison.Ordinal);
        var link = props.Link?.GetValue(item) as string;   // 链接叶子：FolderId=null、Id=linkId
        return string.Equals(link, id, StringComparison.Ordinal);
    }
    }
}
