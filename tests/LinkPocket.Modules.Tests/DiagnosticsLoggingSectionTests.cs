using System.Text.Json;
using LinkPocket.Composition;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// diagnostics.collect 的 **logging 段**（S2）：观测面读数必须"如实"——未装配管道时 wired=false 且计数为 null
/// （不填假值），装配后读数来自 LpLog 的实时计数（单一数据源）。
/// ⚠️ LpLog 是进程级静态：本类与 <see cref="LogCommandTests"/> 同属一个 xunit Collection（同集合内串行），
/// 每个用例自装自卸——否则两类的"装配 / 未装配"起点会互相插入。
/// </summary>
[Collection("日志管道")]
public class DiagnosticsLoggingSectionTests
{
    [Fact]
    public async Task 未装配日志管道_读数如实为false与null()
    {
        LpLog.Configure(null);   // 防御：确保本用例起点未装配（其它用例可能装过）
        var (engine, _, _) = TestHost.Create();

        var json = await engine.QueryAsync<JsonElement>("diagnostics.collect");
        var logging = json.GetProperty("logging");
        Assert.False(logging.GetProperty("wired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, logging.GetProperty("level").ValueKind);
        Assert.Equal(JsonValueKind.Null, logging.GetProperty("dropped").ValueKind);
        Assert.Equal(0, logging.GetProperty("files").GetInt32());
        Assert.Equal(0, logging.GetProperty("files_bytes").GetInt64());
        Assert.True(logging.GetProperty("unconfigured_drops").GetInt64() >= 0);
    }

    [Fact]
    public async Task 装配后_读数来自LpLog实时计数()
    {
        var dir = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), "diag-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pipeline = EngineComposer.ConfigureLogging(new LoggingOptions { Directory = dir, MinimumLevel = LogLevel.Debug });
        try
        {
            LpLog.Info("诊断读数用例：写一条日志");
            pipeline.Flush(TimeSpan.FromSeconds(2));

            var (engine, _, _) = TestHost.Create();
            var json = await engine.QueryAsync<JsonElement>("diagnostics.collect");
            var logging = json.GetProperty("logging");

            Assert.True(logging.GetProperty("wired").GetBoolean());
            Assert.Equal("debug", logging.GetProperty("level").GetString());
            Assert.Equal(dir, logging.GetProperty("directory").GetString());
            Assert.Equal(1, logging.GetProperty("files").GetInt32());
            Assert.True(logging.GetProperty("accepted").GetInt64() >= 1);
            Assert.True(logging.GetProperty("written").GetInt64() >= 1);
            Assert.Equal(0, logging.GetProperty("dropped").GetInt64());
            Assert.Equal(0, logging.GetProperty("failed").GetInt64());

            // 审计段：新库没有任何审计行（行数与最旧时刻如实为空）
            var audit = json.GetProperty("audit");
            Assert.Equal(0, audit.GetProperty("rows").GetInt32());
            Assert.Equal(JsonValueKind.Null, audit.GetProperty("oldest_at").ValueKind);
        }
        finally
        {
            LpLog.Shutdown();
            try { Directory.Delete(dir, recursive: true); } catch { /* 测试清理尽力而为 */ }
        }
    }
}
