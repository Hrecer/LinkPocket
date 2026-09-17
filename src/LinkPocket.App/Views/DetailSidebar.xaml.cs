using System.Windows.Controls;

namespace LinkPocket.Views;

/// <summary>
/// 可复用右侧详情栏（数据驱动，与 SortableDataTable 同思路的独立控件）：
/// DataContext = ViewModels.DetailSidebarModel（或其子类）。
/// 空占位 / 单选头部 / URL / 信息卡（按 Rows 集合渲染）/ 描述 / 操作 / 多选统计，
/// 全部由数据模型驱动；样式自包含，可放入任何页面（浏览页、搜索页）。
/// </summary>
public partial class DetailSidebar : UserControl
{
    public DetailSidebar()
    {
        InitializeComponent();
    }
}
