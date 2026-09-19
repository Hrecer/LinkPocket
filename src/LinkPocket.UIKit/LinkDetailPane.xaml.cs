using System.Windows.Controls;

namespace LinkPocket.Views;

/// <summary>
/// 「链接详情」共享页（浏览页详情页 / 回收站只读详情页共用；纯绑定，无页面逻辑）。
/// DataContext = <see cref="ViewModels.LinkDetailPaneModel"/>（或其子类）；各页只填数据与动作面。
/// </summary>
public partial class LinkDetailPane : UserControl
{
    public LinkDetailPane() => InitializeComponent();
}
