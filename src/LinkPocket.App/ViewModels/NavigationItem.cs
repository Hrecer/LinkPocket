using CommunityToolkit.Mvvm.ComponentModel;
using Material3.Wpf;

namespace LinkPocket.ViewModels;

public partial class NavigationItem : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    /// <summary>
    /// 导航项文案的<b>键</b>（不是文本）：模板经 <c>{loc:LocKey LabelKey}</c> 在渲染边界取词，
    /// 语言一变绑定自己重算——宿主没有任何需要"记得重投影"的地方。
    /// </summary>
    [ObservableProperty]
    private string _labelKey = string.Empty;

    [ObservableProperty]
    private string _iconKind = "folder-outline";
    // 注：选中态不再是项上的标记——由 MainViewModel.SelectedNavItem（SlidingNavStrip 双向绑定）表达
}
