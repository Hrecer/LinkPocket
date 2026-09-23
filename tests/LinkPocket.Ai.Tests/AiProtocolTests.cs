using System.Net;
using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Ai.Tests;

/// <summary>协议适配与 HTTP 传输（桩处理器，不打真实网络）：请求装配 / 解析 / 流式拼装 / 错误映射 / 重试。</summary>
public class AiProtocolTests
{
    private static AiProviderInfo Provider(AiProtocol protocol, string baseUrl) => new(
        "p", "P", protocol, baseUrl, AiProviderSource.Preset, IsLocal: false, Enabled: true,
        HasApiKey: true, ApiKeyMasked: "sk-1…cdef", AiProviderStatus.Configured, null, null, null, []);

    private static AiToolSpec Tool() => new("folders.find", "find folders",
        """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""");

    [Fact]
    public void OpenAI协议_请求装配_系统消息与工具数组_鉴权头()
    {
        var request = new AiChatRequest("gpt-4o", "SYS",
            [new AiChatMessage("user", "hi"),
             new AiChatMessage("assistant", "", [new AiToolCallRequest("call_1", "folders.find", """{"name":"x"}""")]),
             new AiChatMessage("tool", """{"ok":true}""", null, "call_1")],
            [Tool()], 1024, Stream: true);

        var http = AiProtocols.OpenAiChat.BuildChatRequest(Provider(AiProtocol.OpenAiChat, "https://api.example.com/v1"),
            "sk-1234567890abcdef", request);

        Assert.Equal("POST", http.Method);
        Assert.Equal("https://api.example.com/v1/chat/completions", http.Url);
        Assert.Equal("Bearer sk-1234567890abcdef", http.Headers["Authorization"]);
        using var doc = JsonDocument.Parse(http.BodyJson!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("call_1", messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("function", doc.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void Anthropic协议_请求装配_system顶层与工具结果包裹()
    {
        var request = new AiChatRequest("claude-x", "SYS",
            [new AiChatMessage("user", "hi"), new AiChatMessage("tool", """{"ok":true}""", null, "tool_1")],
            [Tool()], 1024, Stream: false);

        var http = AiProtocols.AnthropicMessages.BuildChatRequest(
            Provider(AiProtocol.AnthropicMessages, "https://api.anthropic.com"), "sk-1234567890abcdef", request);

        Assert.Equal("https://api.anthropic.com/v1/messages", http.Url);
        Assert.Equal("sk-1234567890abcdef", http.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", http.Headers["anthropic-version"]);
        using var doc = JsonDocument.Parse(http.BodyJson!);
        Assert.Equal("SYS", doc.RootElement.GetProperty("system").GetString());
        var toolResult = doc.RootElement.GetProperty("messages")[1].GetProperty("content")[0];
        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("tool_1", toolResult.GetProperty("tool_use_id").GetString());
        var tool = doc.RootElement.GetProperty("tools")[0];
        Assert.Equal("folders.find", tool.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, tool.GetProperty("input_schema").ValueKind);
    }

    [Fact]
    public void 两协议_模型列表解析一致()
    {
        const string body = """{"data":[{"id":"m-1"},{"id":"m-2"}]}""";
        Assert.Equal(["m-1", "m-2"], AiProtocols.OpenAiChat.ParseModels(body));
        Assert.Equal(["m-1", "m-2"], AiProtocols.AnthropicMessages.ParseModels(body));
    }

    [Fact]
    public void 流式行解析_文本与工具调用按序号归并()
    {
        var openaiText = AiProtocols.OpenAiChat.ParseStreamLine(
            """data: {"choices":[{"delta":{"content":"he"}}]}""");
        Assert.Equal("text", openaiText!.Kind);
        Assert.Equal("he", openaiText.Text);

        var first = AiProtocols.OpenAiChat.ParseStreamLine(
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_9","function":{"name":"folders.find","arguments":"{\"na"}}]}}]}""");
        var second = AiProtocols.OpenAiChat.ParseStreamLine(
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"me\":\"x\"}"}}]}}]}""");
        Assert.Equal("idx:0", first!.ToolCallId);
        Assert.Equal("call_9", first.ProviderId);
        Assert.Equal("folders.find", first.ToolName);
        Assert.Equal("idx:0", second!.ToolCallId);   // 同一归并键 → 参数可拼装
        Assert.Null(AiProtocols.OpenAiChat.ParseStreamLine("data: [DONE]"));

