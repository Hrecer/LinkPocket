namespace LinkPocket.ViewModels;

/// <summary>
/// 浏览页**就地重命名会话（控制器）**：从 BrowserViewModel 抽出的状态机——
/// 唯一事实来源 = 「目标 ID + 是否文件夹 + 编辑面 + 原名 + 编辑文本」；
/// 行与树上的 IsRenaming 全部是它的投影（与 IsSelected 同构），绝不各自持一份"我在编辑"的标记。
///
/// 提交用**会话快照**（<see cref="Capture"/>）：提交动作只读快照，杜绝异步途中被切换目标串味。
/// **挂起提交**（右键菜单收尾：菜单关闭后才落库，否则提交刷新会销毁承载菜单的行，见 WARNINGS 35）
/// 也由本控制器记存。引擎调用 / 状态文案 / 投影由宿主（VM）负责。
/// </summary>
public sealed class BrowserRenameController
{
    /// <summary>改名会话快照（提交动作的**唯一凭据**）。</summary>
    public sealed record Session(string Id, bool IsFolder, string OriginalName, string Name);

    private string? _id;
    private bool _isFolder;
    private BrowserPane _surface;
    private string _originalName = string.Empty;
    private Session? _deferredCommit;

    /// <summary>改名编辑中的文本（编辑框 TwoWay 绑定；输入即回写）。</summary>
    public string EditingName { get; set; } = string.Empty;

    /// <summary>是否正在就地改名（页面级动作一律让位：编辑语义优先）。</summary>
    public bool IsActive => _id != null;

    /// <summary>某实体此刻是否显示改名编辑框（投影判据：目标一致 + 编辑面一致）。</summary>
    public bool IsRenamingId(string? id, BrowserPane surface)
        => id != null && _id != null && _surface == surface
           && string.Equals(_id, id, System.StringComparison.Ordinal);

    /// <summary>开始会话（调用方需先处理旧会话：Capture 旧快照 → 结束旧会话 → 再 Begin 新会话）。</summary>
    public void Begin(string id, bool isFolder, string name, BrowserPane surface)
    {
        _id = id;
        _isFolder = isFolder;
        _surface = surface;
        _originalName = name;
        EditingName = name;
    }

    /// <summary>收会话（提交与取消的**唯一收口**）：返回被收掉的快照（未在会话 → null）。</summary>
    public Session? End()
    {
        if (_id == null) return null;
        var session = new Session(_id, _isFolder, _originalName, EditingName ?? string.Empty);
        _id = null;
        _isFolder = false;
        _originalName = string.Empty;
        EditingName = string.Empty;
        return session;
    }

    /// <summary>捕获当前会话快照；未在改名 → null。</summary>
    public Session? Capture()
        => _id == null
            ? null
            : new Session(_id, _isFolder, _originalName, EditingName ?? string.Empty);

    /// <summary>挂起一个待提交快照（右键菜单收尾：编辑态已收起，落库推迟到菜单关闭）。</summary>
    public void Defer(Session session) => _deferredCommit = session;

    /// <summary>取走挂起的提交（菜单关闭时落地；无挂起 → null）。</summary>
    public Session? TakeDeferred()
    {
        var session = _deferredCommit;
        _deferredCommit = null;
        return session;
    }

    /// <summary>是否有挂起提交。</summary>
    public bool HasDeferred => _deferredCommit != null;
}
