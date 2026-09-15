using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinkPocket.ViewModels;

namespace LinkPocket.Views.Browser;

/// <summary>
/// 资源管理器式浏览页（P4）。视图只负责渲染与鼠标交互转发，
/// 业务逻辑全部在 BrowserViewModel（数据经协议、行状态在行 VM 上）。
/// </summary>
public partial class BrowserView : UserControl
{
    public BrowserView()
    {
        InitializeComponent();
    }

    private BrowserViewModel? ViewModel => DataContext as BrowserViewModel;

    /// <summary>点击空白处清除选中（命中行内元素时不处理，由行命令负责）。</summary>
    private void ContentArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var hitTest = VisualTreeHelper.HitTest((Visual)sender, e.GetPosition((IInputElement)sender));
        if (hitTest?.VisualHit == null || !IsBrowserRow(hitTest.VisualHit))
            ViewModel?.ClearSelection();
    }

    private static bool IsBrowserRow(DependencyObject element)
    {
        while (element != null)
        {
            if (element is Border border && "BrowserRow".Equals(border.Tag as string))
                return true;
            if (element is Visual)
                element = VisualTreeHelper.GetParent(element);
            else
                break;
        }
        return false;
    }
}
