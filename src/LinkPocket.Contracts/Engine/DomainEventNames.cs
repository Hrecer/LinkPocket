namespace LinkPocket.Contracts;

/// <summary>
/// 领域事件名常量：模块声明变更集、查询声明缓存依赖、宿主订阅筛选都用这一份常量，
/// 杜绝各处手写字符串导致的「拼错即静默失效」。**新增事件名必须在此登记常量**。
/// </summary>
public static class DomainEventNames
{
    /// <summary>链接表发生变更（增/改/删/移动/访问记录）。</summary>
    public const string LinksChanged = "links.changed";

    /// <summary>文件夹表发生变更（增/改/删/移动/排序，含父链 touch）。</summary>
    public const string FoldersChanged = "folders.changed";

    /// <summary>回收站发生变更（移入/还原/彻底删除）。</summary>
    public const string TrashChanged = "trash.changed";

    /// <summary>宏被保存/更新（macro.save）。</summary>
    public const string MacroSaved = "macro.saved";

    /// <summary>宏被删除（macro.delete）。</summary>
    public const string MacroDeleted = "macro.deleted";

    /// <summary>撤销/重做栈被清空（undo.clear）。</summary>
    public const string UndoCleared = "undo.cleared";

    /// <summary>文件被拷入暂存区（staging.stage）。</summary>
    public const string StagingStaged = "staging.staged";

    /// <summary>暂存文件被丢弃（staging.discard）。</summary>
    public const string StagingDiscarded = "staging.discarded";
}
