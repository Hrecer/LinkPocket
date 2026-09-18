using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Bookmarks;

/// <summary>书签互导模块入口（方案 4.2）：Netscape 格式导入/导出/只读预检。</summary>
/// <remarks>提供 handler：bookmarks.inspect（只读预检）、bookmarks.import（导入）、bookmarks.export（导出）。</remarks>
public static class BookmarksModule
{
    public static IReadOnlyList<ICommandHandler> CreateHandlers() =>
    [
        new BookmarksInspectHandler(),
        new BookmarksImportHandler(),
        new BookmarksExportHandler(),
    ];
}
