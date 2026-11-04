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

    /// <summary>
    /// 行类型：<c>link</c>（默认）或 <c>folder</c>。
    /// 搜索结果把两类实体放在同一条有序结果集里，界面按本字段决定图标/列/可用动作；
    /// 老命令不读它时保持 <c>link</c>，序列化面完全兼容。
    /// </summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "link";
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
    [JsonPropertyName("folder_name")] public string FolderName { get; set; } = FolderIds.RootToken;
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
    /// 浏览页主视图一次的「目录页 + 树 + 统计」一致快照。<c>folders.contents</c> 恒为 null。
    /// </summary>
    [JsonPropertyName("tree"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FolderDto>? Tree { get; set; }

    /// <summary>
    /// 根级直接书签数（与 links.stats.RootLevel 同口径）。仅 <c>folders.overview</c> 填充；<c>folders.contents</c> 恒为 null。
    /// </summary>
    [JsonPropertyName("root_link_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RootLinkCount { get; set; }

    /// <summary>
    /// 全库活动链接平铺（树叶子注入数据源）：每链接携带归属目录 <c>list_id</c>（null = 根级），
    /// 调用方按父目录分组后把直接链接叶子挂到对应文件夹节点下。仅 <c>folders.overview</c> 填充；
    /// <c>folders.contents</c> 恒为 null。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="TreeLinkDto"/> 而非完整 <see cref="LinkDto"/>：这里就是**全库**链接（10k 库 = 1 万条），
    /// 而树叶子只读四个字段（id / 标题 / 地址 / 归属）——把描述、时间戳、访问计数、重要标记一起搬，
    /// 是每次导航都要多付一份序列化与反序列化的纯浪费。
    /// </remarks>
    [JsonPropertyName("tree_links"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TreeLinkDto>? TreeLinks { get; set; }
}

/// <summary>
/// 树叶子用的链接精简投影（<c>folders.overview</c> 的 <c>tree_links</c> 与 <c>folders.tree_links</c>）：
/// 目录树把链接挂成叶子只需要这几个字段（含站点图标地址——叶子要画站点头像）；完整字段请用
/// <see cref="LinkDto"/>（主栏行数据源）。
/// </summary>
public class TreeLinkDto
{
    [JsonPropertyName("id")] public string LinkId { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("list_id")] public string? ListId { get; set; }
    /// <summary>站点图标地址（内嵌 data URI 或外部地址；界面层解码/回落，树叶子与主栏行同一套链路）。</summary>
    [JsonPropertyName("favicon_url")] public string? FaviconUrl { get; set; }
}

public class PagedLinksDto
{
    [JsonPropertyName("links")] public List<LinkDto> Links { get; set; } = new();
    [JsonPropertyName("total_count")] public int TotalCount { get; set; }
    [JsonPropertyName("current_page")] public int CurrentPage { get; set; }
    [JsonPropertyName("last_page")] public int LastPage { get; set; }
}

/// <summary>回收站平铺条目类型常量（杜绝 "link"/"folder" 魔法值拼写错即静默失效）。</summary>
public static class TrashEntryType
{
    /// <summary>单独删除的书签条目。</summary>
    public const string Link = "link";

    /// <summary>被删文件夹单元根条目。</summary>
    public const string Folder = "folder";
}

/// <summary>
/// 回收站平铺条目（Windows 式）：entry_type = "link" | "folder"（见 <see cref="TrashEntryType"/>）。
/// folder 条目 = 「删除操作」的直接对象（被删文件夹单元的根），单元内部内容在回收站树里展示；
/// link 条目 = 单独删除的书签。id 对 link = 原 link_id，对 folder = trash_folder_id。
/// </summary>
public class TrashEntryDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("entry_type")] public string EntryType { get; set; } = TrashEntryType.Link;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string? Url { get; set; }
    /// <summary>描述快照（link 条目 = 原链接描述；folder 条目 = 被删文件夹的描述）。</summary>
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("favicon_url")] public string? FaviconUrl { get; set; }
    [JsonPropertyName("origin_path")] public string? OriginPath { get; set; }
    [JsonPropertyName("deleted_at")] public DateTime DeletedAt { get; set; }

    /// <summary>归属的回收站单元（**仅 link 条目有意义**）：null = 根级（单独删除）；非 null = 属于该单元的直接内容。
    /// 仅 <c>trash.overview</c> 填充（树叶子注入数据源）；<c>trash.list</c> / <c>trash.unit_contents</c> 恒为 null。</summary>
    [JsonPropertyName("trash_folder_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TrashFolderId { get; set; }
}

/// <summary>回收站文件夹树节点（层级展示用；回收站内文件夹不可打开/导航）。</summary>
public class TrashFolderDto
{
    [JsonPropertyName("trash_folder_id")] public string TrashFolderId { get; set; } = string.Empty;
    [JsonPropertyName("parent_trash_folder_id")] public string? ParentTrashFolderId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    /// <summary>删除时的描述快照（v5 保真列；仅 <see cref="TrashOverviewDto"/> 填充）。</summary>
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    /// <summary>单元内书签总数（含子孙单元）。</summary>
    [JsonPropertyName("link_count")] public int LinkCount { get; set; }
    [JsonPropertyName("deleted_at")] public DateTime DeletedAt { get; set; }

    /// <summary>删除时的原位置路径快照（仅 <c>trash.overview</c> 填充，供「原位置」列展示；其它查询恒 null）。</summary>
    [JsonPropertyName("origin_path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginPath { get; set; }
}

/// <summary>
/// trash.overview（回收站页主视图快照）：**单元树 + 全量链接快照（每项携归属单元）一次取齐**——
/// 与 <c>folders.overview</c> 同哲学（树/列表/计数同一快照，页面导航与刷新不再逐单元取数）。
/// 消费者：回收站页（树叶子注入 + 主栏内容按 <c>trash_folder_id</c> 过滤 + 面包屑路径）。
/// </summary>
public class TrashOverviewDto
{
    /// <summary>全量单元平铺（层级由调用方按 parent_trash_folder_id 组装；link_count = 子树书签总数）。</summary>
    [JsonPropertyName("folders")] public List<TrashFolderDto> Folders { get; set; } = new();

    /// <summary>全量书签快照（含根级单独删除的；每项 trash_folder_id = 归属单元，null = 根级）。
    /// 调用方按父单元分组后把直接链接叶子挂到对应单元节点下（与 folders.overview.tree_links 同口径）。</summary>
    [JsonPropertyName("links")] public List<TrashEntryDto> Links { get; set; } = new();
}

public class MetadataDto
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("favicon_url")] public string? FaviconUrl { get; set; }

    /// <summary>标题与描述都拿不到，且原因 = 站点返回的是反爬/人机校验中间页
    /// （所有可用路径——含浏览器指纹与浏览器内核渲染——都没越过它）。
    /// 调用方据此把"没解析到"说成真实原因，而不是泛泛的"页面没写"。</summary>
    [JsonPropertyName("blocked_by_verification")] public bool BlockedByVerification { get; set; }

    /// <summary>结果由哪一档取得：http / fingerprint / browser_render / human_verification / blocked。
    /// 让调用方说得清"为什么慢"（浏览器渲染档会慢几秒），而不是把慢一律当成卡死。</summary>
    [JsonPropertyName("resolution")] public string? Resolution { get; set; }

    /// <summary>站点的人机挑战仍在等人点：标题/描述为空，但完成一次人工验证后可以重试成功。
    /// 与 <see cref="BlockedByVerification"/> 的区别 = "有人点一下就能过" vs "这条路径就是被拒绝"。</summary>
    [JsonPropertyName("needs_human_verification")] public bool NeedsHumanVerification { get; set; }

    /// <summary>当前宿主是否具备打开人工验证窗口的能力（桌面宿主 / 已连接桌面能力桥 = true；
    /// 纯无头宿主 = false）。调用方据此决定"请用户点一下"还是"换有界面的入口"。</summary>
    [JsonPropertyName("human_verification_available")] public bool HumanVerificationAvailable { get; set; }

    /// <summary>当前宿主是否有浏览器渲染兜底（桌面宿主 / 已连接桌面能力桥 = true）。</summary>
    [JsonPropertyName("browser_render_available")] public bool BrowserRenderAvailable { get; set; }

    /// <summary>本次抓取耗时（毫秒，含所有兜底档）。</summary>
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; set; }
}

/// <summary>metadata 结果的取得档（机器面字符串；新增档只能追加，不能改名）。</summary>
public static class MetadataResolutions
{
    public const string Http = "http";
    public const string Fingerprint = "fingerprint";
    public const string BrowserRender = "browser_render";
    public const string HumanVerification = "human_verification";
    public const string Blocked = "blocked";
}

/// <summary>批量元数据抓取的逐项结果：批处理**永不弹人机验证窗口**，需要人工的项按
/// <see cref="NeedsHumanVerification"/> 标出，由调用方对这些项单独调用
/// <c>links.metadata_fetch</c>（interactive=true）。大批量里只有一两项需要弹窗时，不会打断整批。</summary>
public class MetadataBatchItemDto
{
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("favicon_url")] public string? FaviconUrl { get; set; }
    [JsonPropertyName("resolution")] public string? Resolution { get; set; }
    [JsonPropertyName("needs_human_verification")] public bool NeedsHumanVerification { get; set; }
    [JsonPropertyName("human_verification_available")] public bool HumanVerificationAvailable { get; set; }
    [JsonPropertyName("blocked_by_verification")] public bool BlockedByVerification { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("error_message")] public string? ErrorMessage { get; set; }
    [JsonPropertyName("retryable")] public bool Retryable { get; set; }
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; set; }
}

public class MetadataBatchSummaryDto
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("ok")] public int Ok { get; set; }
    [JsonPropertyName("empty")] public int Empty { get; set; }
    [JsonPropertyName("needs_human_verification")] public int NeedsHumanVerification { get; set; }
    [JsonPropertyName("blocked")] public int Blocked { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; set; }
    [JsonPropertyName("browser_render_available")] public bool BrowserRenderAvailable { get; set; }
    [JsonPropertyName("human_verification_available")] public bool HumanVerificationAvailable { get; set; }
}

