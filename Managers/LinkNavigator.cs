using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LinkPocket.Models;
using LinkPocket.ViewModels;

namespace LinkPocket.Managers
{
    public class LinkNavigator
    {
        public delegate void ExpandAncestorAction(string folderId);
        public delegate void RefreshMainListAction();
        public delegate void RefreshSidebarAction(MainViewModel vm);
        public delegate void UpdateSelectionVisualsAction();
        public delegate void UpdateDetailPanelAction();
        public delegate void BringLinkIntoViewAction(string linkId);
        public delegate void BringSidebarIntoViewAction(string linkId, string? folderId);
        public delegate void ClearSearchSelectionAction();
        public delegate void BeforeNavigateAction();
        public delegate void ClearExpandedAction(string prefix);
        public delegate void AddExpandedAction(string key);

        private readonly MainViewModel _viewModel;
        private readonly Dispatcher _dispatcher;

        public ExpandAncestorAction? OnExpandAncestorFolders { get; set; }
        public RefreshMainListAction? OnRefreshMainList { get; set; }
        public RefreshSidebarAction? OnRefreshSidebar { get; set; }
        public UpdateSelectionVisualsAction? OnUpdateSelectionVisuals { get; set; }
        public UpdateDetailPanelAction? OnUpdateDetailPanel { get; set; }
        public BringLinkIntoViewAction? OnBringLinkIntoView { get; set; }
        public BringSidebarIntoViewAction? OnBringSidebarIntoView { get; set; }
        public ClearSearchSelectionAction? OnClearSearchSelection { get; set; }
        public BeforeNavigateAction? OnBeforeNavigate { get; set; }
        public ClearExpandedAction? OnClearExpanded { get; set; }
        public AddExpandedAction? OnAddExpanded { get; set; }

        public LinkNavigator(MainViewModel viewModel, Dispatcher dispatcher)
        {
            _viewModel = viewModel;
            _dispatcher = dispatcher;
        }

        public void NavigateToLinkInMainList(string linkId)
        {
            if (_viewModel.LinkViewModel == null) return;

            var targetLink = _viewModel.LinkViewModel.Links.FirstOrDefault(l => l.LinkId == linkId);
            if (targetLink == null) return;

            OnBeforeNavigate?.Invoke();
            OnClearSearchSelection?.Invoke();

            _viewModel.CurrentNavId = "links";

            _dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Delay(100);

                if (_viewModel.LinkViewModel == null) return;

                if (!_viewModel.FolderItems.Any(f => f.Id == targetLink.ListId))
                {
                    await _viewModel.RefreshFolderTreeAndUIAsync();
                }

                if (!string.IsNullOrEmpty(targetLink.ListId))
                {
                    var targetNode = FindFolderNode(_viewModel.FolderItems, targetLink.ListId);
                    if (targetNode != null)
                    {
                        OnClearExpanded?.Invoke("_main");
                        OnClearExpanded?.Invoke("_sidebar");
                        OnAddExpanded?.Invoke("_main:0");
                        OnAddExpanded?.Invoke("_sidebar:0");
                        OnExpandAncestorFolders?.Invoke(targetNode.Id);
                        OnRefreshMainList?.Invoke();
                        OnRefreshSidebar?.Invoke(_viewModel);

                        await Task.Delay(150);
                    }
                }
                else
                {
                    OnClearExpanded?.Invoke("_main");
                    OnClearExpanded?.Invoke("_sidebar");
                    OnAddExpanded?.Invoke("_main:0");
                    OnAddExpanded?.Invoke("_sidebar:0");
                    OnRefreshMainList?.Invoke();
                    OnRefreshSidebar?.Invoke(_viewModel);
                    await Task.Delay(150);
                }

                _viewModel.SelectedLinkId = targetLink.LinkId;
                OnUpdateSelectionVisuals?.Invoke();
                OnUpdateDetailPanel?.Invoke();

                OnBringLinkIntoView?.Invoke(targetLink.LinkId);
                OnBringSidebarIntoView?.Invoke(targetLink.LinkId, targetLink.ListId);
            }));
        }

        public void NavigateToLinkById(string linkId, string? listId)
        {
            if (_viewModel.LinkViewModel == null) return;

            OnBeforeNavigate?.Invoke();
            OnClearSearchSelection?.Invoke();

            _viewModel.CurrentNavId = "links";
            var capturedListId = listId;

            _dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Delay(100);

                if (_viewModel.LinkViewModel == null) return;

                if (!_viewModel.FolderItems.Any(f => f.Id == capturedListId))
                {
                    await _viewModel.RefreshFolderTreeAndUIAsync();
                }

                if (!string.IsNullOrEmpty(capturedListId))
                {
                    var targetNode = FindFolderNode(_viewModel.FolderItems, capturedListId);
                    if (targetNode != null)
                    {
                        OnClearExpanded?.Invoke("_main");
                        OnClearExpanded?.Invoke("_sidebar");
                        OnAddExpanded?.Invoke("_main:0");
                        OnAddExpanded?.Invoke("_sidebar:0");
                        OnExpandAncestorFolders?.Invoke(targetNode.Id);
                        OnRefreshMainList?.Invoke();
                        OnRefreshSidebar?.Invoke(_viewModel);

                        await Task.Delay(150);
                    }
                }
                else
                {
                    OnClearExpanded?.Invoke("_main");
                    OnClearExpanded?.Invoke("_sidebar");
                    OnAddExpanded?.Invoke("_main:0");
                    OnAddExpanded?.Invoke("_sidebar:0");
                    OnRefreshMainList?.Invoke();
                    OnRefreshSidebar?.Invoke(_viewModel);
                    await Task.Delay(150);
                }

                _viewModel.SelectedLinkId = linkId;
                OnUpdateSelectionVisuals?.Invoke();
                OnUpdateDetailPanel?.Invoke();

                OnBringLinkIntoView?.Invoke(linkId);
                OnBringSidebarIntoView?.Invoke(linkId, capturedListId);
            }));
        }

        public void NavigateToFolderById(string folderId)
        {
            OnBeforeNavigate?.Invoke();
            OnClearSearchSelection?.Invoke();

            _viewModel.CurrentNavId = "links";
            _viewModel.LinkViewModel?.ClearSelectionCommand.Execute(null);

            _dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Delay(100);

                if (!_viewModel.FolderItems.Any(f => f.Id == folderId))
                    await _viewModel.RefreshFolderTreeAndUIAsync();

                _viewModel.SelectFolder(folderId);

                OnClearExpanded?.Invoke("_main");
                OnClearExpanded?.Invoke("_sidebar");
                OnAddExpanded?.Invoke("_main:0");
                OnAddExpanded?.Invoke("_sidebar:0");
                OnExpandAncestorFolders?.Invoke(folderId);
                OnRefreshMainList?.Invoke();
                OnRefreshSidebar?.Invoke(_viewModel);

                await Task.Delay(150);

                OnUpdateSelectionVisuals?.Invoke();
                OnUpdateDetailPanel?.Invoke();
                OnBringSidebarIntoView?.Invoke("", folderId);
            }));
        }

        private static FolderNode? FindFolderNode(System.Collections.ObjectModel.ObservableCollection<FolderNode> nodes, string id)
        {
            foreach (var node in nodes)
            {
                if (node.Id == id) return node;
                var found = FindFolderNode(node.Children, id);
                if (found != null) return found;
            }
            return null;
        }
    }
}
