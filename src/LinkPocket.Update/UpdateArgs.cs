namespace LinkPocket.Update;

/// <summary>更新器命令行参数：<c>--package</c> / <c>--install</c> 必填；<c>--wait-pid</c> 可选（先等该进程退出）。</summary>
public sealed record UpdateArgs(string PackagePath, string InstallPath, int? WaitPid)
{
    public const string Usage =
        """
        LinkPocket updater - applies an extracted package onto an existing install.

        Usage:
          LinkPocket-Update.exe --package <package dir> --install <install dir> [--wait-pid <pid>]

        Options:
          --package    directory holding the new version (LinkPocket.exe + update\ ...)
          --install    directory of the current installation (must contain LinkPocket.exe)
          --wait-pid   wait for this process to exit first (up to 120 s)

        Exit codes: 0 = success, 1 = failure, 2 = usage error.
        """;

    public static UpdateArgs Parse(string[] args)
    {
        string? package = null, install = null;
        int? waitPid = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--package":
                    package = Next(args, ref i, "--package");
                    break;
                case "--install":
                    install = Next(args, ref i, "--install");
                    break;
                case "--wait-pid":
                    var raw = Next(args, ref i, "--wait-pid");
                    if (!int.TryParse(raw, out var pid) || pid <= 0)
                        throw new UpdateUsageException($"--wait-pid expects a positive process id, got '{raw}'");
                    waitPid = pid;
                    break;
                default:
                    throw new UpdateUsageException($"unknown argument: {args[i]}");
            }
        }
        if (string.IsNullOrWhiteSpace(package)) throw new UpdateUsageException("--package is required");
        if (string.IsNullOrWhiteSpace(install)) throw new UpdateUsageException("--install is required");
        return new UpdateArgs(package, install, waitPid);
    }

    private static string Next(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length) throw new UpdateUsageException($"{flag} expects a value");
        return args[++index];
    }
}

/// <summary>用法错误（退出码 2；消息面向用户）。</summary>
public sealed class UpdateUsageException(string message) : Exception(message);
