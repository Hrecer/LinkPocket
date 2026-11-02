using System.Threading.Tasks;
using LinkPocket.I18n;

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
    /// <param name="confirmText">确认键文案；<c>null</c> = 按当前语言取「删除」。
    /// 默认值写成 null 是因为默认参数必须是编译期常量——运行时取词不能放在签名里。</param>
    bool Confirm(string title, string message, string? confirmText = null, string iconKind = "delete-outline");

    /// <summary>提示/警告弹窗（信息类，非删除色调）：失败提示等，VM 不直接依赖任何窗口类型。</summary>
    void Alert(string title, string message);
}

/// <summary>
/// 浏览页编辑态端口：跨页动作在**执行前**询问"这个实体是不是正在被编辑"。
/// **编辑优先**：正在就地改名 / 整页编辑的实体，任何 UI 入口都不得删除或挪动它；
/// 编辑本身保持不变，由调用方给出一次性提示。只读详情覆盖层不算编辑。
/// </summary>
public interface IBrowseEditState
{
    /// <summary>浏览页是否有编辑面打开（就地改名 / 整页编辑器）。</summary>
    bool IsEditing { get; }

    /// <summary>正在编辑的实体 ID（文件夹或链接）；新建链接（尚无实体）时为 null。</summary>
    string? EditingEntityId { get; }

    /// <summary>指定实体是否正在被编辑。</summary>
    bool IsEditingEntity(string id);
}

/// <summary>
/// 一次性提示端口：**全站共用的 Toast（轻提示）栈**（底部居中、最多 3 条、
/// 新提示在底部堆叠、1.8 秒后快速淡出、不挡操作）。
/// 阻断类提示（编辑期间拒绝跳转/删除）与一次性事件（外部变更、目录消失）都走这一条；
/// 校验错误与真正失败仍走各页既有错误面（横幅 / 状态行 / 确认弹窗）。
/// </summary>
public interface IUiNoticeService
{
    /// <summary>显示一条已取好词的提示（调用方负责 i18n；空串不显示）。</summary>
    void Show(string text);
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

    /// <summary>切到搜索页（全局快捷键 Ctrl+E / Ctrl+F；错误：如空串不动作）。</summary>
    void NavigateToSearch();

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

    /// <summary>浏览页编辑态（跨页破坏性动作的编辑优先闸）。</summary>
    public IBrowseEditState? BrowseEdit { get; set; }

    /// <summary>全站共用的一次性提示条（Shell 单实例）。</summary>
    public IUiNoticeService? Notices { get; set; }
}
