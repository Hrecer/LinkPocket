using System.Text.Json.Serialization;

namespace LinkPocket.Api;

/// <summary>
/// 前后端通信 DTO。全部为可 JSON 序列化的纯数据对象，
/// 不携带任何 UI 框架类型——未来前端换成 Web 技术时无需改动。
/// </summary>

public class LinkDto
{
    [JsonPropertyName("id")] public string LinkId { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("favicon_url")] public string FaviconUrl { get; set; } = string.Empty;
    [JsonPropertyName("list_id")] public string? ListId { get; set; }
    [JsonPropertyName("last_visited_at")] public DateTime? LastVisitedAt { get; set; }
    [JsonPropertyName("visit_count")] public int VisitCount { get; set; }
    [JsonPropertyName("is_important")] public bool IsImportant { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class FolderDto
{
    [JsonPropertyName("id")] public string FolderId { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("parent_id")] public string? ParentId { get; set; }

    /// <summary>
    /// 该文件夹下所有链接总数（递归）：含直接子链接以及全部子孙文件夹内的链接。
    /// </summary>
    [JsonPropertyName("link_count")] public int LinkCount { get; set; }

    /// <summary>
    /// 文件夹「最后更新」＝内容最后变动时间。事件：内容变动（新增/删除/改名/移入移出链接与子文件夹、
    /// 链接内容被编辑）。内核沿父链刷新（FolderService.TouchModifiedAsync），UI 只读；<b>查看不算变动</b>。
    /// </summary>
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }

    /// <summary>文件夹创建时间（内核维护，UI 只读展示）。</summary>
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 文件夹「最后查看」时间。事件：子孙链接被打开详情页/访问。内核沿父链刷新
    /// （FolderService.RecordFolderViewAsync），与「最后更新」同一条父链原语、同一套事件驱动增量口径。
    /// </summary>
    [JsonPropertyName("last_visited_at")] public DateTime? LastVisitedAt { get; set; }

    /// <summary>
    /// 文件夹「查看次数」：子树内任何链接被查看详情页/访问时，沿父链所有祖先 +1
    /// （与 LastVisitedAt 同一次链式写入、同一事件；与链接的 VisitCount 相互独立，
    /// 移动链接不转移历史计数）。
    /// </summary>
    [JsonPropertyName("visit_count")] public int VisitCount { get; set; }
}

/// <summary>
/// 资源管理器式浏览的一个"目录页"：当前文件夹的子文件夹 + 链接 + 面包屑路径。
/// folderId 为 null 表示根目录（全部书签）——它不是实体、没有 ID。
/// </summary>
public class FolderContentsDto
{
    [JsonPropertyName("folder_id")] public string? FolderId { get; set; }
    [JsonPropertyName("folder_name")] public string FolderName { get; set; } = FolderIds.RootDisplayName;
    [JsonPropertyName("sub_folders")] public List<FolderDto> SubFolders { get; set; } = new();
    [JsonPropertyName("links")] public List<LinkDto> Links { get; set; } = new();
    [JsonPropertyName("breadcrumb")] public List<string> Breadcrumb { get; set; } = new();
    /// <summary>
    /// 直接子链接总数（语义确认，P3）：只统计当前目录的直接子链接，
    /// 不递归统计子文件夹内的链接；根目录为根级链接数。UI 上"书签数"含义以此为准。
    /// </summary>
    [JsonPropertyName("total_link_count")] public int TotalLinkCount { get; set; }
    /// <summary>当前页码（从 1 开始；未启用分页时为 1）。</summary>
    [JsonPropertyName("current_page")] public int CurrentPage { get; set; } = 1;
    /// <summary>每页链接数（0 表示未启用分页，一次取回全部）。</summary>
    [JsonPropertyName("per_page")] public int PerPage { get; set; }
    /// <summary>链接总页数（未启用分页时为 1）。按本目录实际链接查询结果计算。</summary>
    [JsonPropertyName("last_page")] public int LastPage { get; set; } = 1;
}

public class PagedLinksDto
{
    [JsonPropertyName("links")] public List<LinkDto> Links { get; set; } = new();
    [JsonPropertyName("total_count")] public int TotalCount { get; set; }
    [JsonPropertyName("current_page")] public int CurrentPage { get; set; }
    [JsonPropertyName("last_page")] public int LastPage { get; set; }
}

public class TrashEntryDto
{
    [JsonPropertyName("link_id")] public string LinkId { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("favicon_url")] public string? FaviconUrl { get; set; }
    [JsonPropertyName("original_list_id")] public string? OriginalListId { get; set; }
    [JsonPropertyName("last_visited_at")] public DateTime? LastVisitedAt { get; set; }
    [JsonPropertyName("visit_count")] public int VisitCount { get; set; }
    [JsonPropertyName("is_important")] public bool IsImportant { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
    [JsonPropertyName("deleted_at")] public DateTime DeletedAt { get; set; }
}

public class MetadataDto
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("favicon_url")] public string? FaviconUrl { get; set; }
}

public class LinkCountsDto
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("trash")] public int Trash { get; set; }
    [JsonPropertyName("root_level")] public int RootLevel { get; set; }
    [JsonPropertyName("by_folder")] public Dictionary<string, int> ByFolder { get; set; } = new();
}

public class ApiResultDto
{
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public class BackupImportDto
{
    [JsonPropertyName("folders_created")] public int FoldersCreated { get; set; }
    [JsonPropertyName("links_created")] public int LinksCreated { get; set; }
    [JsonPropertyName("total_items")] public int TotalItems { get; set; }
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = new();
}
