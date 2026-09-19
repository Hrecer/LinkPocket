using CommunityToolkit.Mvvm.ComponentModel;
using Material3.Wpf;

namespace LinkPocket.ViewModels;

public partial class NavigationItem : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _iconKind = "folder-outline";
    // 注：选中态不再是项上的标记——由 MainViewModel.SelectedNavItem（SlidingNavStrip 双向绑定）表达
}
