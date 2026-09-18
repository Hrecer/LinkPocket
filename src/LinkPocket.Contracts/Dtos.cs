using System.Text.Json.Serialization;

namespace LinkPocket.Contracts;

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
    /// 该文件夹下所有链接总数（**递归**）：含直接子链接以及全部子孙文件夹内的链接。
    /// 与之相对的直接口径见 <see cref="DirectLinkCount"/>——两个口径不同名同义，调用方不得混用。
    /// </summary>
    [JsonPropertyName("link_count")] public int LinkCount { get; set; }

    /// <summary>
    /// 该文件夹的**直接**子链接数（不递归；不含子孙文件夹内的链接）。
    /// 与 <see cref="LinkCount"/>（递归）成对存在，语义自明——旧实现的两种口径混用已收敛到这里。
    /// </summary>
    [JsonPropertyName("direct_link_count")] public int DirectLinkCount { get; set; }

    /// <summary>
    /// 手动排序序号（<c>folders.sort</c> 写入；同层内升序即用户自定义顺序）。
    /// 目录页/树在 <c>sort_by=sort_order</c> 时按它排序——此前该列只有写路径没有读路径。
    /// </summary>
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }

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
    /// 当前目录的**直接**子链接总数：只统计当前目录的直接子链接，不递归统计子文件夹内的链接；
    /// 根目录为根级链接数。字段名与语义一一对应（旧名 total_link_count 会把"直接"误读成"总计"）。
    /// </summary>
    [JsonPropertyName("direct_link_count")] public int DirectLinkCount { get; set; }
    /// <summary>
    /// 是否因**引擎上限**而少返（true = 你要的比这里给的多，必须翻页或调大上限）。
    /// 显式 per_page ≤ 上限时恒为 false —— 那种情况下的分页是调用方自己的选择，不算截断。
    /// </summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    /// <summary>当前页码（从 1 开始；未启用分页时为 1）。</summary>
    [JsonPropertyName("current_page")] public int CurrentPage { get; set; } = 1;
    /// <summary>每页链接数（0 表示未启用分页，一次取回全部）。</summary>
    [JsonPropertyName("per_page")] public int PerPage { get; set; }
    /// <summary>链接总页数（未启用分页时为 1）。按本目录实际链接查询结果计算。</summary>
    [JsonPropertyName("last_page")] public int LastPage { get; set; } = 1;

    /// <summary>
    /// 全量文件夹平铺（与 folders.tree 同构；层级由调用方组装）。仅 <c>folders.overview</c> 填充：
    /// 浏览页主视图一次的「目录页 + 树 + 统计」一致快照（2.10-45）。<c>folders.contents</c> 恒为 null。
    /// </summary>
    [JsonPropertyName("tree"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FolderDto>? Tree { get; set; }

    /// <summary>
    /// 根级直接书签数（与 links.stats.RootLevel 同口径）。仅 <c>folders.overview</c> 填充；<c>folders.contents</c> 恒为 null。
    /// </summary>
    [JsonPropertyName("root_link_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RootLinkCount { get; set; }
}

public class PagedLinksDto
{
    [JsonPropertyName("links")] public List<LinkDto> Links { get; set; } = new();
    [JsonPropertyName("total_count")] public int TotalCount { get; set; }
    [JsonPropertyName("current_page")] public int CurrentPage { get; set; }
    [JsonPropertyName("last_page")] public int LastPage { get; set; }
}

/// <summary>
/// 回收站平铺条目（Windows 式）：entry_type = "link" | "folder"。
/// folder 条目 = 「删除操作」的直接对象（被删文件夹单元的根），单元内部内容在回收站树里展示；
/// link 条目 = 单独删除的书签。id 对 link = 原 link_id，对 folder = trash_folder_id。
/// </summary>
public class TrashEntryDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("entry_type")] public string EntryType { get; set; } = "link";
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("favicon_url")] public string? FaviconUrl { get; set; }
    [JsonPropertyName("origin_path")] public string? OriginPath { get; set; }
    [JsonPropertyName("deleted_at")] public DateTime DeletedAt { get; set; }
}

/// <summary>回收站文件夹树节点（层级展示用；回收站内文件夹不可打开/导航）。</summary>
public class TrashFolderDto
{
    [JsonPropertyName("trash_folder_id")] public string TrashFolderId { get; set; } = string.Empty;
    [JsonPropertyName("parent_trash_folder_id")] public string? ParentTrashFolderId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    /// <summary>单元内书签总数（含子孙单元）。</summary>
    [JsonPropertyName("link_count")] public int LinkCount { get; set; }
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

/// <summary>
/// Netscape 书签文件只读预检结果（导入前展示 / 导出后校验共用）。
/// 纯数据对象，不含任何 UI 依赖；<see cref="Warnings"/> 为可容忍的问题（结构不完整等）。
/// </summary>
public class BookmarkFileInspectionDto
{
    [JsonPropertyName("is_valid")] public bool IsValid { get; set; }
    /// <summary>无效原因（有效时为空字符串）。</summary>
    [JsonPropertyName("error")] public string Error { get; set; } = string.Empty;
    /// <summary>识别到的格式（如「Netscape 书签文件（NETSCAPE-Bookmark-file-1）」）。</summary>
    [JsonPropertyName("format")] public string Format { get; set; } = string.Empty;
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
    [JsonPropertyName("folder_count")] public int FolderCount { get; set; }
    [JsonPropertyName("link_count")] public int LinkCount { get; set; }
    /// <summary>被跳过的条目数（无地址 / about:blank）。</summary>
    [JsonPropertyName("skipped_count")] public int SkippedCount { get; set; }
    /// <summary>最深文件夹嵌套层数（根级文件夹 = 1；无文件夹时为 0）。</summary>
    [JsonPropertyName("max_depth")] public int MaxDepth { get; set; }
    [JsonPropertyName("file_bytes")] public long FileBytes { get; set; }
    [JsonPropertyName("total_items")] public int TotalItems { get; set; }
}