public class MetadataBatchDto
{
    [JsonPropertyName("items")] public List<MetadataBatchItemDto> Items { get; set; } = new();
    [JsonPropertyName("summary")] public MetadataBatchSummaryDto Summary { get; set; } = new();
}

/// <summary>favicon.fetch 的逐项结果：**等待缓存就绪**（同一 <c>FaviconCache</c> 管线，不另算一遍），
/// 用 <see cref="Reason"/> 说明为什么没有图（内嵌 / 站点没给 / 被反爬 / 宿主不能画 SVG …）。</summary>
public class FaviconFetchItemDto
{
    [JsonPropertyName("favicon_url")] public string FaviconUrl { get; set; } = string.Empty;
    [JsonPropertyName("cached")] public bool Cached { get; set; }
    [JsonPropertyName("cache_path")] public string? CachePath { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; set; }
}

public class FaviconFetchSummaryDto
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("cached")] public int Cached { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; set; }
    /// <summary>当前宿主是否能光栅化 SVG（桌面宿主 / 已连接能力桥 = true）。</summary>
    [JsonPropertyName("svg_rasterizer_available")] public bool SvgRasterizerAvailable { get; set; }
}

public class FaviconFetchDto
{
    [JsonPropertyName("items")] public List<FaviconFetchItemDto> Items { get; set; } = new();
    [JsonPropertyName("summary")] public FaviconFetchSummaryDto Summary { get; set; } = new();
}

