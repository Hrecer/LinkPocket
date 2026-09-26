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
    IReadOnlyList<AiToolSpec> Tools, int MaxOutputTokens, bool Stream,
    /// <summary>本模型是否支持"服务端执行的原生联网搜索"（按厂商方言在 tools 里<b>声明</b>内置工具；
    /// 客户端**绝不自行执行**它——那样只会拿到一个自己无法出结果的 tool_call）。</summary>
    bool NativeWebSearch = false);

/// <summary>流式增量：Kind = text（正文） / reasoning（思考原文） / tool（工具调用拼装） / usage（用量读数）。
/// <para><c>ToolCallId</c> 是**归并键**（流式分片只稳定给出序号：OpenAI <c>idx:N</c> / Anthropic <c>blk:N</c>）；
/// <c>ProviderId</c> 是服务商给的真实调用 ID（可能只在首片出现，缺省用归并键兜底）。</para>
/// <para><c>InputTokens</c> / <c>OutputTokens</c>（Kind = usage）= 服务商下发的用量读数；语义 = **本次请求的累计值**
/// （非增量，取最后非空值即可）——OpenAI 需请求体带 <c>stream_options.include_usage</c>，
/// Anthropic 在 message_start / message_delta 里给。</para></summary>
public sealed record AiChatDelta(string Kind, string? Text = null, string? ToolCallId = null,
    string? ToolName = null, string? ArgumentsDelta = null, string? ProviderId = null,
    int? InputTokens = null, int? OutputTokens = null);

/// <summary>一次完整回复（非流式，或流式结束后的汇总）。</summary>
public sealed record AiChatCompletion(string Text, IReadOnlyList<AiToolCallRequest> ToolCalls,
    int? InputTokens, int? OutputTokens, string? Reasoning = null);

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
    public static readonly IAiProtocolAdapter OpenAiResponses = new OpenAiResponsesAdapter();

    public static IAiProtocolAdapter For(AiProtocol kind) => kind switch
    {
        AiProtocol.AnthropicMessages => AnthropicMessages,
        AiProtocol.OpenAiResponses => OpenAiResponses,
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
        if (request.Stream)
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (request.Tools.Count > 0 || request.NativeWebSearch)
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
            // 原生联网搜索（**服务端执行**的公司方言形状：小米 MiMo / 智谱 GLM / MiniMax 等
            // 在 chat/completions 里都收 {"type":"web_search"}；客户端只声明，结果由服务端回灌）。
            if (request.NativeWebSearch)
                tools.Add(new JsonObject { ["type"] = "web_search" });
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
        string? reasoning = null;
        var calls = new List<AiToolCallRequest>();
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var message = choices[0].GetProperty("message");
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                text = content.GetString() ?? "";
            reasoning = OpenAiReasoningText(message);
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
            AiJson.IntAt(doc.RootElement, "usage", "completion_tokens"), reasoning);
    }

    public AiChatDelta? ParseStreamLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var payload = line["data:".Length..].Trim();
        if (payload.Length == 0 || payload == "[DONE]") return null;
        using var doc = JsonDocument.Parse(payload);
        // usage 块（stream_options.include_usage）的 choices 是**空数组**：必须先判它再判 choices（见 WARNINGS 137）
        if (OpenAiUsage(doc.RootElement) is { } usage) return usage;
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
        if (!choices[0].TryGetProperty("delta", out var delta)) return null;

        // 思考原文：字段名各网关不一（DeepSeek/GLM/z.ai 系 reasoning_content，OpenRouter 系 reasoning）。
        // 必须在正文之前判——思考先来、正文后来；两者同帧时先给思考，下一帧再给正文（不丢内容）。
        if (OpenAiReasoningText(delta) is { Length: > 0 } reasoning)
            return new AiChatDelta("reasoning", Text: reasoning);

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

    /// <summary>
    /// 从一段 delta / message 里取思考原文。兼容两类字段名：
    /// <c>reasoning_content</c>（DeepSeek / GLM / z.ai 系）与 <c>reasoning</c>（OpenRouter 等网关，可能是字符串或
    /// <c>{content|text}</c> 对象）。取不到 = 该网关不返回思考，如实返回 null（不编造）。
    /// </summary>
    internal static string? OpenAiReasoningText(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;
        if (AiJson.String(node, "reasoning_content") is { Length: > 0 } direct) return direct;
        if (!node.TryGetProperty("reasoning", out var reasoning)) return null;
        if (reasoning.ValueKind == JsonValueKind.String) return reasoning.GetString();
        if (reasoning.ValueKind != JsonValueKind.Object) return null;
        return AiJson.String(reasoning, "content") ?? AiJson.String(reasoning, "text");
    }

    /// <summary>OpenAI 流式用量块（`stream_options.include_usage`；两个字段都缺 = 不是用量块）。</summary>
    private static AiChatDelta? OpenAiUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        var input = AiJson.Int(usage, "prompt_tokens");
        var output = AiJson.Int(usage, "completion_tokens");
        return input is null && output is null
            ? null
            : new AiChatDelta("usage", InputTokens: input, OutputTokens: output);
    }
}

