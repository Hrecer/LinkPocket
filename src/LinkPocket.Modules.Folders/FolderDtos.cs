namespace LinkPocket.Modules.Folders;

/// <summary>文件夹域命令结果 DTO 组（一文件一主类规则的 DTO 组例外）。</summary>
/// <summary>深层复制的产出。</summary>
public sealed record FolderCopyResult(string NewFolderId);

/// <summary>删除结果（含级联模式与连带统计）。</summary>
public sealed record FolderDeleteResult(
    string Cascade,
    int DeletedFolders,
    int TrashedLinks);

/// <summary>批量移动结果（含同名自动编号说明）。</summary>
public sealed record FolderMoveBatchResult(
    int Moved,
    IReadOnlyList<string> RenamedNotes);

/// <summary>重排结果。</summary>
public sealed record FolderSortResult(int Sorted);
