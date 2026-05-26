using LinkPocket.Models;

namespace LinkPocket.Managers
{
    public class SelectionManager
    {
        private LinkItem? _currentSelectedLink;
        private string? _multiSelectFolderId;
        private string _selectedFolderId = string.Empty;
        private string _activeFolderId = string.Empty;

        public LinkItem? CurrentSelectedLink => _currentSelectedLink;
        public bool IsInMultiSelectMode => !string.IsNullOrEmpty(_multiSelectFolderId);
        public string? MultiSelectFolderId => _multiSelectFolderId;
        public string SelectedFolderId => _selectedFolderId;
        public string ActiveFolderId => _activeFolderId;

        public event EventHandler<LinkItem?>? CurrentLinkChanged;
        public event EventHandler? MultiSelectStateChanged;
        public event EventHandler? FolderSelectionChanged;

        public enum CtrlClickResult
        {
            BlockedCrossDirectory,
            Promoted,
            Allowed
        }

        public void ClearAll()
        {
            _currentSelectedLink = null;
            _multiSelectFolderId = null;
            _selectedFolderId = string.Empty;
            _activeFolderId = string.Empty;
            NotifyAllCleared();
        }

        public void HandleSingleClick(LinkItem link)
        {
            _multiSelectFolderId = null;

            if (_currentSelectedLink?.LinkId == link.LinkId)
            {
                _currentSelectedLink = null;
                _selectedFolderId = string.Empty;
                _activeFolderId = string.Empty;
            }
            else
            {
                _currentSelectedLink = link;
                _selectedFolderId = string.Empty;
                _activeFolderId = link.ListId ?? string.Empty;
            }
            CurrentLinkChanged?.Invoke(this, _currentSelectedLink);
        }

        public CtrlClickResult HandleCtrlClick(LinkItem targetLink)
        {
            if (!string.IsNullOrEmpty(_selectedFolderId))
                return CtrlClickResult.BlockedCrossDirectory;

            var linkListId = targetLink.ListId ?? string.Empty;

            if (!string.IsNullOrEmpty(_multiSelectFolderId) && linkListId != _multiSelectFolderId)
                return CtrlClickResult.BlockedCrossDirectory;

            if (_currentSelectedLink != null)
            {
                var promotedListId = _currentSelectedLink.ListId ?? string.Empty;
                if (promotedListId != linkListId)
                    return CtrlClickResult.BlockedCrossDirectory;

                _multiSelectFolderId = promotedListId;
                _currentSelectedLink = null;
                CurrentLinkChanged?.Invoke(this, null);
                return CtrlClickResult.Promoted;
            }

            if (string.IsNullOrEmpty(_multiSelectFolderId))
            {
                _multiSelectFolderId = linkListId;
            }
            return CtrlClickResult.Allowed;
        }

        public void NotifyMultiSelectEnded()
        {
            _multiSelectFolderId = null;
        }

        public void ClearMultiSelectOnly()
        {
            _multiSelectFolderId = null;
            MultiSelectStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SelectFolder(string folderId)
        {
            _selectedFolderId = folderId;
            _activeFolderId = folderId;
            _multiSelectFolderId = null;
            _currentSelectedLink = null;
            FolderSelectionChanged?.Invoke(this, EventArgs.Empty);
            CurrentLinkChanged?.Invoke(this, null);
        }

        public void ClearFolderSelection()
        {
            _selectedFolderId = string.Empty;
            _activeFolderId = string.Empty;
            _currentSelectedLink = null;
            _multiSelectFolderId = null;
            FolderSelectionChanged?.Invoke(this, EventArgs.Empty);
            CurrentLinkChanged?.Invoke(this, null);
        }

        public void ClearCurrentSelectedLink()
        {
            _currentSelectedLink = null;
            CurrentLinkChanged?.Invoke(this, null);
        }

        private void NotifyAllCleared()
        {
            MultiSelectStateChanged?.Invoke(this, EventArgs.Empty);
            FolderSelectionChanged?.Invoke(this, EventArgs.Empty);
            CurrentLinkChanged?.Invoke(this, null);
        }
    }
}
