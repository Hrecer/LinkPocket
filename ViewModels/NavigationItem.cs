using CommunityToolkit.Mvvm.ComponentModel;
using MaterialDesignThemes.Wpf;

namespace LinkPocket.ViewModels;

public partial class NavigationItem : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private PackIconKind _iconKind = PackIconKind.FolderOutline;

    [ObservableProperty]
    private bool _isSelected;
}
