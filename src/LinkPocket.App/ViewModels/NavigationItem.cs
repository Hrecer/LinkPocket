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

    [ObservableProperty]
    private bool _isSelected;
}
