namespace LinkPocket.Managers
{
    /// <summary>
    /// 浏览器页（资源管理器式浏览）的剪贴板：
    /// 载荷同时容纳文件夹与书签，IsCut 区分剪切（移动）与复制语义。
    /// （旧「链接列表页」的 LinkItem 载荷已随死代码清除删除。）
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
        private BrowserClipboardPayload? _browserPayload;

        /// <summary>浏览器页载荷。</summary>
        public BrowserClipboardPayload? BrowserPayload => _browserPayload;

        /// <summary>写入/清空浏览器页载荷；传 null 即清空。</summary>
        public void SetBrowserPayload(BrowserClipboardPayload? payload)
        {
            _browserPayload = payload;
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? ClipboardChanged;
    }
}