/// <summary>favicon.fetch 的失败原因（机器面字符串；追加式）。</summary>
public static class FaviconFetchReasons
{
    public const string Cached = "cached";
    public const string Empty = "empty";
    public const string InlineData = "inline_data";
    public const string InvalidUrl = "invalid_url";
    public const string InvalidTarget = "invalid_target";
    public const string NotFound = "not_found";
    public const string Blocked = "blocked";
    public const string TooLarge = "too_large";
    public const string SvgRasterizerUnavailable = "svg_rasterizer_unavailable";
    public const string Backoff = "backoff";
    public const string Timeout = "timeout";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

public class LinkCountsDto
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("trash")] public int Trash { get; set; }
    [JsonPropertyName("root_level")] public int RootLevel { get; set; }
    [JsonPropertyName("by_folder")] public Dictionary<string, int> ByFolder { get; set; } = new();
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

/// <summary>
/// 定位解析结果（<c>locate.resolve</c>）：一个 ID 在目录里的位置——与界面无关的纯坐标。
/// 界面的「跳转」= 切到浏览页 + 进入 <see cref="ContainerFolderId"/> + 选中 <see cref="Id"/>。
/// </summary>
public class LocateResolveDto
{
    /// <summary>目标类型：<c>link</c> / <c>folder</c>。</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    /// <summary>**容器目录**：进哪个目录才能看到目标那一行；null = 根「全部书签」（零哨兵）。</summary>
    [JsonPropertyName("container_folder_id")] public string? ContainerFolderId { get; set; }
    /// <summary>容器的显示路径（「全部书签 / A / B」）。</summary>
    [JsonPropertyName("container_path")] public string ContainerPath { get; set; } = string.Empty;
    /// <summary>目标自身的显示路径（容器路径 + 「 / 」 + 名称）。</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
}
