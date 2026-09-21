namespace LinkPocket.Contracts;

/// <summary>
/// 文件夹 ID 的语义定义。
///
/// <para><b>「全部书签」（根目录）不是实体、没有 ID</b>：它不在 <c>folders</c> 表里，也不该被当成文件夹。
/// 契约层一律用 <c>null</c> 表示根——「根级文件夹」= <c>ParentId == null</c>，
/// 「根级链接」= <c>ListId == null</c>，API 入参 <c>folder_id == null</c>（或参数缺省）表示根目录页。</para>
///
/// <para><b>本版本不做任何兼容</b>：根只有 <c>null</c> 一种形状，不存在字符串哨兵、不存在空串归一、
/// 不存在"历史入参形状"。任何非 null 值都被当成真实 ID 去查库，查不到就是
/// <c>LP.STATE.001 ENTITY_NOT_FOUND</c>——错就报错，不猜意图。</para>
/// </summary>
public static class FolderIds
{
    /// <summary>
    /// 「全部书签」虚根在**路径里**的身份（<see cref="BookmarkPath.RootToken"/>）。
    /// 显示名不在这里——它属界面语言，见 <c>I18n.BookmarkDisplay</c>。
    /// </summary>
    public const string RootToken = BookmarkPath.RootToken;

    /// <summary>
    /// 是否为根目录。<b>只认 <c>null</c></b>：空串、<c>"0"</c> 之类的形状都是非法 ID，不是根。
    /// </summary>
    public static bool IsRoot(string? folderId) => folderId is null;
}