using System.Windows;
using System.Windows.Controls;

namespace LinkPocket.Views;

/// <summary>
/// 列表区加载遮罩（半透明白蒙层 + 环形进度）：**唯一实现**（浏览页 / 回收站 / 搜索页 / 智能列表 / 去重明细共用）。
/// <see cref="IsBusy"/> 由宿主绑"导航加载"标志（用户发起的刷新：打开文件夹 / 跳转 / 返回 / F5）；
/// 事件驱动的后台刷新一律不亮（静默）——见 <see cref="RowEntrance"/> 的同源说明与 BEHAVIOR-CONTRACT §1.5。
/// </summary>
public partial class ListBusyOverlay : UserControl
{
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(ListBusyOverlay), new PropertyMetadata(false));

    /// <summary>是否显示遮罩（true = 白蒙层 + 环形进度）。</summary>
    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    public ListBusyOverlay() => InitializeComponent();
}
