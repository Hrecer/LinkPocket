using LinkPocket.Models;

namespace LinkPocket.Managers
{
    /// <summary>
    /// 浏览器页（资源管理器式浏览）的剪贴板载荷：
    /// 同时容纳文件夹与书签，IsCut 区分剪切（移动）与复制语义。
    /// </summary>
    public class BrowserClipboardPayload
    {
        public List<string> FolderIds { get; set; } = new();
        public List<string> LinkIds { get; set; } = new();

        /// <summary>剪贴时的来源目录 ID（null = 根目录「全部书签」），用于"粘贴回原目录 = 无操作"判断与视觉回填。</summary>
        public string? SourceFolderId { get; set; }

        public bool IsCut { get; set; }

        public bool IsEmpty => FolderIds.Count == 0 && LinkIds.Count == 0;
    }

    public class ClipboardManager
    {
        private List<LinkItem>? _clipboardLinks;
        private bool _isCutOperation;
        private BrowserClipboardPayload? _browserPayload;

        public bool HasClipboard => _clipboardLinks is { Count: > 0 };
        public bool IsCut => _isCutOperation;
        public List<LinkItem>? ClipboardLinks => _clipboardLinks;
        public string SourceFolderId => _clipboardLinks?.Count > 0 ? (_clipboardLinks[0].ListId ?? string.Empty) : string.Empty;

        /// <summary>浏览器页载荷（与旧列表页剪贴板相互独立）。</summary>
        public BrowserClipboardPayload? BrowserPayload => _browserPayload;

        /// <summary>写入/清空浏览器页载荷；传 null 即清空。</summary>
        public void SetBrowserPayload(BrowserClipboardPayload? payload)
        {
            _browserPayload = payload;
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
        }

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

        public bool IsSameFolder(string folderId)
        {
            return _clipboardLinks?.Count > 0 && (_clipboardLinks[0].ListId ?? string.Empty) == folderId;
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
