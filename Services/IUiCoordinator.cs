using System;
using System.Threading.Tasks;

namespace LinkPocket.Services
{
    /// <summary>
    /// UI 协调接口：ViewModel 通过它操控界面外观，不直接依赖 MainWindow 类型。
    /// 当前由 MainWindow 实现；未来更换 UI 时由新窗口重新实现，逻辑层无需改动。
    /// 老「链接」页的 16 个占位成员（ShowEditPage/ClearDetailPanel 等）已随死代码清除删除。
    /// </summary>
    public interface IUiCoordinator
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

        void ShowNavigationTabs();

        /// <summary>弹出删除文件夹确认对话框，返回用户是否确认。</summary>
        bool ConfirmDeleteFolder(string folderName);
    }

    /// <summary>UI 协调器的注册点：MainWindow（或未来的新 UI）在构造时注册自身。</summary>
    public static class UiCoordinator
    {
        public static IUiCoordinator? Instance { get; set; }
    }
}
