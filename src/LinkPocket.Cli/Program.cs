using System.Text;
using LinkPocket.Cli;
using LinkPocket.Contracts;

// 控制台入口：把一次命令行交给 LinkPocket.Cli.CliRunner（该执行器同时被外部 Agent 网关进程内复用）。
// 输出全部是英文技术文案（仓库口径：机器面固定英文，中文只进 I18n 字符串表与注释）。
//
// ⚠️ 调用方身份 = **外部进程**（CallerRef.ExternalAgent，不带会话）：两点都不是可选项——
// ① 审计要如实回答"这条记录是别的进程干的"（界面自己的写是 ui，应用内助手是带会话的 agent）；
// ② WPF 宿主的跨进程变更流按该身份过滤（否则命令行改完数据它认不出来，精确失效与提示全失效）。

if (OperatingSystem.IsWindows())
{
    try { Console.OutputEncoding = Encoding.UTF8; }
    catch (IOException) { /* 输出被重定向时不支持改编码，沿用宿主默认 */ }
}

return await CliRunner.RunAsync(args, Console.Out, Console.Error, caller: CallerRef.ExternalAgent);
