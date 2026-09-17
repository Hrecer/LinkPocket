using System.Windows.Controls;

namespace LinkPocket.Views.Browser;

/// <summary>
/// 浏览模块「链接详情页」视图。DataContext 为 LinkDetailPageViewModel；
/// 行为全部由 VM 承担（打开网站 / 复制 / 编辑 / 返回），code-behind 无逻辑。
/// </summary>
public partial class LinkDetailPage : UserControl
{
    public LinkDetailPage()
    {
        InitializeComponent();
    }
}
