using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LinkPocket.ViewModels;

/// <summary>
/// 动作面模型（详情栏 / 详情页**共用基类**）：把「这一屏提供哪些动作」表达为
/// **显式能力位 + 命令槽**——界面（<c>Views.DetailSidebar</c> / <c>Views.LinkDetailPane</c>）只按能力位渲染，
/// 各页只声明动作面；界面与业务逻辑都只有一份，绝不按页复制（用户令 2026-09-19）。
/// </summary>
public class ActionSurfaceModel : INotifyPropertyChanged
{
    // —— 能力位（默认全开；只读页 / 定制页按需收窄） ——

    /// <summary>主按钮（侧栏 = 打开/详情；详情页 = 打开网站 / 还原一类主处置）。</summary>
    public bool ShowOpenAction { get; protected set; } = true;
    /// <summary>「打开网站」第二枚药丸（侧栏用；详情页的主按钮本身即打开网站/还原）。</summary>
    public bool ShowOpenWebsite { get; protected set; } = true;
    public bool ShowEditAction { get; protected set; } = true;
    public bool ShowDeleteAction { get; protected set; } = true;

    /// <summary>主按钮文案 / 提示 / 图标。</summary>
    public string OpenLabel { get; protected set; } = "打开";
    public string OpenToolTip { get; protected set; } = "打开";
    public string OpenIconKind { get; protected set; } = "open-in-new";
    /// <summary>铅笔按钮提示（链接 = 编辑；文件夹 = 重命名；回收站不用）。</summary>
    public string EditLabel { get; protected set; } = "编辑";
    /// <summary>删除按钮文案与提示（回收站 = 「永久删除」，语义更强、避免误读）。</summary>
    public string DeleteActionLabel { get; protected set; } = "删除";
    /// <summary>多选删除按钮文案与提示。</summary>
    public string DeleteSelectionLabel { get; protected set; } = "删除所选";
    /// <summary>「打开网站」按钮最终可见性（侧栏：链接且页面开启）。</summary>
    public bool ShowOpenWebsiteButton { get; protected set; } = true;
    /// <summary>主按钮跨列数（未显示「打开网站」时占满两列；侧栏用）。</summary>
    public int OpenColumnSpan { get; protected set; } = 1;

    /// <summary>动作卡是否显示（任一动作可见）。</summary>
    public bool HasActions => ShowOpenAction || ShowEditAction || ShowDeleteAction;

    // —— 命令槽（由各页注入；一律复用该页既有能力，绝不另写业务逻辑） ——

    public ICommand? OpenCommand { get; set; }
    public ICommand? OpenWebsiteCommand { get; set; }
    public ICommand? RenameCommand { get; set; }
    public ICommand? DeleteCommand { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>动作面状态变更后的统一通知（能力位 / 文案一起刷新）。</summary>
    protected void RaiseActionChanged()
    {
        OnPropertyChanged(nameof(ShowOpenAction));
        OnPropertyChanged(nameof(ShowOpenWebsite));
        OnPropertyChanged(nameof(ShowEditAction));
        OnPropertyChanged(nameof(ShowDeleteAction));
        OnPropertyChanged(nameof(OpenLabel));
        OnPropertyChanged(nameof(OpenToolTip));
        OnPropertyChanged(nameof(OpenIconKind));
        OnPropertyChanged(nameof(EditLabel));
        OnPropertyChanged(nameof(DeleteActionLabel));
        OnPropertyChanged(nameof(DeleteSelectionLabel));
        OnPropertyChanged(nameof(ShowOpenWebsiteButton));
        OnPropertyChanged(nameof(OpenColumnSpan));
        OnPropertyChanged(nameof(HasActions));
    }

    /// <summary>恢复缺省动作面（清空选中时调用）。</summary>
    protected void ResetActionSurface()
    {
        ShowOpenAction = ShowOpenWebsite = ShowEditAction = ShowDeleteAction = true;
        OpenLabel = "打开";
        OpenToolTip = "打开";
        OpenIconKind = "open-in-new";
        EditLabel = "编辑";
        DeleteActionLabel = "删除";
        DeleteSelectionLabel = "删除所选";
        ShowOpenWebsiteButton = true;
        OpenColumnSpan = 1;
    }
}
