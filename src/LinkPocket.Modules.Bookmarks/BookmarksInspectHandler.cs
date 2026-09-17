using System.Text.Json;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Bookmarks;

/// <summary>bookmarks.inspect（Query · FileIo）：只读预检——格式识别 + 条目统计，不写任何数据。</summary>
internal sealed class BookmarksInspectHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "bookmarks.inspect",
        Category: "bookmarks",
        Description: "只读预检 Netscape 书签文件：格式识别、条目统计、告警（导入前展示 / 导出后校验共用）",
        Parameters: [ParamSpec.Req<string>("file_path", "书签 HTML 文件路径")],
        Caps: CommandCaps.Query | CommandCaps.FileIo);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var filePath = CommandArgs.RequireString(args, "file_path");
        var inspection = await InspectAsync(filePath, ctx.Ct);

        return CommandResult.Ok(new BookmarkFileInspectionDto
        {
            IsValid = inspection.IsValid,
            Error = inspection.Error,
            Format = inspection.Format,
            Warnings = inspection.Warnings,
            FolderCount = inspection.FolderCount,
            LinkCount = inspection.LinkCount,
            SkippedCount = inspection.SkippedCount,
            MaxDepth = inspection.MaxDepth,
            FileBytes = inspection.FileBytes,
            TotalItems = inspection.TotalItems,
        });
    }

    /// <summary>纯只读预检（导入 Handler 与导出自校验共用同一解析器——看到的与导入的必然一致）。</summary>
    internal static async Task<BookmarkInspection> InspectAsync(string filePath, CancellationToken ct)
    {
        var inspection = new BookmarkInspection();

        if (string.IsNullOrWhiteSpace(filePath))
        {
            inspection.Error = "未指定文件";
            return inspection;
        }

        try
        {
            if (!File.Exists(filePath))
            {
                inspection.Error = "文件不存在";
                return inspection;
            }

            inspection.FileBytes = new FileInfo(filePath).Length;

            var doc = await NetscapeReader.ParseFileAsync(filePath);
            inspection.IsValid = doc.IsValid;
            inspection.Error = doc.Error;
            inspection.Warnings = doc.Warnings;
            inspection.Format = doc.Format;
            inspection.FolderCount = doc.FolderCount;
            inspection.LinkCount = doc.LinkCount;
            inspection.SkippedCount = doc.SkippedCount;
            inspection.MaxDepth = doc.MaxDepth;
        }
        catch (Exception ex)
        {
            inspection.IsValid = false;
            inspection.Error = "读取文件失败：" + ex.Message;
        }

        return inspection;
    }
}

/// <summary>预检中间结果（模块内；对外经 BookmarkFileInspectionDto 序列化）。</summary>
internal sealed class BookmarkInspection
{
    public bool IsValid { get; set; }
    public string Error { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = [];
    public int FolderCount { get; set; }
    public int LinkCount { get; set; }
    public int SkippedCount { get; set; }
    public int MaxDepth { get; set; }
    public long FileBytes { get; set; }
    public int TotalItems => FolderCount + LinkCount;
}
