namespace LinkPocket.Api;

/// <summary>
/// 文件夹 ID 的语义定义。
///
/// <para><b>「全部书签」（根目录）不是实体、没有 ID</b>：它不在 <c>lists</c> 表里，也不该被当成文件夹。
/// 契约层一律用 <c>null</c> 表示根——「根级文件夹」= <c>ParentId == null</c>，
/// 「根级链接」= <c>ListId == null</c>，API 入参 <c>folderId == null</c> 表示根目录页。</para>
///
/// <para>历史版本用字符串哨兵 <c>"0"</c> 表示根，并与 <c>null</c> 并存，导致全库几十处
/// <c>string.IsNullOrEmpty(x) || x == "0"</c> 的双重判定。<see cref="Normalize"/> 是**唯一**
/// 还认识这个历史值的入口，只用于边界归一化（外部入参 / 未迁移的历史库）；
/// 归一化后的数据一律不再出现 <c>"0"</c>。</para>
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