/// <summary>OpenAI Responses 族（`/responses`）：system 走顶层 `instructions`，对话走 `input` 项数组
/// （助手消息拆成 `output_text` 项 + `function_call` 项，工具结果 = `function_call_output` 项），
/// 工具声明是扁平形态（`{type:function, name, …}`，不套 `function` 外层）；
/// 流式事件名一律 `response.*` 前缀，用量在 `response.completed` 的 `response.usage`。</summary>
public sealed class OpenAiResponsesAdapter : IAiProtocolAdapter
{
    public AiProtocol Kind => AiProtocol.OpenAiResponses;

    public AiHttpRequest BuildChatRequest(AiProviderInfo provider, string? apiKey, AiChatRequest request)
    {
        var input = new JsonArray();
        foreach (var message in request.Messages)
        {
            if (message.Role == "tool")
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = message.ToolCallId ?? "",
                    ["output"] = message.Text,
                });
                continue;
            }

            if (message.Role == "assistant")
            {
                if (message.ToolCalls is { Count: > 0 })
                    foreach (var call in message.ToolCalls)
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = call.Id,
                            ["name"] = call.Name,
                            ["arguments"] = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson,
                        });
                if (!string.IsNullOrEmpty(message.Text))
                    input.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "output_text", ["text"] = message.Text },
                        },
                    });
                continue;
            }

            input.Add(new JsonObject { ["role"] = "user", ["content"] = message.Text });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = input,
            ["max_output_tokens"] = request.MaxOutputTokens,
            ["stream"] = request.Stream,
        };
        if (!string.IsNullOrEmpty(request.System)) body["instructions"] = request.System;
        if (request.Tools.Count > 0 || request.NativeWebSearch)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.ParametersJson),
                });
            // 原生联网搜索（Responses 形状：OpenAI 官方 / DeepSeek 等收 {"type":"web_search"}，服务端执行）
            if (request.NativeWebSearch)
                tools.Add(new JsonObject { ["type"] = "web_search" });
            body["tools"] = tools;
        }

        return new AiHttpRequest("POST", $"{provider.BaseUrl.TrimEnd('/')}/responses",
            Headers(apiKey), body.ToJsonString());
    }

    public AiHttpRequest BuildModelsRequest(AiProviderInfo provider, string? apiKey)
        => new("GET", $"{provider.BaseUrl.TrimEnd('/')}/models", Headers(apiKey), null);

    public IReadOnlyList<string> ParseModels(string body)
        => AiJson.StringsAt(body, "data", "id");

    public AiChatCompletion ParseCompletion(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var text = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        var calls = new List<AiToolCallRequest>();
        if (doc.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            foreach (var item in output.EnumerateArray())
            {
                switch (AiJson.String(item, "type"))
                {
                    case "message":
                        if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                            foreach (var block in content.EnumerateArray())
                                if (AiJson.String(block, "type") == "output_text")
                                    text.Append(AiJson.String(block, "text"));
                        break;
                    case "function_call":
                        calls.Add(new AiToolCallRequest(
                            AiJson.String(item, "call_id") ?? $"call_{calls.Count}",
                            AiJson.String(item, "name") ?? "",
                            AiJson.String(item, "arguments") ?? "{}"));
                        break;
                    case "reasoning":
                        // 思考项：正文在 summary[].text
                        if (!item.TryGetProperty("summary", out var summary)
                            || summary.ValueKind != JsonValueKind.Array) break;
                        foreach (var part in summary.EnumerateArray())
                            reasoning.Append(AiJson.String(part, "text"));
                        break;
                }
            }
        return new AiChatCompletion(text.ToString(), calls,
            AiJson.IntAt(doc.RootElement, "usage", "input_tokens"),
            AiJson.IntAt(doc.RootElement, "usage", "output_tokens"),
            reasoning.Length > 0 ? reasoning.ToString() : null);
    }

    public AiChatDelta? ParseStreamLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var payload = line["data:".Length..].Trim();
        if (payload.Length == 0 || payload == "[DONE]") return null;
        using var doc = JsonDocument.Parse(payload);
        var type = AiJson.String(doc.RootElement, "type");
        switch (type)
        {
            case "response.output_text.delta":
                return AiJson.String(doc.RootElement, "delta") is { Length: > 0 } text
                    ? new AiChatDelta("text", Text: text)
                    : null;
            case "response.reasoning_summary_text.delta":
            case "response.reasoning_text.delta":
                return AiJson.String(doc.RootElement, "delta") is { Length: > 0 } thought
                    ? new AiChatDelta("reasoning", Text: thought)
                    : null;
            case "response.output_item.added":
            {
                if (!doc.RootElement.TryGetProperty("item", out var item)) return null;
                return AiJson.String(item, "type") == "function_call"
                    ? new AiChatDelta("tool", ToolCallId: MergeKey(doc.RootElement),
                        ToolName: AiJson.String(item, "name"), ProviderId: AiJson.String(item, "call_id"))
                    : null;
            }
            case "response.function_call_arguments.delta":
                return AiJson.String(doc.RootElement, "delta") is { Length: > 0 } args
                    ? new AiChatDelta("tool", ToolCallId: MergeKey(doc.RootElement), ArgumentsDelta: args)
                    : null;
            case "response.completed":
            {
                // 用量读数在 response.usage（语义 = 本次请求的累计值）
                if (!doc.RootElement.TryGetProperty("response", out var done)
                    || done.ValueKind != JsonValueKind.Object) return null;
                var inputTokens = AiJson.IntAt(done, "usage", "input_tokens");
                var outputTokens = AiJson.IntAt(done, "usage", "output_tokens");
                return inputTokens is null && outputTokens is null
                    ? null
                    : new AiChatDelta("usage", InputTokens: inputTokens, OutputTokens: outputTokens);
            }
            default:
                return null;
        }
    }

    /// <summary>流式工具调用的归并键 = 输出项序号（真实调用 id 只在 `output_item.added` 出现）。</summary>
    private static string? MergeKey(JsonElement root)
        => root.TryGetProperty("output_index", out var index) && index.TryGetInt32(out var n) ? $"out:{n}" : null;

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
        if (request.Tools.Count > 0 || request.NativeWebSearch)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = JsonNode.Parse(tool.ParametersJson),
                });
            // 原生联网搜索（Anthropic server tool：服务端执行、结果自动回灌；客户端只声明）
            if (request.NativeWebSearch)
                tools.Add(new JsonObject
                {
                    ["type"] = "web_search_20250305",
                    ["name"] = "web_search",
                    ["max_uses"] = 5,
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
        var reasoning = new System.Text.StringBuilder();
        var calls = new List<AiToolCallRequest>();
        if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                var type = AiJson.String(block, "type");
                if (type == "text") text.Append(AiJson.String(block, "text"));
                else if (type == "thinking") reasoning.Append(AiJson.String(block, "thinking"));
                else if (type == "tool_use")
                    calls.Add(new AiToolCallRequest(
                        AiJson.String(block, "id") ?? $"tool_{calls.Count}",
                        AiJson.String(block, "name") ?? "",
                        block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}"));
            }
        return new AiChatCompletion(text.ToString(), calls,
            AiJson.IntAt(doc.RootElement, "usage", "input_tokens"),
            AiJson.IntAt(doc.RootElement, "usage", "output_tokens"),
            reasoning.Length > 0 ? reasoning.ToString() : null);
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
            case "message_start":
                // 输入侧用量在 message_start 里（含缓存读写三段，互不重叠）
                return AnthropicUsage(doc.RootElement, "message", inputSide: true);
            case "message_delta":
                return AnthropicUsage(doc.RootElement, "", inputSide: false);
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
                // 扩展思考：Anthropic 的思考走独立的 thinking_delta（block 类型是 thinking）
                if (deltaType == "thinking_delta")
                    return AiJson.String(delta, "thinking") is { Length: > 0 } thought
                        ? new AiChatDelta("reasoning", Text: thought)
                        : null;
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

    /// <summary>Anthropic 用量：message_start 给输入侧（含缓存读写三段，互不重叠），message_delta 给输出侧。</summary>
    private static AiChatDelta? AnthropicUsage(JsonElement root, string container, bool inputSide)
    {
        if (container.Length > 0
            && (!root.TryGetProperty(container, out var nested) || nested.ValueKind != JsonValueKind.Object))
            return null;
        var scope = container.Length > 0 ? root.GetProperty(container) : root;
        if (!scope.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

        if (inputSide)
        {
            var input = Sum(AiJson.Int(usage, "input_tokens"), AiJson.Int(usage, "cache_creation_input_tokens"),
                AiJson.Int(usage, "cache_read_input_tokens"));
            return input is null ? null : new AiChatDelta("usage", InputTokens: input);
        }

        var output = AiJson.Int(usage, "output_tokens");
        return output is null ? null : new AiChatDelta("usage", OutputTokens: output);
    }

    private static int? Sum(params int?[] values)
    {
        int? total = null;
        foreach (var value in values)
            if (value is { } number)
                total = (total ?? 0) + number;
        return total;
    }

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

    public static int? Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

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
