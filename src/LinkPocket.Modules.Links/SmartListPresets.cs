using LinkPocket.Kernel;

namespace LinkPocket.Modules.Links;

/// <summary>智能列表四预设的**唯一语义出处**（筛选 + 自然排序）：全量命令与分页命令共用，避免两套口径。</summary>
internal static class SmartListPresets
{
    public const int DefaultDays = 7;

    public static bool TryResolve(
        string kind, DateTime since, out LinkFilter filter, out IReadOnlyList<SortSpec> sort)
    {
        switch (kind)
        {
            case "recently_added":
                filter = new LinkFilter { CreatedFrom = since };
                sort = [new SortSpec("created_at", SortDir.Desc)];
                return true;
            case "recently_visited":
                filter = new LinkFilter { LastVisitedFrom = since };
                sort = [new SortSpec("last_visited_at", SortDir.Desc)];
                return true;
            case "recently_edited":
                filter = new LinkFilter { UpdatedFrom = since };
                sort = [new SortSpec("updated_at", SortDir.Desc)];
                return true;
            case "most_visited":
                filter = new LinkFilter { VisitCountMin = 1 };
                sort = [new SortSpec("visit_count", SortDir.Desc), new SortSpec("last_visited_at", SortDir.Desc)];
                return true;
            default:
                filter = new LinkFilter();
                sort = [];
                return false;
        }
    }
}