        var blockStart = AiProtocols.AnthropicMessages.ParseStreamLine(
            """data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"tool_7","name":"links.query"}}""");
        var blockDelta = AiProtocols.AnthropicMessages.ParseStreamLine(
            """data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"a"}}""");
        Assert.Equal("blk:1", blockStart!.ToolCallId);
        Assert.Equal("tool_7", blockStart.ProviderId);
        Assert.Equal("blk:1", blockDelta!.ToolCallId);
        Assert.Equal("""{"a""", blockDelta.ArgumentsDelta);
    }

    [Fact]
    public void 非流式回复解析_文本与工具调用()
    {
        var openai = AiProtocols.OpenAiChat.ParseCompletion("""
            {"choices":[{"message":{"content":"hi","tool_calls":[{"id":"c1","function":{"name":"f","arguments":"{}"}}]}}],"usage":{"prompt_tokens":3,"completion_tokens":4}}
            """);
        Assert.Equal("hi", openai.Text);
        Assert.Equal("c1", openai.ToolCalls[0].Id);
        Assert.Equal(3, openai.InputTokens);

        var anthropic = AiProtocols.AnthropicMessages.ParseCompletion("""
            {"content":[{"type":"text","text":"hi"},{"type":"tool_use","id":"t1","name":"f","input":{"a":1}}]}
            """);
        Assert.Equal("hi", anthropic.Text);
        Assert.Equal("""{"a":1}""", anthropic.ToolCalls[0].ArgumentsJson);
    }

    [Fact]
    public async Task 传输_状态码映射为稳定错误码_且429可重试()
    {
        using var auth = new HttpAiTransport(Stub(HttpStatusCode.Unauthorized, "no"));
        var authError = await Assert.ThrowsAsync<AiException>(() => auth.SendAsync(Get()));
        Assert.Equal(AiErrors.AuthFailed, authError.Error.Code);
        Assert.False(authError.Error.Retryable);

        using var limited = new HttpAiTransport(Stub(HttpStatusCode.TooManyRequests, "slow down"), maxAttempts: 1);
        var limitedError = await Assert.ThrowsAsync<AiException>(() => limited.SendAsync(Get()));
        Assert.Equal(AiErrors.UpstreamRateLimited, limitedError.Error.Code);
        Assert.True(limitedError.Error.Retryable);

        var downHandler = Stub(HttpStatusCode.BadGateway, "boom");
        using var down = new HttpAiTransport(downHandler, maxAttempts: 3);
        var downError = await Assert.ThrowsAsync<AiException>(() => down.SendAsync(Get()));
        Assert.Equal(AiErrors.ProviderUnreachable, downError.Error.Code);
        Assert.Equal(3, downHandler.Attempts);   // 退避重试三次
    }

    [Fact]
    public async Task 传输_流式逐行吐出SSE原始行()
    {
        using var transport = new HttpAiTransport(Stub(HttpStatusCode.OK,
            "data: {\"a\":1}\n\ndata: {\"a\":2}\n\ndata: [DONE]\n"));
        var lines = new List<string>();
        await foreach (var line in transport.SendStreamLinesAsync(Get())) lines.Add(line);

        Assert.Equal("data: {\"a\":1}", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("data: [DONE]", lines[^1]);
    }

    private static AiHttpRequest Get()
        => new("GET", "https://api.example.com/v1/models", new Dictionary<string, string>(), null);

    private static StubHandler Stub(HttpStatusCode status, string body) => new(status, body);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
