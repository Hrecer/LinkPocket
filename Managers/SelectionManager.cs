using LinkPocket.Models;

namespace LinkPocket.Managers
{
    public class SelectionManager
    {
        private LinkItem? _currentSelectedLink;
        private int? _multiSelectFolderId;
        private int _selectedFolderId = -1;
        private int _activeFolderId = -1;

        public LinkItem? CurrentSelectedLink => _currentSelectedLink;
        public bool IsInMultiSelectMode => _multiSelectFolderId.HasValue;
        public int? MultiSelectFolderId => _multiSelectFolderId;
        public int SelectedFolderId => _selectedFolderId;
        public int ActiveFolderId => _activeFolderId;

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
            _selectedFolderId = -1;
            _activeFolderId = -1;
            NotifyAllCleared();
        }

        public void HandleSingleClick(LinkItem link)
        {
            _multiSelectFolderId = null;

            if (_currentSelectedLink?.Id == link.Id)
            {
                _currentSelectedLink = null;
                _selectedFolderId = -1;
                _activeFolderId = -1;
            }
            else
            {
                _currentSelectedLink = link;
                _selectedFolderId = -1;
                _activeFolderId = link.ListId ?? 0;
            }
            CurrentLinkChanged?.Invoke(this, _currentSelectedLink);
        }

        public CtrlClickResult HandleCtrlClick(LinkItem targetLink)
        {
            if (_selectedFolderId >= 0)
                return CtrlClickResult.BlockedCrossDirectory;

            var linkListId = targetLink.ListId ?? -1;

            if (_multiSelectFolderId.HasValue && linkListId != _multiSelectFolderId.Value)
                return CtrlClickResult.BlockedCrossDirectory;

            if (_currentSelectedLink != null)
            {
                var promotedListId = _currentSelectedLink.ListId ?? -1;
                if (promotedListId != linkListId)
                    return CtrlClickResult.BlockedCrossDirectory;

                _multiSelectFolderId = promotedListId;
                _currentSelectedLink = null;
                CurrentLinkChanged?.Invoke(this, null);
                return CtrlClickResult.Promoted;
            }

            if (!_multiSelectFolderId.HasValue)
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

        public void SelectFolder(int folderId)
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
            _selectedFolderId = -1;
            _activeFolderId = -1;
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
