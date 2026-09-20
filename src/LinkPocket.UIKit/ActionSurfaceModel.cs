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
    /// <summary>独立的「重命名」图标钮（侧栏：链接就地改标题；缺省不显示，只有浏览页侧栏的链接开它）。
    /// 与 <see cref="ShowEditAction"/> 是两件事：铅笔「编辑」= 打开整页编辑器，本钮 = 就地改标题。</summary>
    public bool ShowRenameAction { get; protected set; }
    public bool ShowDeleteAction { get; protected set; } = true;
    /// <summary>「还原」动作（回收站：详情页第二枚药丸 / 侧栏图标钮；缺省不显示）。</summary>
    public bool ShowRestoreAction { get; protected set; }
    /// <summary>「还原到根目录」动作（同上；缺省不显示）。</summary>
    public bool ShowRestoreToRootAction { get; protected set; }
    /// <summary>「跳转」动作（进目标所在目录并选中该行；经 <c>IContentLocator</c>）。
    /// 缺省不显示——只给"单一目标"的结果页/侧栏开（搜索页单选、智能列表结果页、查重明细）；
    /// 多选时不显示（跳转只对单个目标有意义，用户令 2026-09-20）。</summary>
    public bool ShowJumpAction { get; protected set; }

    /// <summary>主药丸色调（详情页三枚等大药丸：回收站把「打开」降为浅紫，深紫留给主处置「还原」）。</summary>
    public PillTone OpenTone { get; protected set; } = PillTone.Primary;

    /// <summary>动作卡排布：true = **两行**（第一行药丸 / 第二行图标钮靠右）。
    /// 右栏只有 286 宽时，"两枚药丸 + 三枚 32 图标钮"挤在同一行会把药丸压到裁字
    /// （用户定稿：回收站右栏排两行）；缺省 false = 一行（药丸填满余宽 + 图标钮靠右）。
    /// **只是排布差异**：按钮定义只有一份（见 Views/DetailSidebar.xaml 的两个宿主共用同一对模板）。</summary>
    public bool StackedActions { get; protected set; }

    /// <summary>主按钮文案 / 提示 / 图标。</summary>
    public string OpenLabel { get; protected set; } = "打开";
    public string OpenToolTip { get; protected set; } = "打开";
    public string OpenIconKind { get; protected set; } = "open-in-new";
    /// <summary>铅笔按钮提示（链接 = 编辑；文件夹 = 重命名；回收站不用）。</summary>
    public string EditLabel { get; protected set; } = "编辑";
    /// <summary>铅笔槽取**强调色**（`AccentBtn` 深紫）：文件夹时该槽 = 「重命名」，取色与侧栏那枚独立
    /// 「重命名」图标钮**同源**（同一动作在两种呈现上必须同色——曾出现文件夹那枚是主题 `Primary` 亮紫、
    /// 与链接那枚不一致）；链接时该槽 = 「编辑」，用图标钮缺省的 `Primary`。缺省 false = `Primary`。</summary>
    public bool EditActionAccent { get; protected set; }
    /// <summary>「重命名」图标钮提示（同款 32×32 铅笔，仅取色与「编辑」区分：重命名 = AccentBtn 深紫）。</summary>
    public string RenameActionLabel { get; protected set; } = "重命名";
    /// <summary>删除按钮文案与提示（回收站 = 「永久删除」，语义更强、避免误读）。</summary>
    public string DeleteActionLabel { get; protected set; } = "删除";
    /// <summary>多选删除按钮文案与提示。</summary>
    public string DeleteSelectionLabel { get; protected set; } = "删除所选";
    /// <summary>「还原」文案 / 提示 / 图标 / 色调（到删除前所在位置）。</summary>
    public string RestoreLabel { get; protected set; } = "还原";
    public string RestoreToolTip { get; protected set; } = "还原到删除前所在位置";
    public string RestoreIconKind { get; protected set; } = "restore";
    public PillTone RestoreTone { get; protected set; } = PillTone.Primary;

    /// <summary>「还原到根目录」文案 / 提示 / 图标 / 色调。</summary>
    public string RestoreToRootLabel { get; protected set; } = "还原到根目录";
    public string RestoreToRootToolTip { get; protected set; } = "还原到根目录";
    public string RestoreToRootIconKind { get; protected set; } = "backup-restore";
    public PillTone RestoreToRootTone { get; protected set; } = PillTone.Tonal;

    /// <summary>「跳转」文案 / 提示 / 图标（进目录 + 选中行；**与"外部打开"无关**，故图标不是 open-in-new）。
    /// 提示文案就是「跳转」（用户令 2026-09-20：不要长句子）；图标 = 地图图钉 `map-marker`（"在哪儿"一眼可读）。</summary>
    public string JumpLabel { get; protected set; } = "跳转";
    public string JumpToolTip { get; protected set; } = "跳转";
    public string JumpIconKind { get; protected set; } = "map-marker";

    /// <summary>「打开网站」按钮最终可见性（侧栏：链接且页面开启）。</summary>
    public bool ShowOpenWebsiteButton { get; protected set; } = true;
    /// <summary>主按钮跨列数（未显示「打开网站」时占满两列；侧栏用）。</summary>
    public int OpenColumnSpan { get; protected set; } = 1;

    /// <summary>动作卡是否显示（任一动作可见）。</summary>
    public bool HasActions => ShowOpenAction || ShowEditAction || ShowRenameAction || ShowDeleteAction
        || ShowRestoreAction || ShowRestoreToRootAction || ShowJumpAction;

    // —— 命令槽（由各页注入；一律复用该页既有能力，绝不另写业务逻辑） ——

    public ICommand? OpenCommand { get; set; }
    public ICommand? OpenWebsiteCommand { get; set; }
    public ICommand? RenameCommand { get; set; }
    /// <summary>「重命名」图标钮的命令（就地改标题）。与 <see cref="RenameCommand"/>（铅笔槽，页面自定义）分开：
    /// 浏览页侧栏的铅笔槽 = 链接开编辑器 / 文件夹就地改名，本槽 = 链接就地改名。</summary>
    public ICommand? RenameActionCommand { get; set; }
    public ICommand? DeleteCommand { get; set; }
    public ICommand? RestoreCommand { get; set; }
    public ICommand? RestoreToRootCommand { get; set; }
    /// <summary>「跳转」的命令（进目录 + 选中行）——各页挂 <c>IContentLocator</c> 的定位入口。</summary>
    public ICommand? JumpCommand { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>动作面状态变更后的统一通知（能力位 / 文案一起刷新）。</summary>
    protected void RaiseActionChanged()
    {
        OnPropertyChanged(nameof(ShowOpenAction));
        OnPropertyChanged(nameof(ShowOpenWebsite));
        OnPropertyChanged(nameof(ShowEditAction));
        OnPropertyChanged(nameof(ShowRenameAction));
        OnPropertyChanged(nameof(ShowDeleteAction));
        OnPropertyChanged(nameof(OpenLabel));
        OnPropertyChanged(nameof(OpenToolTip));
        OnPropertyChanged(nameof(OpenIconKind));
        OnPropertyChanged(nameof(EditLabel));
        OnPropertyChanged(nameof(EditActionAccent));
        OnPropertyChanged(nameof(RenameActionLabel));
        OnPropertyChanged(nameof(DeleteActionLabel));
        OnPropertyChanged(nameof(DeleteSelectionLabel));
        OnPropertyChanged(nameof(ShowRestoreAction));
        OnPropertyChanged(nameof(ShowRestoreToRootAction));
        OnPropertyChanged(nameof(ShowJumpAction));
        OnPropertyChanged(nameof(OpenTone));
        OnPropertyChanged(nameof(StackedActions));
        OnPropertyChanged(nameof(RestoreLabel));
        OnPropertyChanged(nameof(RestoreToolTip));
        OnPropertyChanged(nameof(RestoreIconKind));
        OnPropertyChanged(nameof(RestoreTone));
        OnPropertyChanged(nameof(RestoreToRootLabel));
        OnPropertyChanged(nameof(RestoreToRootToolTip));
        OnPropertyChanged(nameof(RestoreToRootIconKind));
        OnPropertyChanged(nameof(RestoreToRootTone));
        OnPropertyChanged(nameof(JumpLabel));
        OnPropertyChanged(nameof(JumpToolTip));
        OnPropertyChanged(nameof(JumpIconKind));
        OnPropertyChanged(nameof(ShowOpenWebsiteButton));
        OnPropertyChanged(nameof(OpenColumnSpan));
        OnPropertyChanged(nameof(HasActions));
    }

    /// <summary>恢复缺省动作面（清空选中时调用）。</summary>
    protected void ResetActionSurface()
    {
        ShowOpenAction = ShowOpenWebsite = ShowEditAction = ShowDeleteAction = true;
        ShowRenameAction = false;
        ShowRestoreAction = ShowRestoreToRootAction = false;
        ShowJumpAction = false;
        OpenLabel = "打开";
        OpenToolTip = "打开";
        OpenIconKind = "open-in-new";
        OpenTone = PillTone.Primary;
        StackedActions = false;
        EditLabel = "编辑";
        EditActionAccent = false;
        RenameActionLabel = "重命名";
        DeleteActionLabel = "删除";
        DeleteSelectionLabel = "删除所选";
        RestoreLabel = "还原";
        RestoreToolTip = "还原到删除前所在位置";
        RestoreIconKind = "restore";
        RestoreTone = PillTone.Primary;
        RestoreToRootLabel = "还原到根目录";
        RestoreToRootToolTip = "还原到根目录";
        RestoreToRootIconKind = "backup-restore";
        RestoreToRootTone = PillTone.Tonal;
        JumpLabel = "跳转";
        JumpToolTip = "跳转";
        JumpIconKind = "map-marker";
        ShowOpenWebsiteButton = true;
        OpenColumnSpan = 1;
    }
}
