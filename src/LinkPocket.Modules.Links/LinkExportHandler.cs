using System.Text.Json;
using System.Text.Json.Serialization;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Links;

/// <summary>links.export（★ 引擎能力，不接 UI）：全量链接导出为 JSON / CSV 文件（FileIO）。</summary>
internal sealed class LinkExportHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "links.export",
        Category: "links",
        Description: "导出全部链接为 JSON 或 CSV 文件（file_path 为目标文件完整路径）",
        Parameters:
        [
            ParamSpec.Req<string>("file_path", "导出文件完整路径"),
            ParamSpec.Opt<string>("format", "json（默认）| csv"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var filePath = CommandArgs.RequireString(args, "file_path");
        var format = CommandArgs.OptionalString(args, "format") ?? "json";
        if (format is not ("json" or "csv"))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EnumOutOfRange, $"format 只支持 json / csv，得到「{format}」", correlationId: ctx.CorrelationId));

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath, $"导出目录不存在：{directory}", correlationId: ctx.CorrelationId));

        var links = await ctx.Uow.Links.ListAsync(new LinkQuerySpec(), ctx.Ct);
        var dtos = links.Select(l => l.ToDto()).ToList();

        try
        {
            if (format == "json")
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                };
                await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(dtos, options), ctx.Ct);
            }
            else
            {
                await File.WriteAllTextAsync(fullPath, BuildCsv(dtos), ctx.Ct);
            }
        }
        catch (Exception ex)
        {
            throw new EngineException(EngineErrors.Of(
                EngineErrors.FileIoError, $"导出文件写入失败：{ex.Message}", retryable: true));
        }

        var bytes = new FileInfo(fullPath).Length;
        return CommandResult.Ok(
            new LinkExportResult(fullPath, format, dtos.Count, bytes),
            ChangeSet.Of(
                new EntityRef("file", fullPath),
                "links.changed",
                $"已导出 {dtos.Count} 个链接到 {fullPath}"));
    }

    internal static string BuildCsv(IReadOnlyList<LinkDto> dtos)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("id,url,title,description,favicon_url,list_id,visit_count,is_important,created_at,updated_at");
        foreach (var dto in dtos)
        {
            sb.AppendLine(string.Join(",",
                Csv(dto.LinkId), Csv(dto.Url), Csv(dto.Title), Csv(dto.Description), Csv(dto.FaviconUrl),
                Csv(dto.ListId ?? string.Empty), dto.VisitCount.ToString(), dto.IsImportant ? "1" : "0",
                Csv(dto.CreatedAt.ToString("O")), Csv(dto.UpdatedAt.ToString("O"))));
        }

        return sb.ToString();

        static string Csv(string value)
        {
            if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }
}
