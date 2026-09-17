using ProtocolSmoke;

// LinkPocket 协议冒烟测试（阶段 6 重写：引擎行为等价断言）
// 运行：dotnet run --project tests/ProtocolSmoke
// 面向 = 新引擎（EngineClient 强类型面 + EngineWire JSON-RPC 面），
// 断言由附录 B 行为等价表导出（引擎侧可断言项）+ 并发压测 + 10k 性能门槛（7.3）。
// 旧协议（LinkPocketApi/Dispatcher/TransportedLinkPocketApi）由 UI 现役使用，不在本测试范围。
// 测试库 = 进程内临时 v2 库（SchemaMigrator 建库），不污染正式数据。

await SmokeRunner.RunAsync();
