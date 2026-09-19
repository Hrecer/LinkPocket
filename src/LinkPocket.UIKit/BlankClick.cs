using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LinkPocket.Views;

/// <summary>
/// 「点空白清选中 / 清焦点」的**唯一实现**（附加行为，按区域挂载）：
/// 用法 <c>views:BlankClick.Command="{Binding ClearPageSelectionCommand}"</c>；
/// 挂在哪个区域，就只有"落在该区域、且没命中任何可交互元素"的单击才会执行该命令。
///
/// **为什么按区域挂、不在公共祖先上统一挂**：外层处理器会把**子区域**的点击一起当成空白，
/// 吞掉子区域的点击语义（见 文档/WARNINGS.md 第 30 条）。本行为用"命中链归属"判定空白，
/// 每个区域只为自己范围内的空白负责；命中后置 <c>e.Handled</c>，外层区域不重复处理。
///
/// **归属校验**（同 WARNINGS 第 31 条）：必须在**同一次手势**里"按下与抬起都落空白"且"按下是单击"
/// ——双击打开文件夹会重建列表，第二击的抬起可能落在新位置的空白处，那种抬起绝不能当作点空白清选中。
/// </summary>
public static class BlankClick
{
    private sealed class Gesture
    {
        public bool PressedOnBlank;
        public int ClickCount;
    }

    private static readonly ConditionalWeakTable<UIElement, Gesture> Gestures = new();

    /// <summary>区域命令：非空即启用本行为（空 = 摘除处理）。</summary>
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached(
            "Command", typeof(ICommand), typeof(BlankClick), new PropertyMetadata(null, OnCommandChanged));

    /// <summary>「本元素是交互面，不算空白」的显式声明——给**不是控件、但自带鼠标语义**的元素用
    /// （如面包屑地址栏胶囊：自有"点空白即编辑"，点它绝不能顺手清掉列表选中）。</summary>
    public static readonly DependencyProperty IsInteractiveProperty =
        DependencyProperty.RegisterAttached(
            "IsInteractive", typeof(bool), typeof(BlankClick), new PropertyMetadata(false));

    public static void SetCommand(DependencyObject element, ICommand? value)
        => element.SetValue(CommandProperty, value);

    public static ICommand? GetCommand(DependencyObject element)
        => (ICommand?)element.GetValue(CommandProperty);

    public static void SetIsInteractive(DependencyObject element, bool value)
        => element.SetValue(IsInteractiveProperty, value);

    public static bool GetIsInteractive(DependencyObject element)
        => (bool)element.GetValue(IsInteractiveProperty);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement region) return;
        region.PreviewMouseLeftButtonDown -= OnRegionPreviewMouseLeftButtonDown;
        region.MouseLeftButtonUp -= OnRegionMouseLeftButtonUp;
        if (e.NewValue is ICommand)
        {
            region.PreviewMouseLeftButtonDown += OnRegionPreviewMouseLeftButtonDown;
            region.MouseLeftButtonUp += OnRegionMouseLeftButtonUp;
        }
        else
        {
            Gestures.Remove(region);
        }
    }

    private static void OnRegionPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var region = (UIElement)sender;
        var gesture = Gestures.GetOrCreateValue(region);
        gesture.PressedOnBlank = IsBlank(e.OriginalSource as DependencyObject, region);
        gesture.ClickCount = e.ClickCount;
    }

    private static void OnRegionMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var region = (UIElement)sender;
        if (!Gestures.TryGetValue(region, out var gesture) || !gesture.PressedOnBlank || gesture.ClickCount > 1) return;
        gesture.PressedOnBlank = false;   // 覆盖式更新：本次手势只处理一次（铁律 9）
        if (!IsBlank(e.OriginalSource as DependencyObject, region)) return;
        if (GetCommand(region) is not { } command) return;
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;   // 命中即消费：外层区域不再重复处理
    }

    /// <summary>命中链归属：从命中元素向上走到区域本身，途中只要经过可交互元素就不算空白。</summary>
    private static bool IsBlank(DependencyObject? hit, UIElement region)
    {
        for (var element = hit; element != null; element = ParentOf(element))
        {
            if (IsInteractive(element)) return false;
            if (ReferenceEquals(element, region)) return true;
        }
        return false;   // 命中不在本区域内（或命中链已断）
    }

    private static DependencyObject? ParentOf(DependencyObject element)
        => element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);

    /// <summary>可交互元素（命中即"不是空白"）：显式声明的交互面 / 按钮 / 文本（含只读可拖选文本）/
    /// 滚动条与滑块与分隔条 / 树节点与列表项 / 菜单项 / 下拉框，以及**行容器**——
    /// 本仓约定：行外层 Border 带字符串 <c>Tag</c>（<c>Tag="BrowserRow"</c> / <c>"TrashRow"</c>），
    /// 行内空白属于行，不属于"页面空白"。</summary>
    private static bool IsInteractive(DependencyObject element) => element switch
    {
        _ when GetIsInteractive(element) => true,
        ButtonBase => true,
        TextBoxBase => true,      // 只读展示型文本同样算：点击是在选文本，不是点空白
        PasswordBox => true,
        ComboBox => true,
        MenuItem => true,
        ScrollBar => true,
        Thumb => true,
        Slider => true,
        TreeViewItem => true,
        ListBoxItem => true,
        TabItem => true,
        FrameworkElement { Tag: string } => true,
        _ => false
    };
}
