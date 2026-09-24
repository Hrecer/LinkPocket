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
    /// <summary>照剧本吐 SSE 行的假传输层（CI 不打真实网络；每个剧本项对应一次模型请求）。</summary>
    internal sealed class ScriptedTransport(params string[][] scripts) : IAiHttpTransport
    {
        private int _index;

        public int Requests => _index;

        public Task<AiHttpResponse> SendAsync(AiHttpRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("回合循环只用流式");

        public async IAsyncEnumerable<string> SendStreamLinesAsync(AiHttpRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
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

    internal static async Task ConfigureAsync(Host host, AiMode mode, bool advancedTools = false)
    {
        await host.Assistant.SaveProviderAsync(new AiProviderDraft("gw", "Gateway", AiProtocol.OpenAiChat,
            "https://gw.example.com/v1", Enabled: true, IsLocal: false));
        await host.Assistant.SetApiKeyAsync("gw", "sk-1234567890abcdef");
        await host.Assistant.SaveModelAsync(new AiModelDraft("gw", "test-model", "Test model", true,
            ContextWindow: 32_000, MaxOutputTokens: 1024, SupportsTools: true, SupportsStreaming: true));
        var preferences = await host.Assistant.GetPreferencesAsync();
        await host.Assistant.SavePreferencesAsync(preferences with
        {
            ProviderId = "gw",
            ModelId = "test-model",
            AdvancedToolsEnabled = advancedTools,   // Tier 2（永久删除 / 审计清理…）要显式开启
        });

        var session = await host.Assistant.CreateSessionAsync();
        await host.Assistant.SetModeAsync(session.SessionId, mode);
    }
}
