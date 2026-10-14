using System.Diagnostics;

namespace LinkPocket.Update;

/// <summary>一次 CLI 调用的结果（更新器只看：退出码 + 两路输出）。</summary>
public sealed record CliResult(int ExitCode, string StdOut, string StdErr);

/// <summary>外部 CLI 调用（乙路径：旧版导出 / 新版导入）——测试用假实现替换，生产走 <see cref="ProcessCliRunner"/>。</summary>
public interface ICliRunner
{
    CliResult Run(string exePath, IReadOnlyList<string> args);
}

/// <summary>唯一实现：起进程、收两路输出（stderr 异步读，避免大输出死锁）。</summary>
internal sealed class ProcessCliRunner : ICliRunner
{
    public CliResult Run(string exePath, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new UpdateFailure($"failed to start: {exePath}");
        var stdErrTask = process.StandardError.ReadToEndAsync();
        var stdOut = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, stdOut, stdErrTask.GetAwaiter().GetResult());
    }
}
