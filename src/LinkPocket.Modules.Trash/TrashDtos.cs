using LinkPocket.Data;

namespace LinkPocket.Modules.Trash;

/// <summary>回收站模块结果 DTO 组。</summary>
/// <summary>还原结果（还原固定落根，ListId = null——行为等价项）。</summary>
public sealed record TrashRestoreResult(string LinkId);

/// <summary>批量还原结果。</summary>
public sealed record TrashRestoreBatchResult(int Restored);

/// <summary>批量永久删除结果。</summary>
public sealed record TrashPurgeBatchResult(int PurgedLinks, int PurgedFolders);
