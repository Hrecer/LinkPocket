using System.Text;
using System.Text.Json;

namespace LinkPocket.Api;

/// <summary>
/// 通信层抽象：前端与后端之间唯一的连接通道。
/// 当前实现为进程内直连（InProcessTransport）；未来 Web 化时，
/// 只需新增 Http/WebSocket 传输实现（同一 JSON 协议），前后端代码均无需改动。
/// </summary>
public interface ILinkPocketTransport : IDisposable
{
    /// <summary>发送 JSON-RPC 2.0 格式请求，返回 JSON-RPC 响应字符串。</summary>
    Task<string> SendAsync(string jsonRequest, CancellationToken cancellationToken = default);

    /// <summary>后端事件推送（数据变更通知等），负载为 JSON 字符串。第一阶段预留。</summary>
    event EventHandler<string>? EventReceived;
}

/// <summary>API 调用失败（远程错误 / 方法不存在等）。</summary>
public class LinkPocketApiException : Exception
{
    public int ErrorCode { get; }
    public LinkPocketApiException(string message, int errorCode = -32000) : base(message) => ErrorCode = errorCode;
}

/// <summary>进程内直连传输：把请求直接交给分发器，无序列化开销之外的网络环节。</summary>
public class InProcessTransport : ILinkPocketTransport
{
    private readonly LinkPocketApiDispatcher _dispatcher;

    public InProcessTransport(LinkPocketApiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public Task<string> SendAsync(string jsonRequest, CancellationToken cancellationToken = default)
        => _dispatcher.HandleAsync(jsonRequest, cancellationToken);

    // 进程内直连没有独立后端进程，事件推送预留为空实现
    public event EventHandler<string>? EventReceived
    {
        add { }
        remove { }
    }

    public void Dispose() { }
}

/// <summary>
/// JSON-RPC 2.0 分发器：把协议方法名路由到 ILinkPocketApi。
/// 协议方法命名：域.动作，如 "folders.contents"、"links.create"。
/// </summary>
public class LinkPocketApiDispatcher
{
    private readonly ILinkPocketApi _api;

    public LinkPocketApiDispatcher(ILinkPocketApi api) => _api = api;

