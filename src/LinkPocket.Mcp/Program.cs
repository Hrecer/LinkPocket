using LinkPocket.Mcp;

// 外部 Agent 网关：MCP over stdio。
// stdin 逐行读 JSON-RPC 2.0 消息，stdout 逐行写响应（日志走 stderr，不污染协议帧）。
// 每个工具调用都在进程内复用 LinkPocket.Cli 的命令行执行器——外部 Agent 与界面、内置助手是同一套命令。
return await McpServer.RunAsync(args);
