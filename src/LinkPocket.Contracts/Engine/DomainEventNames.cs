namespace LinkPocket.Contracts;

/// <summary>
/// 领域事件名常量（方案 4.4）：模块声明变更集、查询声明缓存依赖、宿主订阅筛选都用这一份常量，
/// 杜绝各处手写字符串导致的「拼错即静默失效」。
/// </summary>
public static class DomainEventNames
{
    /// <summary>链接表发生变更（增/改/删/移动/访问记录）。</summary>
    public const string LinksChanged = "links.changed";

    /// <summary>文件夹表发生变更（增/改/删/移动/排序，含父链 touch）。</summary>
    public const string FoldersChanged = "folders.changed";

    /// <summary>回收站发生变更（移入/还原/彻底删除）。</summary>
    public const string TrashChanged = "trash.changed";
}
