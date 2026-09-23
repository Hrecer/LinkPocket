using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 批模板谓词（P3-3：<c>[n]</c> / <c>[*]</c> / <c>length</c>）与批步骤上限（P3-4：<see cref="EngineLimits.MaxBatchSteps"/>）：
/// 谓词让"把查询结果整批搬走"一步到位（模型不用回显几百个 ID）；越界/非数组**如实报错**；
/// 步骤上限在进写闸前拒绝（长脚本不再独占全局写闸），<c>macro.save/run</c> 同一道校验。
/// </summary>
public class BatchScriptTests
{
    private static (EngineCore Engine, string Db) Create()
    {
        var (factory, path) = TestEnv.CreateDb();
        var engine = TestEnv.CreateEngine(factory, withOrchestration: true, sessions: null, new EchoHandler());
        return (engine, path);
    }

    /// <summary>第一步给一份数组负载（回声原样带回），第二步用谓词取值。</summary>
    private static BatchScript ProbeScript(object probeArgs) => new("谓词批",
    [
        new BatchStep("seed", "test.echo", JsonSerializer.SerializeToElement(new
        {
            items = new[] { new { id = "A" }, new { id = "B" }, new { id = "C" } },
            tags = new[] { "x", "y" },
        })),
        new BatchStep("probe", "test.echo", JsonSerializer.SerializeToElement(probeArgs)),
    ]);

    private static JsonElement EchoOf(BatchStepResult step) => ((JsonElement?)step.Data)!.Value.GetProperty("echo");

    [Fact]
    public async Task 模板谓词_下标_展开_长度_全链可用()
    {
        var (engine, path) = Create();
        try
        {
            var report = await engine.Batch!.RunAsync(ProbeScript(new
            {
                pick = "{seed.echo.items[1].id}",                  // [n] 数组下标
                mapped = "{seed.echo.items[*].id}",                // [*] 逐元映射 → 同形数组（可直接喂数组参数）
                count = "{seed.echo.items.length}",                // 数组长度
                tag_count = "{seed.echo.tags.length}",
                inline = "共{seed.echo.items.length}项",            // 内嵌引用照常
                spread_arg = new[] { "pre", "{seed.echo.items[*].id}" },   // 数组字面量里 = 展开摊平
            }));

            Assert.True(report.Ok);
            var echo = EchoOf(report.Steps[1]);
            Assert.Equal("B", echo.GetProperty("pick").GetString());
            Assert.Equal(new[] { "A", "B", "C" },
                echo.GetProperty("mapped").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.Equal(3, echo.GetProperty("count").GetInt32());
            Assert.Equal(2, echo.GetProperty("tag_count").GetInt32());
            Assert.Equal("共3项", echo.GetProperty("inline").GetString());
            Assert.Equal(new[] { "pre", "A", "B", "C" },
                echo.GetProperty("spread_arg").EnumerateArray().Select(e => e.GetString()).ToArray());
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 模板谓词_对象同名length属性_属性优先()
    {
        var (engine, path) = Create();
        try
        {
            var report = await engine.Batch!.RunAsync(new BatchScript("length 属性优先",
            [
                new BatchStep("seed", "test.echo", JsonSerializer.SerializeToElement(new { length = "属性值" })),
                new BatchStep("probe", "test.echo", JsonSerializer.SerializeToElement(new { got = "{seed.echo.length}" })),
            ]));
            Assert.Equal("属性值", EchoOf(report.Steps[1]).GetProperty("got").GetString());
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 模板谓词_越界与非数组展开_如实报错()
    {
        var (engine, path) = Create();
        try
        {
            // 下标越界
            var outOfRange = await Assert.ThrowsAsync<EngineException>(() =>
                engine.Batch!.RunAsync(ProbeScript(new { pick = "{seed.echo.items[9].id}" })));
            Assert.Equal(EngineErrors.TypeMismatch, outOfRange.Error.Code);
            Assert.Contains("out of range", outOfRange.Error.Message, StringComparison.Ordinal);

            // 展开作用在非数组上（items[0].id = 字符串）→ 如实报错
            var expand = await Assert.ThrowsAsync<EngineException>(() =>
                engine.Batch!.RunAsync(ProbeScript(new { bad = "{seed.echo.items[0].id[*]}" })));
            Assert.Equal(EngineErrors.TypeMismatch, expand.Error.Code);
            Assert.Contains("cannot expand", expand.Error.Message, StringComparison.Ordinal);

            // 下标作用在非数组上 → 如实报错
            var index = await Assert.ThrowsAsync<EngineException>(() =>
                engine.Batch!.RunAsync(ProbeScript(new { bad = "{seed.echo.items[0].id[0]}" })));
            Assert.Equal(EngineErrors.TypeMismatch, index.Error.Code);
            Assert.Contains("cannot index", index.Error.Message, StringComparison.Ordinal);

            // 映射路径缺字段（元素级错误如实报错，不留空洞）
            var missing = await Assert.ThrowsAsync<EngineException>(() =>
                engine.Batch!.RunAsync(ProbeScript(new { bad = "{seed.echo.tags[*].id}" })));
            Assert.Equal(EngineErrors.TypeMismatch, missing.Error.Code);
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 步骤上限_超限在校验阶段拒绝_边界值放行()
    {
        var (engine, path) = Create();
        try
        {
            var limits = new EngineLimits();
            BatchStep Step(int i) => new($"s{i}", "test.echo", JsonSerializer.Deserialize<JsonElement>("{}"));

            var tooMany = new BatchScript("超限批", Enumerable.Range(0, limits.MaxBatchSteps + 1).Select(Step).ToArray());
            var ex = await Assert.ThrowsAsync<EngineException>(() => engine.Batch!.RunAsync(tooMany));
            Assert.Equal(EngineErrors.EnumOutOfRange, ex.Error.Code);

            var atLimit = new BatchScript("边界批", Enumerable.Range(0, limits.MaxBatchSteps).Select(Step).ToArray());
            var report = await engine.Batch!.RunAsync(atLimit);
            Assert.True(report.Ok);
            Assert.Equal(limits.MaxBatchSteps, report.Steps.Count);
        }
        finally { TestEnv.Cleanup(path); }
    }

    [Fact]
    public async Task 宏_保存与运行都过同一道校验()
    {
        var (engine, path) = Create();
        try
        {
            var empty = JsonSerializer.SerializeToElement(new BatchScript("空宏", []));
            var bad = await Assert.ThrowsAsync<EngineException>(() =>
                engine.ExecuteAsync<JsonElement>("macro.save", new { name = "空宏", script = empty }));
            Assert.Equal(EngineErrors.RequiredParam, bad.Error.Code);

            var steps = Enumerable.Range(0, 501)
                .Select(i => new BatchStep($"s{i}", "test.echo", JsonSerializer.Deserialize<JsonElement>("{}")))
                .ToArray();
            var tooMany = JsonSerializer.SerializeToElement(new BatchScript("超限宏", steps));
            var ex = await Assert.ThrowsAsync<EngineException>(() =>
                engine.ExecuteAsync<JsonElement>("macro.save", new { name = "超限宏", script = tooMany }));
            Assert.Equal(EngineErrors.EnumOutOfRange, ex.Error.Code);
        }
        finally { TestEnv.Cleanup(path); }
    }
}
