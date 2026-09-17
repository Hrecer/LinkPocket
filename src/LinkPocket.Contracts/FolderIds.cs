namespace LinkPocket.Api;

/// <summary>
/// 文件夹 ID 的语义定义。
///
/// <para><b>「全部书签」（根目录）不是实体、没有 ID</b>：它不在 <c>folders</c> 表里，也不该被当成文件夹。
/// 契约层一律用 <c>null</c> 表示根——「根级文件夹」= <c>ParentId == null</c>，
/// 「根级链接」= <c>ListId == null</c>，API 入参 <c>folderId == null</c> 表示根目录页。</para>
///
/// <para>历史 wire 协议曾用字符串哨兵 <c>"0"</c> 表示根。schema v2 全新建库后哨兵值在库里
/// 彻底不存在（根 = NULL，唯一表示）；<see cref="Normalize"/> 只兜外部入参的历史形状，
/// 待阶段 6 协议重写后一并退役。</para>
/// </summary>
public static class FolderIds
{
    /// <summary>根目录的显示名（仅用于界面文案与目录页标题，不是数据）。</summary>
    public const string RootDisplayName = "全部书签";

    /// <summary>兼容判定：是否为根目录。除边界归一化外，业务代码请直接判 <c>null</c>。</summary>
    public static bool IsRoot(string? folderId)
        => string.IsNullOrEmpty(folderId) || folderId == LegacyRootId;

    /// <summary>归一化：根（null / 空 / 历史哨兵）→ <c>null</c>，其余原样返回。</summary>
    public static string? Normalize(string? folderId)
        => IsRoot(folderId) ? null : folderId;

    /// <summary>
    /// 历史哨兵值：整个代码库里这个字面量**只此一处**（外加数据迁移 SQL 引用它）。
    /// 新代码不得再引入任何字符串哨兵来表示根目录。
    /// </summary>
    public const string LegacyRootId = "0";
}
