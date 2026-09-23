using System.Text.Json;
using System.Text.Json.Nodes;
using LinkPocket.Contracts;

namespace LinkPocket.Ai;

/// <summary>一条中性聊天消息（协议无关）：role ∈ user / assistant / tool；tool 消息携带 ToolCallId。</summary>
public sealed record AiChatMessage(string Role, string Text,
    IReadOnlyList<AiToolCallRequest>? ToolCalls = null, string? ToolCallId = null);

/// <summary>模型要求的一次工具调用（ArgumentsJson = 参数 JSON 原文）。</summary>
public sealed record AiToolCallRequest(string Id, string Name, string ArgumentsJson);

/// <summary>暴露给模型的工具声明（ParametersJson = JSON Schema 原文）。</summary>
public sealed record AiToolSpec(string Name, string Description, string ParametersJson);

/// <summary>一次模型请求（中性形态）。</summary>
public sealed record AiChatRequest(
    string Model, string System, IReadOnlyList<AiChatMessage> Messages,
    IReadOnlyList<AiToolSpec> Tools, int MaxOutputTokens, bool Stream);

/// <summary>流式增量：Kind = text（正文） / tool（工具调用拼装）。
/// <para><c>ToolCallId</c> 是**归并键**（流式分片只稳定给出序号：OpenAI <c>idx:N</c> / Anthropic <c>blk:N</c>）；
/// <c>ProviderId</c> 是服务商给的真实调用 ID（可能只在首片出现，缺省用归并键兜底）。</para></summary>
public sealed record AiChatDelta(string Kind, string? Text = null, string? ToolCallId = null,
    string? ToolName = null, string? ArgumentsDelta = null, string? ProviderId = null);

/// <summary>一次完整回复（非流式，或流式结束后的汇总）。</summary>
public sealed record AiChatCompletion(string Text, IReadOnlyList<AiToolCallRequest> ToolCalls,
    int? InputTokens, int? OutputTokens);

/// <summary>协议适配器：请求装配 / 响应解析 / 模型列表 / 流式行解析（每协议一份，纯函数无状态）。</summary>
public interface IAiProtocolAdapter
{
    AiProtocol Kind { get; }
    AiHttpRequest BuildChatRequest(AiProviderInfo provider, string? apiKey, AiChatRequest request);
    AiHttpRequest BuildModelsRequest(AiProviderInfo provider, string? apiKey);
    IReadOnlyList<string> ParseModels(string body);
    AiChatCompletion ParseCompletion(string body);
    /// <summary>解析一行原始 SSE 行 → 增量（null = 该行无需处理）。</summary>
    AiChatDelta? ParseStreamLine(string line);
}

/// <summary>按协议取适配器（唯一路由点）。</summary>
public static class AiProtocols
{
    public static readonly IAiProtocolAdapter OpenAiChat = new OpenAiChatAdapter();
    public static readonly IAiProtocolAdapter AnthropicMessages = new AnthropicMessagesAdapter();

    public static IAiProtocolAdapter For(AiProtocol kind) => kind switch
    {
        AiProtocol.AnthropicMessages => AnthropicMessages,
        _ => OpenAiChat,
    };
}

/// <summary>OpenAI 兼容族（/chat/completions；国内网关与本地推理同族）。</summary>
public sealed class OpenAiChatAdapter : IAiProtocolAdapter
{
    public AiProtocol Kind => AiProtocol.OpenAiChat;

    public AiHttpRequest BuildChatRequest(AiProviderInfo provider, string? apiKey, AiChatRequest request)
    {
        var messages = new JsonArray();
        messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        foreach (var message in request.Messages)
        {
            var item = new JsonObject { ["role"] = message.Role };
            if (message.Role == "tool")
            {
                item["tool_call_id"] = message.ToolCallId ?? "";
                item["content"] = message.Text;
            }
            else if (message.ToolCalls is { Count: > 0 })
            {
                item["content"] = message.Text;
                var calls = new JsonArray();
                foreach (var call in message.ToolCalls)
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.ArgumentsJson },
                    });
                item["tool_calls"] = calls;
            }
            else
            {
                item["content"] = message.Text;
            }
            messages.Add(item);
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = request.Stream,
        };
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.ParametersJson),
                    },
                });
            body["tools"] = tools;
        }

        return new AiHttpRequest("POST", $"{provider.BaseUrl.TrimEnd('/')}/chat/completions",
            Headers(apiKey), body.ToJsonString());
    }

    public AiHttpRequest BuildModelsRequest(AiProviderInfo provider, string? apiKey)
        => new("GET", $"{provider.BaseUrl.TrimEnd('/')}/models", Headers(apiKey), null);

    public IReadOnlyList<string> ParseModels(string body)
        => AiJson.StringsAt(body, "data", "id");

    public AiChatCompletion ParseCompletion(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var text = "";
        var calls = new List<AiToolCallRequest>();
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var message = choices[0].GetProperty("message");
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                text = content.GetString() ?? "";
            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                foreach (var call in toolCalls.EnumerateArray())
                {
                    var function = call.GetProperty("function");
                    calls.Add(new AiToolCallRequest(
                        AiJson.String(call, "id") ?? $"call_{calls.Count}",
                        AiJson.String(function, "name") ?? "",
                        AiJson.String(function, "arguments") ?? "{}"));
                }
        }
        return new AiChatCompletion(text, calls,
            AiJson.IntAt(doc.RootElement, "usage", "prompt_tokens"),
            AiJson.IntAt(doc.RootElement, "usage", "completion_tokens"));
    }

    public AiChatDelta? ParseStreamLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var payload = line["data:".Length..].Trim();
        if (payload.Length == 0 || payload == "[DONE]") return null;
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
        if (!choices[0].TryGetProperty("delta", out var delta)) return null;

        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            && content.GetString() is { Length: > 0 } text)
            return new AiChatDelta("text", Text: text);

        if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            foreach (var call in toolCalls.EnumerateArray())
            {
                var index = call.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0;
                var id = AiJson.String(call, "id");
                string? name = null, args = null;
                if (call.TryGetProperty("function", out var function))
                {
                    name = AiJson.String(function, "name");
                    args = AiJson.String(function, "arguments");
                }
                if (id is null && name is null && args is null) continue;
                // 归并键恒为 idx:N（分片只有首片带 id）；真实 id 走 ProviderId
                return new AiChatDelta("tool", ToolCallId: $"idx:{index}", ToolName: name,
                    ArgumentsDelta: args, ProviderId: id);
            }
        return null;
    }

    private static Dictionary<string, string> Headers(string? apiKey)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(apiKey)) headers["Authorization"] = $"Bearer {apiKey}";
        return headers;
    }
}

