using System.Text;
using LinkPocket.Cli;

// 控制台入口：把一次命令行交给 LinkPocket.Cli.CliRunner（该执行器同时被外部 Agent 网关进程内复用）。
// 输出全部是英文技术文案（仓库口径：机器面固定英文，中文只进 I18n 字符串表与注释）。

if (OperatingSystem.IsWindows())
{
    try { Console.OutputEncoding = Encoding.UTF8; }
    catch (IOException) { /* 输出被重定向时不支持改编码，沿用宿主默认 */ }
}

return await CliRunner.RunAsync(args, Console.Out, Console.Error);
