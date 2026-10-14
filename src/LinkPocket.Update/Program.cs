using System.Diagnostics;

namespace LinkPocket.Update;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            Console.WriteLine(UpdateArgs.Usage);
            return 0;
        }

        UpdateArgs parsed;
        try
        {
            parsed = UpdateArgs.Parse(args);
        }
        catch (UpdateUsageException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(UpdateArgs.Usage);
            return 2;
        }

        // 运行痕迹写在更新器自身目录（= 暂存区；正常流程由提取方保证）。
        var directory = AppContext.BaseDirectory;
        var report = new UpdateReport(Path.Combine(directory, "update.log"));
        void Emit(string line)
        {
            report.Stage(line);
            Console.WriteLine(line);
        }

        try
        {
            var runner = new UpdateRunner(new ProcessCliRunner(), LaunchNewVersion, Emit, directory);
            runner.Run(parsed, report);
            return 0;
        }
        catch (Exception ex)
        {
            Emit("update failed: " + ex.Message);
            report.Fail(ex.Message);
            return 1;
        }
        finally
        {
            report.WriteResultJson(Path.Combine(directory, "update-result.json"));
        }

        void LaunchNewVersion(string exePath)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = true,
            });
        }
    }
}
