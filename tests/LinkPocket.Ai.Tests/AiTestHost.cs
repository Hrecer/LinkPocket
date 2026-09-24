using System.Runtime.CompilerServices;
using System.Text.Json;
using LinkPocket.Composition;
using LinkPocket.Contracts;

namespace LinkPocket.Ai.Tests;

/// <summary>
/// 回合链路测试的公共宿主（假传输层 + 真实引擎 + 临时库）：
/// AiLoopTests 与 AiReconcileTests 共用同一套搭建方式（一份实现，不复制）。
/// </summary>
internal static class AiTestHost
{
    /// <summary>照剧本吐 SSE 行 / 非流式回复的假传输层（CI 不打真实网络；每个剧本项对应一次模型请求）。</summary>
    internal sealed class ScriptedTransport(params string[][] scripts) : IAiHttpTransport
    {
        private readonly Queue<string> _completions = new();
        private int _index;

        public int Requests => _index;

        /// <summary>每次请求的原始入参（断言"模型看到了什么"用：系统提示 / 历史 / 工具清单）。</summary>
        public List<AiHttpRequest> Captured { get; } = [];

        /// <summary>排队的非流式回复（摘要请求用；空队列 = 该请求未编排，抛错暴露）。</summary>
        public void EnqueueCompletion(string body) => _completions.Enqueue(body);

        public Task<AiHttpResponse> SendAsync(AiHttpRequest request, CancellationToken ct = default)
        {
            Captured.Add(request);
            if (_completions.Count == 0)
                throw new NotSupportedException("no scripted completion for a non-streaming request");
            return Task.FromResult(new AiHttpResponse(200, _completions.Dequeue()));
        }

        public async IAsyncEnumerable<string> SendStreamLinesAsync(AiHttpRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Captured.Add(request);
            var script = scripts[Math.Min(_index++, scripts.Length - 1)];
            foreach (var line in script)
            {
                await Task.Yield();
                yield return line;
            }
        }
    }

