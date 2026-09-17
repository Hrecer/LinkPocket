using System.Windows.Controls;

namespace LinkPocket.Views.Browser;

/// <summary>
/// 「新建/编辑链接」整页编辑器（覆盖层），与链接详情页同版式；数据由 LinkEditorViewModel 驱动。
/// </summary>
public partial class LinkEditorPage : UserControl
{
    public LinkEditorPage()
    {
        InitializeComponent();
    }
}
