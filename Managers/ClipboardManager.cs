using LinkPocket.Models;

namespace LinkPocket.Managers
{
    public class ClipboardManager
    {
        private List<LinkItem>? _clipboardLinks;
        private bool _isCutOperation;

        public bool HasClipboard => _clipboardLinks is { Count: > 0 };
        public bool IsCut => _isCutOperation;
        public List<LinkItem>? ClipboardLinks => _clipboardLinks;
        public int SourceFolderId => _clipboardLinks?.Count > 0 ? (_clipboardLinks[0].ListId ?? -1) : -1;

        public event EventHandler? ClipboardChanged;

        public void Copy(List<LinkItem> items)
        {
            ClearInternal();
            _clipboardLinks = items.ToList();
            _isCutOperation = false;
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Cut(List<LinkItem> items)
        {
            ClearInternal();
            _clipboardLinks = items.ToList();
            _isCutOperation = true;
            foreach (var item in _clipboardLinks)
                item.IsCut = true;
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            ClearInternal();
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
        }

        public void AfterPaste()
        {
            ClearInternal();
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool IsSameFolder(int folderId)
        {
            return _clipboardLinks?.Count > 0 && (_clipboardLinks[0].ListId ?? -1) == folderId;
        }

        private void ClearInternal()
        {
            if (_isCutOperation && _clipboardLinks != null)
            {
                foreach (var item in _clipboardLinks)
                    item.IsCut = false;
            }
            _clipboardLinks = null;
            _isCutOperation = false;
        }
    }
}
