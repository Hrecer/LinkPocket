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
        Description: "Export all links as a JSON or CSV file (file_path is the target absolute path)",
        Parameters:
        [
            ParamSpec.Req<string>("file_path", "Export target absolute path"),
            ParamSpec.Opt<string>("format", "json (default) | csv"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var filePath = CommandArgs.RequireString(args, "file_path");
        var format = CommandArgs.OptionalString(args, "format") ?? "json";
        if (format is not ("json" or "csv"))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.EnumOutOfRange, $"format supports only json / csv, got '{format}'", correlationId: ctx.CorrelationId));

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath, $"export directory does not exist: {directory}", correlationId: ctx.CorrelationId));

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
                EngineErrors.FileIoError, $"export file write failed: {ex.Message}", retryable: true));
        }

        var bytes = new FileInfo(fullPath).Length;
        return CommandResult.Ok(
            new LinkExportResult(fullPath, format, dtos.Count, bytes),
            ChangeSet.Of(
                new EntityRef("file", fullPath),
                LinkPocket.Contracts.DomainEventNames.LinksChanged,
                $"Exported {dtos.Count} link(s) to {fullPath}"));
    }

    internal static string BuildCsv(IReadOnlyList<LinkDto> dtos)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("id,url,title,description,favicon_url,list_id,visit_count,is_important,created_at,updated_at");
        foreach (var dto in dtos)
        {
            sb.AppendLine(string.Join(",",
                Csv(dto.LinkId), Csv(dto.Url), Csv(dto.Title), Csv(dto.Description), Csv(dto.FaviconUrl),
                Csv(dto.ListId ?? string.Empty),
                dto.VisitCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                dto.IsImportant ? "1" : "0",
                Csv(dto.CreatedAt.ToString("O")), Csv(dto.UpdatedAt.ToString("O"))));
        }

        return sb.ToString();

        static string Csv(string value)
        {
            // 公式注入防护：以 = + - @ 开头的单元格会被表格软件当公式执行（标题/描述/地址都可能来自
            // 导入的书签文件，不是用户敲的）——前置一个单引号让它保持文本。
            if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
                value = "'" + value;

            if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }
}
