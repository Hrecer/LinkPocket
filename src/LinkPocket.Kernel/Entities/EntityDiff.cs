using LinkPocket.Contracts;

namespace LinkPocket.Data;

/// <summary>链接的字段级 diff 快照（用户可见字段集；时间戳与计数列不入 diff，见 ENGINE-API §1）。</summary>
public readonly record struct LinkSnapshot(
    string Url, string? Title, string? Description, string? FolderId, bool IsImportant, string? FaviconUrl)
{
    public static LinkSnapshot Of(Link link)
        => new(link.Url, link.Title, link.Description, link.ListId, link.IsImportant, link.FaviconUrl);

    /// <summary>回收站快照 → 原值快照（删除类 diff 的 Before 来源；归属取 origin——回收站行的权威位置字段）。</summary>
    public static LinkSnapshot Of(TrashedLink snapshot)
        => new(snapshot.Url, snapshot.Title, snapshot.Description, snapshot.OriginListId,
            snapshot.IsImportant, snapshot.FaviconUrl);
}

/// <summary>文件夹的字段级 diff 快照（用户可见字段集）。</summary>
public readonly record struct FolderSnapshot(string Name, string? Description, string? ParentId)
{
    public static FolderSnapshot Of(Folder folder)
        => new(folder.Name, folder.Description, folder.ParentId);

    /// <summary>回收站单元快照 → 原值快照（删除类 diff 的 Before 来源；父取 origin——原位还原的数据依据）。</summary>
    public static FolderSnapshot Of(TrashedFolder snapshot)
        => new(snapshot.Name, snapshot.Description, snapshot.OriginParentFolderId);
}

/// <summary>
/// 实体字段级 diff 的**唯一产出点**（字段集与 null 语义见 ENGINE-API §1「字段级 diff 语义」）：
/// Links / Folders / Trash / Bookmarks 四域共用同一份字段表，绝不各写一份。
///
/// <para>快照为 <c>null</c> = 该时刻实体不存在：创建前 = Before 全"不适用"、删除后 = After 全"不适用"；
/// 两侧都在时只报**真的变了**的字段（大小写敏感逐字比较）。</para>
/// </summary>
public static class EntityDiff
{
    public const string LinkType = "link";
    public const string FolderType = "folder";

    /// <summary>链接 diff（新建 / 修改 / 删除共用一条口径）。</summary>
    public static IReadOnlyList<FieldChange> Diff(string id, LinkSnapshot? before, LinkSnapshot? after)
    {
        var diff = new List<FieldChange>(6);
        AddString(diff, LinkType, id, "url", before, after, s => s.Url);
        AddString(diff, LinkType, id, "title", before, after, s => s.Title);
        AddString(diff, LinkType, id, "description", before, after, s => s.Description);
        AddString(diff, LinkType, id, "folder_id", before, after, s => s.FolderId);
        AddBool(diff, LinkType, id, "is_important", before, after, s => s.IsImportant);
        // favicon_url 是辅助字段：两侧都没有值时不入 diff（否则每行都挂一条空字段）
        if (before?.FaviconUrl != null || after?.FaviconUrl != null)
            AddString(diff, LinkType, id, "favicon_url", before, after, s => s.FaviconUrl);
        return diff;
    }

    /// <summary>文件夹 diff（新建 / 改名 / 改描述 / 移动 / 删除共用一条口径）。</summary>
    public static IReadOnlyList<FieldChange> Diff(string id, FolderSnapshot? before, FolderSnapshot? after)
    {
        var diff = new List<FieldChange>(3);
        AddString(diff, FolderType, id, "name", before, after, s => s.Name);
        AddString(diff, FolderType, id, "description", before, after, s => s.Description);
        AddString(diff, FolderType, id, "parent_id", before, after, s => s.ParentId);
        return diff;
    }

    private static void AddString<T>(List<FieldChange> diff, string type, string id, string field,
        T? before, T? after, Func<T, string?> get) where T : struct
    {
        var b = before is { } bs ? get(bs) : null;
        var a = after is { } asx ? get(asx) : null;
        if (before is not null && after is not null && string.Equals(b, a, StringComparison.Ordinal)) return;
        diff.Add(new FieldChange(type, id, field,
            before is null ? null : FieldValue.Str(b),
            after is null ? null : FieldValue.Str(a)));
    }

    private static void AddBool<T>(List<FieldChange> diff, string type, string id, string field,
        T? before, T? after, Func<T, bool> get) where T : struct
    {
        if (before is not null && after is not null && before is { } bs && after is { } asx && get(bs) == get(asx)) return;
        var b = before is { } b2 ? get(b2) : false;
        var a = after is { } a2 ? get(a2) : false;
        diff.Add(new FieldChange(type, id, field,
            before is null ? null : FieldValue.Bool(b),
            after is null ? null : FieldValue.Bool(a)));
    }
}
