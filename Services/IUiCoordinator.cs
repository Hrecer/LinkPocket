using System;
using System.Threading.Tasks;
using LinkPocket.Models;

namespace LinkPocket.Services
{
    /// <summary>
    /// UI 协调接口：ViewModel 通过它操控界面外观，不直接依赖 MainWindow 类型。
    /// 当前由 MainWindow 实现；未来更换 UI 时由新窗口重新实现，逻辑层无需改动。
    /// </summary>
    public interface IUiCoordinator
    {
        // —— 页面切换 ——
        /// <summary>进入编辑页：隐藏主列表与详情面板，显示编辑视图。</summary>
        void ShowEditPage();

        /// <summary>退出编辑页：returnToDetail 为 true 时回到详情面板，否则回到主列表。</summary>
        void CloseEditPage(bool returnToDetail);

        /// <summary>显示资源管理器式浏览页。</summary>
        void ShowBrowserPage();

        /// <summary>隐藏资源管理器式浏览页。</summary>
        void CloseBrowserPage();

        /// <summary>
        /// 在浏览页打开某条链接的详情（工具页/智能列表/搜索页的「跳转」统一走这里）。
        /// 老「链接」页删除后，跳转目标由它改为浏览页详情页。
        /// </summary>
        void OpenLinkInBrowser(string linkId);

        /// <summary>在浏览页定位到某个文件夹（进入该目录）。</summary>
        void OpenFolderInBrowser(string folderId);

        /// <summary>进入详情面板：隐藏主列表，显示详情视图。</summary>
        void ShowDetailView();

        /// <summary>退出详情面板：回到主列表。</summary>
        void CloseDetailView();

        // —— 详情面板 ——
        void UpdateDetailPanel(LinkItem link);
        void ClearDetailPanel();
        LinkItem? GetSelectedLink();

        // —— 侧栏与主列表 ——
        void RefreshSidebar();
        Task RefreshSidebarAsync();
        Task RefreshMainListAsync();
        void ClearMainList();
        void ExpandFolder(string folderId);
        void ClearFolderSelection();

        // —— 对话框与杂项 ——
        void FocusNewFolderDialog();
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
