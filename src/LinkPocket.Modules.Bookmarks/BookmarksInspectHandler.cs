using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Bookmarks;

/// <summary>bookmarks.inspect（Query · FileIo）：只读预检——格式识别 + 条目统计，不写任何数据。</summary>
internal sealed class BookmarksInspectHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "bookmarks.inspect",
        Category: "bookmarks",
        Description: "Read-only preflight of a Netscape bookmark file: format detection, entry statistics, warnings (shared by pre-import display and post-export validation)",
        Parameters: [ParamSpec.Req<string>("file_path", "Bookmark HTML file path")],
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
            inspection.Error = "no file specified";
            return inspection;
        }

        try
        {
            if (!File.Exists(filePath))
            {
                inspection.Error = "file does not exist";
                return inspection;
            }

            inspection.FileBytes = new FileInfo(filePath).Length;

            var doc = await NetscapeReader.ParseFileAsync(filePath, ct);
            inspection.IsValid = doc.IsValid;
            inspection.Error = doc.Error;
            inspection.Warnings = doc.Warnings;
            inspection.Format = doc.Format;
            inspection.FolderCount = doc.FolderCount;
            inspection.LinkCount = doc.LinkCount;
            inspection.SkippedCount = doc.SkippedCount;
            inspection.MaxDepth = doc.MaxDepth;
        }
        catch (OperationCanceledException)
        {
            // 用户取消不得被降级成"读取失败"：取消沿调用链上抛，由引擎管道按 Cancelled 落审计
            throw;
        }
        catch (Exception ex)
        {
            // 失败降级为读取失败（存量容错语义；失败留痕由引擎管道审计承担——模块层无日志器引用）
            inspection.IsValid = false;
            inspection.Error = "file read failed:" + ex.Message;
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