/// <summary>Anthropic Messages 族（/v1/messages；system 为顶层字段、工具结果包在 user 消息里）。</summary>
public sealed class AnthropicMessagesAdapter : IAiProtocolAdapter
{
    public AiProtocol Kind => AiProtocol.AnthropicMessages;

    public AiHttpRequest BuildChatRequest(AiProviderInfo provider, string? apiKey, AiChatRequest request)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            if (message.Role == "tool")
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = message.ToolCallId ?? "",
                            ["content"] = message.Text,
                        },
                    },
                });
                continue;
            }

            var blocks = new JsonArray();
            if (!string.IsNullOrEmpty(message.Text)) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = message.Text });
            if (message.ToolCalls is { Count: > 0 })
                foreach (var call in message.ToolCalls)
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson),
                    });
            if (blocks.Count == 0) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = "" });
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = blocks });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["system"] = request.System,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = request.Stream,
        };
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = JsonNode.Parse(tool.ParametersJson),
                });
            body["tools"] = tools;
        }

        return new AiHttpRequest("POST", $"{provider.BaseUrl.TrimEnd('/')}/v1/messages",
            Headers(apiKey), body.ToJsonString());
    }

    public AiHttpRequest BuildModelsRequest(AiProviderInfo provider, string? apiKey)
        => new("GET", $"{provider.BaseUrl.TrimEnd('/')}/v1/models", Headers(apiKey), null);

    public IReadOnlyList<string> ParseModels(string body)
        => AiJson.StringsAt(body, "data", "id");

    public AiChatCompletion ParseCompletion(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var text = new System.Text.StringBuilder();
        var calls = new List<AiToolCallRequest>();
        if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                var type = AiJson.String(block, "type");
                if (type == "text") text.Append(AiJson.String(block, "text"));
                else if (type == "tool_use")
                    calls.Add(new AiToolCallRequest(
                        AiJson.String(block, "id") ?? $"tool_{calls.Count}",
                        AiJson.String(block, "name") ?? "",
                        block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}"));
            }
        return new AiChatCompletion(text.ToString(), calls,
            AiJson.IntAt(doc.RootElement, "usage", "input_tokens"),
            AiJson.IntAt(doc.RootElement, "usage", "output_tokens"));
    }

    public AiChatDelta? ParseStreamLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var payload = line["data:".Length..].Trim();
        if (payload.Length == 0) return null;
        using var doc = JsonDocument.Parse(payload);
        var type = AiJson.String(doc.RootElement, "type");
        switch (type)
        {
            case "content_block_start":
            {
                if (!doc.RootElement.TryGetProperty("content_block", out var block)) return null;
                return AiJson.String(block, "type") == "tool_use"
                    ? new AiChatDelta("tool", ToolCallId: StreamKey(doc.RootElement),
                        ToolName: AiJson.String(block, "name"), ProviderId: AiJson.String(block, "id"))
                    : null;
            }
            case "content_block_delta":
            {
                if (!doc.RootElement.TryGetProperty("delta", out var delta)) return null;
                var deltaType = AiJson.String(delta, "type");
                if (deltaType == "text_delta") return new AiChatDelta("text", Text: AiJson.String(delta, "text"));
                if (deltaType == "input_json_delta")
                    return new AiChatDelta("tool", ToolCallId: StreamKey(doc.RootElement),
                        ArgumentsDelta: AiJson.String(delta, "partial_json"));
                return null;
            }
            default:
                return null;
        }
    }

    /// <summary>流式工具调用的归并键 = content_block 序号（真实 id 只在 start 事件出现）。</summary>
    private static string? StreamKey(JsonElement root)
        => root.TryGetProperty("index", out var index) && index.TryGetInt32(out var n) ? $"blk:{n}" : null;

    private static Dictionary<string, string> Headers(string? apiKey)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic-version"] = "2023-06-01",
        };
        if (!string.IsNullOrWhiteSpace(apiKey)) headers["x-api-key"] = apiKey;
        return headers;
    }
}

/// <summary>轻量 JSON 读取（缺键/类型不符一律给 null，不抛）。</summary>
internal static class AiJson
{
    public static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? IntAt(JsonElement element, string parent, string name)
        => element.TryGetProperty(parent, out var node) && node.TryGetProperty(name, out var value)
           && value.TryGetInt32(out var result)
            ? result
            : null;

    public static IReadOnlyList<string> StringsAt(string body, string arrayName, string fieldName)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray()
            .Select(item => String(item, fieldName))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();
    }
}
