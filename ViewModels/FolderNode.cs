using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using MaterialDesignThemes.Wpf;

namespace LinkPocket.ViewModels;

public class FolderNode : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _folderId = string.Empty;
    private string _name = string.Empty;
    private string? _parentName;
    private int _linkCount;
    private PackIconKind _iconKind = PackIconKind.FolderOutline;
    private ObservableCollection<FolderNode> _children = new();

    public string Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    public string FolderId
    {
        get => _folderId;
        set { _folderId = value; OnPropertyChanged(); }
    }

    public string? ParentId { get; set; }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string? ParentName
    {
        get => _parentName;
        set { _parentName = value; OnPropertyChanged(); }
    }

    public int LinkCount
    {
        get => _linkCount;
        set { _linkCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalLinkCount)); }
    }

    public int TotalLinkCount => _linkCount + _children.Sum(c => c.TotalLinkCount);

    public PackIconKind IconKind
    {
        get => _iconKind;
        set { _iconKind = value; OnPropertyChanged(); }
    }

    public ObservableCollection<FolderNode> Children
    {
        get => _children;
        set { _children = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
