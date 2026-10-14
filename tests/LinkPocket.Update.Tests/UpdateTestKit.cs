using LinkPocket.Update;

namespace LinkPocket;

/// <summary>更新器单测的公共夹具：临时树构造 + 假 CLI + 失败注入点的 runner 工厂。</summary>
internal static class UpdateTestKit
{
    public static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lp-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // 尽力而为（临时目录）
        }
    }

    public static string WriteFile(string directory, string relative, string content)
    {
        var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>造一个真的 SQLite 库（含纪元戳）——更新器的纪元读取走真读路径。</summary>
    public static string CreateDb(string path, int userVersion)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE t(x INTEGER); PRAGMA user_version = {userVersion};";
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return path;
    }

    /// <summary>最小"安装目录"：LinkPocket.exe 占位 + data\（内容由调用方给）。</summary>
    public static string CreateInstall(string root, Action<string>? data = null)
    {
        var install = Path.Combine(root, "LinkPocket");
        Directory.CreateDirectory(install);
        WriteFile(install, "LinkPocket.exe", "old-app");
        var dataDir = Path.Combine(install, "data");
        Directory.CreateDirectory(dataDir);
        data?.Invoke(dataDir);
        return install;
    }

    /// <summary>最小"包目录"：LinkPocket.exe + LinkPocket-Update.exe + docs\ + update\skip-install.txt。</summary>
    public static string CreatePackage(string root, Action<string>? extra = null)
    {
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(package);
        WriteFile(package, "LinkPocket.exe", "new-app");
        WriteFile(package, "LinkPocket-Update.exe", "updater-binary");
        WriteFile(package, "docs/readme.txt", "docs");
        WriteFile(package, "update/skip-install.txt", "");
        extra?.Invoke(package);
        return package;
    }

    public static UpdateReport CreateReport(string root)
        => new(Path.Combine(root, "update.log"));

    public static UpdateContext CreateContext(string root, string install, string package, UpdateReport report, string? stagingDir = null)
        => new()
        {
            Report = report,
            StagingDir = stagingDir ?? Path.Combine(root, "staging"),
            PackageDir = package,
            InstallDir = install,
            ParentDir = root,
            InstallName = Path.GetFileName(install),
        };

    /// <summary>runner + 记录启动/输出/阶段；<paramref name="failOnStage"/> 在指定阶段前抛错（测回滚）。</summary>
    public static (UpdateRunner Runner, List<string> Launched, List<string> Lines, List<string> Stages) CreateRunner(
        string stagingDir, ICliRunner? cli = null, string? failOnStage = null)
    {
        var launched = new List<string>();
        var lines = new List<string>();
        var stages = new List<string>();
        var runner = new UpdateRunner(
            cli ?? new FakeCli(),
            path => launched.Add(path),
            lines.Add,
            stagingDir,
            stage =>
            {
                stages.Add(stage);
                if (failOnStage is not null && stage == failOnStage)
                    throw new InvalidOperationException($"test-injected failure at stage '{stage}'");
            });
        return (runner, launched, lines, stages);
    }
}

/// <summary>假 CLI：记录每次调用；<see cref="Handler"/> 缺省返回空成功。</summary>
internal sealed class FakeCli : ICliRunner
{
    public List<(string Exe, string[] Args)> Calls { get; } = [];

    public Func<string, string[], CliResult>? Handler { get; set; }

    public CliResult Run(string exePath, IReadOnlyList<string> args)
    {
        var argArray = args.ToArray();
        Calls.Add((exePath, argArray));
        return Handler?.Invoke(exePath, argArray) ?? new CliResult(0, "{}", "");
    }
}