    public async Task<string> HandleAsync(string jsonRequest, CancellationToken cancellationToken = default)
    {
        JsonElement? id = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonRequest);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl))
                id = idEl.Clone();
            var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            var @params = root.TryGetProperty("params", out var p) ? p.Clone() : JsonDocument.Parse("{}").RootElement.Clone();

            var result = await DispatchAsync(method, @params, cancellationToken);
            return WrapResult(id, result);
        }
        catch (Exception ex)
        {
            return WrapError(id, ex is LinkPocketApiException apiEx ? apiEx.ErrorCode : -32000, ex.Message);
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement p, CancellationToken ct) => method switch
    {
        // 浏览
        "folders.contents" => await _api.GetFolderContentsAsync(
            PStrOrNull(p, "folder_id"),
            PStr(p, "sort_by", "title"),
            PStr(p, "sort_order", "asc")),
        "folders.tree" => await _api.GetFolderTreeAsync(),
        "folders.breadcrumb" => await _api.GetBreadcrumbAsync(PStrOrNull(p, "folder_id")),

        // 文件夹管理
        "folders.create" => await _api.CreateFolderAsync(PReqStr(p, "name"), PStrOrNull(p, "parent_id")),
        "folders.update" => await _api.UpdateFolderAsync(
            PReqStr(p, "id"), PStrOrNull(p, "name"), PStrOrNull(p, "description"), PStrOrNull(p, "parent_id")),
        "folders.delete" => await WrapVoid(() => _api.DeleteFolderAsync(
            PReqStr(p, "id"), PStr(p, "cascade", "move_to_parent"), PStrOrNull(p, "target_list_id"))),
        "folders.move" => await WrapVoid(() => _api.MoveFolderAsync(PReqStr(p, "folder_id"), PStrOrNull(p, "target_parent_id"))),
        "folders.copy" => await _api.CopyFolderAsync(PReqStr(p, "folder_id"), PStrOrNull(p, "target_parent_id")),
        "folders.would_create_cycle" => await _api.WouldMoveCreateCycleAsync(PReqStr(p, "folder_id"), PReqStr(p, "target_parent_id")),
        "folders.update_sort" => await WrapVoid(() => _api.UpdateSortAsync(PStrOrNull(p, "parent_id"), PStrList(p, "item_ids"))),

        // 链接
        "links.list" => await _api.GetLinksAsync(
            PStrOrNull(p, "list_id"), PStrOrNull(p, "search"), PBoolOrNull(p, "is_important"),
            PStr(p, "sort_by", "created_at"), PStr(p, "sort_order", "desc"),
            PInt(p, "page", 1), PInt(p, "per_page", 20)),
        "links.create" => await _api.CreateLinkAsync(
            PReqStr(p, "url"), PStrOrNull(p, "title"), PStrOrNull(p, "description"),
            PStrOrNull(p, "list_id"), PStrOrNull(p, "favicon_url")),
        "links.update" => await _api.UpdateLinkAsync(
            PReqStr(p, "id"), PStrOrNull(p, "url"), PStrOrNull(p, "title"), PStrOrNull(p, "description"), PStrOrNull(p, "favicon_url")),
        "links.trash" => await WrapVoid(() => _api.TrashLinkAsync(PReqStr(p, "id"))),
        "links.record_visit" => await WrapVoid(() => _api.RecordVisitAsync(PReqStr(p, "id"))),

        // 回收站
        "trash.list" => await _api.GetTrashAsync(),
        "trash.restore" => await _api.RestoreLinkAsync(PReqStr(p, "link_id")),
        "trash.purge" => await WrapVoid(() => _api.PurgeLinkAsync(PReqStr(p, "link_id"))),

        // 搜索与智能列表
        "search" => await _api.SearchAsync(
            PReqStr(p, "query"), PBool(p, "search_title", true), PBool(p, "search_url", false),
            PBool(p, "search_description", false), PBool(p, "search_path", false),
            PStr(p, "sort_by", "title"), PStr(p, "sort_order", "asc")),
        "smartlist" => await _api.GetSmartListAsync(PReqStr(p, "kind"), PInt(p, "limit", 50)),

        // 元数据与统计
        "meta.fetch" => await _api.FetchMetadataAsync(PReqStr(p, "url")),
        "stats.counts" => await _api.GetCountsAsync(),

        // 导入导出
        "export.bookmarks_html" => await _api.ExportBookmarksHtmlAsync(PReqStr(p, "output_path")),
        "import.bookmarks_html" => await _api.ImportBookmarksHtmlAsync(PReqStr(p, "file_path")),

        _ => throw new LinkPocketApiException($"未知方法: {method}", -32601)
    };

    private static async Task<object?> WrapVoid(Func<Task> action)
    {
        await action();
        return null;
    }

    private static string WrapResult(JsonElement? id, object? result)
    {
        var idToken = id.HasValue ? JsonSerializer.Serialize(id.Value) : "null";
        var resultToken = result == null ? "null" : JsonSerializer.Serialize(result);
        return $"{{\"id\":{idToken},\"result\":{resultToken}}}";
    }

    private static string WrapError(JsonElement? id, int code, string message)
    {
        var idToken = id.HasValue ? JsonSerializer.Serialize(id.Value) : "null";
        var msgToken = JsonSerializer.Serialize(message);
        return $"{{\"id\":{idToken},\"error\":{{\"code\":{code},\"message\":{msgToken}}}}}";
    }

    // —— 参数读取助手 ——

    private static string PStr(JsonElement p, string name, string fallback)
        => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static string? PStrOrNull(JsonElement p, string name)
        => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string PReqStr(JsonElement p, string name)
        => PStrOrNull(p, name) ?? throw new LinkPocketApiException($"缺少必填参数: {name}", -32602);

    private static int PInt(JsonElement p, string name, int fallback)
        => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    private static bool PBool(JsonElement p, string name, bool fallback)
    {
        if (p.TryGetProperty(name, out var v))
        {
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return v.GetBoolean();
        }
        return fallback;
    }

    private static bool? PBoolOrNull(JsonElement p, string name)
        => p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static List<string> PStrList(JsonElement p, string name)
    {
        var list = new List<string>();
        if (p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    list.Add(item.GetString() ?? string.Empty);
        }
        return list;
    }
}

/// <summary>前端便捷调用扩展：强类型封装 JSON-RPC 编解码。</summary>
public static class TransportExtensions
{
    public static async Task<JsonElement> InvokeAsync(this ILinkPocketTransport transport,
        string method, object? args = null, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("N"),
            method,
            @params = args
        });
        var raw = await transport.SendAsync(payload, cancellationToken);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "API 调用失败" : "API 调用失败";
            var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : -32000;
            throw new LinkPocketApiException(message, code);
        }
        return root.TryGetProperty("result", out var result) ? result.Clone() : JsonDocument.Parse("null").RootElement.Clone();
    }

    public static async Task<T> InvokeAsync<T>(this ILinkPocketTransport transport,
        string method, object? args = null, CancellationToken cancellationToken = default)
    {
        var result = await transport.InvokeAsync(method, args, cancellationToken);
        if (result.ValueKind == JsonValueKind.Null)
            return default!;
        return result.Deserialize<T>() ?? throw new LinkPocketApiException("响应反序列化失败");
    }
}
