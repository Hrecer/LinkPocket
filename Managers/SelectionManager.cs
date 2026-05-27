using LinkPocket.Models;

namespace LinkPocket.Managers
{
    public class SelectionManager
    {
        private string? _multiSelectFolderId;

        public bool IsInMultiSelectMode => !string.IsNullOrEmpty(_multiSelectFolderId);
        public string? MultiSelectFolderId => _multiSelectFolderId;

        public event EventHandler? MultiSelectStateChanged;

        public enum CtrlClickResult
        {
            BlockedCrossDirectory,
            Promoted,
            Allowed
        }

        public void ClearAll()
        {
            _multiSelectFolderId = null;
            MultiSelectStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public CtrlClickResult HandleCtrlClick(LinkItem targetLink, string selectedFolderId, string? selectedLinkId, out string? newSelectedLinkId)
        {
            newSelectedLinkId = null;

            if (!string.IsNullOrEmpty(selectedFolderId))
                return CtrlClickResult.BlockedCrossDirectory;

            var linkListId = targetLink.ListId ?? string.Empty;

            if (!string.IsNullOrEmpty(_multiSelectFolderId) && linkListId != _multiSelectFolderId)
                return CtrlClickResult.BlockedCrossDirectory;

            if (!string.IsNullOrEmpty(selectedLinkId))
            {
                var promotedListId = targetLink.ListId ?? string.Empty;
                if (promotedListId != linkListId)
                    return CtrlClickResult.BlockedCrossDirectory;

                _multiSelectFolderId = promotedListId;
                newSelectedLinkId = null;
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
    }
}
