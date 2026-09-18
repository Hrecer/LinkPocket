using System.Threading.Tasks;

namespace LinkPocket.Services;

/// <summary>
/// 对话框端口：ViewModel 通过它弹确认框，不直接依赖任何窗口类型。
/// 原 IUiCoordinator 的对话职责（拆分）；确认弹窗视觉走 ConfirmDialog 唯一入口，语义与文案不变。
/// </summary>
public interface IDialogService
{
    /// <summary>弹出删除文件夹确认对话框，返回用户是否确认。</summary>
    bool ConfirmDeleteFolder(string folderName);

    /// <summary>
    /// 通用确认弹窗（MVVM）：统一走 ConfirmDialog 唯一入口；
    /// Windows 口径 = 删除类文案「将 X 移入回收站吗？」，不罗列后果。
    /// iconKind 须在 LpIcons 字形表内（默认删除口径 delete-outline）。
    /// </summary>
    bool Confirm(string title, string message, string confirmText = "删除", string iconKind = "delete-outline");

    /// <summary>提示/警告弹窗（信息类，非删除色调）：失败提示等，VM 不直接依赖任何窗口类型。</summary>
    void Alert(string title, string message);
}

/// <summary>
/// 导航端口：ViewModel 通过它切页/开详情/刷新回收站，不直接依赖 MainWindow 类型。
/// 当前由 MainWindow 实现；未来更换 UI 时由新窗口重新实现，逻辑层无需改动。
/// 原 IUiCoordinator 的导航职责（拆分）。
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// 在浏览页打开某条链接的**详情页**（工具页/搜索页/智能列表的「详情」入口走这里）。
    /// ⚠️ 语义区分：「详情」= 展开该链接的详情页；「跳转」= 进入其所在目录并选中那一行，
    /// 后者是标准能力，请用 <see cref="IContentLocator"/>（Services/ContentLocator），
    /// 不要再用本方法承担跳转语义。
    /// </summary>
    void OpenLinkInBrowser(string linkId);

    /// <summary>在浏览页定位到某个文件夹（进入该目录）。</summary>
    void OpenFolderInBrowser(string folderId);

    Task RefreshTrashPageAsync();
}

/// <summary>
/// UI 端口槽位（组合根持有）：MainWindow（Shell）在构造时登记实现，
/// ViewModel 经构造注入持有本实例消费端口——不经过任何静态注册点。
/// </summary>
public sealed class UiPortProvider
{
    public IDialogService? Dialogs { get; set; }

    public INavigationService? Navigation { get; set; }
}