    internal static string TextChunk(string text)
        => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } });

    /// <summary>OpenAI 流式用量块（`stream_options.include_usage`；choices 是空数组）。</summary>
    internal static string UsageChunk(int promptTokens, int completionTokens)
        => "data: " + JsonSerializer.Serialize(new
        {
            choices = Array.Empty<object>(),
            usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens },
        });

    /// <summary>非流式回复体（摘要请求的剧本）。</summary>
    internal static string Completion(string text, int promptTokens = 10, int completionTokens = 5)
        => JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = text } } },
            usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens },
        });

    /// <summary>Anthropic 流式：message_start（输入侧用量）+ 文本增量 + message_delta（输出侧用量）。</summary>
    internal static string[] AnthropicStream(params string[] texts)
    {
        var lines = new List<string>
        {
            "event: message_start",
            "data: " + JsonSerializer.Serialize(new
            {
                type = "message_start",
                message = new { usage = new { input_tokens = 88 } },
            }),
        };
        foreach (var text in texts)
        {
            lines.Add("event: content_block_delta");
            lines.Add("data: " + JsonSerializer.Serialize(new
            {
                type = "content_block_delta",
                index = 0,
                delta = new { type = "text_delta", text },
            }));
        }
        lines.Add("event: message_delta");
        lines.Add("data: " + JsonSerializer.Serialize(new
        {
            type = "message_delta",
            usage = new { output_tokens = 21 },
        }));
        return lines.ToArray();
    }

    internal static string ToolChunk(int index, string? id, string? name, string? argsJson)
        => "data: " + JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { delta = new { tool_calls = new[] { new { index, id, function = new { name, arguments = argsJson } } } } },
            },
        });

    internal sealed record Host(IAiAssistant Assistant, EngineClient Client, ISessionManager Sessions,
        ScriptedTransport Transport, string DataRoot, string DbPath) : IDisposable
    {
        public void Dispose()
        {
            AiTestEnv.Drop(DataRoot);
            TestEnvCleanup(DbPath);
        }
    }

    internal static Host NewHost(string[][] scripts)
    {
        var dbPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpai_{Guid.NewGuid():N}.db");
        var dataRoot = AiTestEnv.NewRoot();
        var composed = EngineComposer.Compose(dbPath);
        var transport = new ScriptedTransport(scripts);
        var assistant = AiRuntime.Create(dataRoot, composed.Client, composed.Sessions, transport);
        return new Host(assistant, composed.Client, composed.Sessions, transport, dataRoot, dbPath);
    }

    /// <summary>带预置数据的宿主：先 seed，再用真实 ID 生成模型剧本（模型/测试需要知道实体 ID）。</summary>
    internal static async Task<Host> NewSeededHostAsync(Func<EngineClient, Task> seed,
        Func<EngineClient, Task<string[][]>> scripts)
    {
        var dbPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpai_{Guid.NewGuid():N}.db");
        var dataRoot = AiTestEnv.NewRoot();
        var composed = EngineComposer.Compose(dbPath);
        await seed(composed.Client);
        var transport = new ScriptedTransport(await scripts(composed.Client));
        var assistant = AiRuntime.Create(dataRoot, composed.Client, composed.Sessions, transport);
        return new Host(assistant, composed.Client, composed.Sessions, transport, dataRoot, dbPath);
    }

    internal static void TestEnvCleanup(string dbPath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }

    internal static async Task<string> ConfigureAsync(Host host, AiMode mode, bool advancedTools = false,
        int contextWindow = 32_000, AiProtocol protocol = AiProtocol.OpenAiChat,
        string baseUrl = "https://gw.example.com/v1")
    {
        await host.Assistant.SaveProviderAsync(new AiProviderDraft("gw", "Gateway", protocol,
            baseUrl, Enabled: true, IsLocal: false));
        await host.Assistant.SetApiKeyAsync("gw", "sk-1234567890abcdef");
        await host.Assistant.SaveModelAsync(new AiModelDraft("gw", "test-model", "Test model", true,
            ContextWindow: contextWindow, MaxOutputTokens: 1024, SupportsTools: true, SupportsStreaming: true));
        var preferences = await host.Assistant.GetPreferencesAsync();
        await host.Assistant.SavePreferencesAsync(preferences with
        {
            ProviderId = "gw",
            ModelId = "test-model",
            AdvancedToolsEnabled = advancedTools,   // Tier 2（永久删除 / 审计清理…）要显式开启
        });

        // 引擎测试要的是「一条可用的正式会话」，不关心草稿阶段：**不建草稿**（草稿只在专门用例里断言），
        // 直接落一份空会话文件——于是 `ListSessionsAsync()` 能列出它、`ReadSessionFile` 能读到它。
        var sessionId = $"s-{Guid.NewGuid():N}";
        SeedSessionFile(host.DataRoot, sessionId, []);
        await host.Assistant.SetModeAsync(sessionId, mode);
        return sessionId;
    }

    /// <summary>会话文件里的一行模型历史（预置长历史用；字段名与 <c>AiSessionStore</c> 的序列化一致）。</summary>
    internal sealed record ChatLine(string Role, string Text,
        IReadOnlyList<AiToolCallRequest>? ToolCalls = null, string? ToolCallId = null);

    /// <summary>读会话文件的原始 JSON（黑盒断言：文件形状就是契约，见功能书 §9.1）。</summary>
    internal static JsonElement ReadSessionFile(string dataRoot, string sessionId)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "sessions", sessionId + ".json")))
            .RootElement.Clone();

    /// <summary>
    /// 预置一个**正式**会话文件（长历史 / 熔断计数 / 跨会话读取目标），模拟"上次留下的会话"。
    /// 走 <see cref="AiSessionStore"/> 写入，并顺手丢弃同 id 的内存草稿——否则 <c>CreateSessionAsync</c>
    /// 刚造的草稿会遮住这份预置文件（草稿在内存里优先于磁盘）。
    /// </summary>
    internal static void SeedSessionFile(string dataRoot, string sessionId, IEnumerable<ChatLine> chat,
        int compactFailures = 0, IEnumerable<(string Text, AiRole Role)>? messages = null)
    {
        var store = new AiSessionStore(dataRoot);
        store.DiscardDraft(sessionId);   // 同 id 草稿清掉：预置的就是"已经在库里的正式会话"
        var now = DateTimeOffset.UtcNow;
        var seq = 0;
        var uiMessages = (messages ?? []).Select(item => new AiMessage($"m-seed{++seq}", seq, item.Role,
            item.Text, now, null)).ToArray();
        var file = AiSessionFile.Create(new AiSessionSummary(sessionId, "seeded", AiMode.AutoApply,
            null, null, now, now, uiMessages.Length, 0, null, AiSessionPersistence.Immediate));
        file.Messages.AddRange(uiMessages);
        file.Chat.AddRange(chat.Select(line => new AiChatMessage(line.Role, line.Text,
            line.ToolCalls, line.ToolCallId)));
        file.CompactFailures = compactFailures;
        store.Save(file);
    }

    /// <summary>会话文件里 Chat 的正文序列（断言占位符 / 摘要注入）。</summary>
    internal static IReadOnlyList<string> ChatTexts(JsonElement sessionFile)
        => sessionFile.GetProperty("Chat").EnumerateArray()
            .Select(item => item.GetProperty("Text").GetString() ?? "")
            .ToArray();

    /// <summary>
    /// 请求体的**解码后文本**：默认 encoder 会把非 ASCII 转义成 <c>\uXXXX</c>，
    /// 断言中文 / canonical 路径前必须先解码（否则永远查不到）。
    /// </summary>
    internal static string BodyText(AiHttpRequest request)
    {
        using var doc = JsonDocument.Parse(request.BodyJson!);
        var builder = new System.Text.StringBuilder();
        Walk(doc.RootElement);
        return builder.ToString();

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    builder.AppendLine(element.GetString());
                    break;
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    builder.AppendLine(element.GetRawText());
                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject()) Walk(property.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Walk(item);
                    break;
            }
        }
    }

    /// <summary>宏脚本的最小可用形状（macro.save 的校验要求至少一步）。</summary>
    internal static object MinimalMacroScript(string name)
        => new { name, steps = new[] { new { @ref = "a", command = "folders.tree" } } };
}
