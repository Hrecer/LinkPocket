using ProtocolSmoke;

// LinkPocket 协议冒烟测试（含 CI 性能门槛）
// 运行：dotnet run --project tests/ProtocolSmoke                       （Debug，门槛放宽 ×5）
//       dotnet run --project tests/ProtocolSmoke -c Release -- --strict-perf   （CI 口径，严格门槛）
// 退出码：0 = 全部通过；非 0 = 断言失败（含性能门槛超标）。
// 报告：运行结束落盘 perf_report.json（工作目录；可用 LP_PERF_REPORT 指定路径）。
// 面向 = 引擎（EngineClient 强类型面 + EngineWire JSON-RPC 面），
// 断言由附录 B 行为等价表导出（引擎侧可断言项）+ 并发压测 + 10k 性能门槛 + 缓存与增量失效。
// 独立协议层（LinkPocket.Infrastructure/ILinkPocketApi/Transport 系）已整体删除，不在本测试范围。
// 测试库 = 进程内临时库（SchemaMigrator 建库），不污染正式数据。

var strictPerf = args.Contains("--strict-perf", StringComparer.Ordinal)
    || Environment.GetEnvironmentVariable("LP_PERF_STRICT") == "1";
if (strictPerf)
{
    PerfReport.Instance.Strict = true;
    PerfReport.Instance.Relaxation = 1.0;   // 严格模式 = 按原始门槛判定
}

try
{
    await SmokeRunner.RunAsync();
}
finally
{
    var path = PerfReport.Instance.Write();
    var samples = PerfReport.Instance.Samples;
    if (samples.Count > 0)
    {
        Console.WriteLine($"性能门槛：{samples.Count(s => s.Ok)}/{samples.Count} 达标" +
                          $"（严格模式={(PerfReport.Instance.Strict ? "开" : "关")}，放宽 ×{PerfReport.Instance.Relaxation}）" +
                          (path is null ? "；报告落盘失败" : $"；报告 {path}"));
    }
}
