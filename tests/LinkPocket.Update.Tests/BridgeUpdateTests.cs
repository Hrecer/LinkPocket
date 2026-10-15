using LinkPocket.Update;
using Xunit;

namespace LinkPocket;

public class BridgeUpdateTests
{
    /// <summary>假 CLI：导写出备份文件 + 报告计数；导入写一个"新库" + 报告计数。</summary>
    private static FakeCli SuccessCli(int importedFolders = 2, int importedLinks = 3)
    {
        return new FakeCli
        {
            Handler = (_, args) =>
            {
                if (args.Contains("export"))
                {
                    var output = args[Array.IndexOf(args, "--output_path") + 1];
                    File.WriteAllText(output, "fake-backup-bytes");
                    return new CliResult(0, "{\"ok\":true,\"data\":{\"total_folders\":2,\"total_links\":3}}", "");
                }

                if (args.Contains("import"))
                {
                    var db = args[Array.IndexOf(args, "--db") + 1];
                    Directory.CreateDirectory(Path.GetDirectoryName(db)!);
                    File.WriteAllText(db, "fresh-db");
                    var json = "{\"ok\":true,\"data\":{\"folders_created\":" + importedFolders
                               + ",\"links_created\":" + importedLinks + "}}";
                    return new CliResult(0, json, "");
                }

                return new CliResult(0, "{}", "");
            },
        };
    }

    private static string CreateBridgeInstall(string root)
    {
        var install = UpdateTestKit.CreateInstall(root, data =>
        {
            UpdateTestKit.CreateDb(Path.Combine(data, "linkpocket.db"), 8);
            UpdateTestKit.WriteFile(data, "ui-preferences.json", "{\"Version\":1}");
            UpdateTestKit.WriteFile(data, "update-preferences.json", "{\"version\":1,\"auto_check_on_startup\":false}");
            UpdateTestKit.WriteFile(data, "macros.json", "{\"version\":1,\"macros\":[]}");
            UpdateTestKit.WriteFile(data, "ai/preferences.json", "{\"x\":1}");
            UpdateTestKit.WriteFile(data, "ai/usage.json", "{}");
            UpdateTestKit.WriteFile(data, "fonts/f.ttf", "font");
            UpdateTestKit.WriteFile(data, "favicons/i.png", "icon");
        });
        UpdateTestKit.WriteFile(install, "tools/linkpocket.exe", "old-cli");
        return install;
    }

    [Fact]
    public void 乙路径全流程_先导出_设置就位_计数对账()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = CreateBridgeInstall(root);
            var package = UpdateTestKit.CreatePackage(root,
                pkg => UpdateTestKit.WriteFile(pkg, "tools/linkpocket.exe", "new-cli"));

            var report = UpdateTestKit.CreateReport(root);
            var staging = Path.Combine(root, "staging");
            var ctx = UpdateTestKit.CreateContext(root, install, package, report, staging);
            var cli = SuccessCli();
            var launched = new List<string>();

            BridgeUpdate.Execute(ctx, cli, launched.Add, _ => { }, _ => { });

            // 两次 CLI 调用：旧版导出 → 新版导入（均走安装目录里的 CLI；导入即"新装的 CLI"）
            Assert.Equal(2, cli.Calls.Count);
            Assert.Equal(Path.Combine(install, "tools", "linkpocket.exe"), cli.Calls[0].Exe);
            Assert.Contains("export", cli.Calls[0].Args);
            Assert.Equal(Path.Combine(install, "data", "linkpocket.db"), cli.Calls[0].Args[Array.IndexOf(cli.Calls[0].Args, "--db") + 1]);
            Assert.Equal(Path.Combine(install, "tools", "linkpocket.exe"), cli.Calls[1].Exe);
            Assert.Contains("import", cli.Calls[1].Args);
            Assert.Contains("--yes", cli.Calls[1].Args);

            // 计数对账进报告
            Assert.Equal(2, report.Counts["exported_folders"]);
            Assert.Equal(3, report.Counts["exported_links"]);
            Assert.Equal(2, report.Counts["imported_folders"]);
            Assert.Equal(3, report.Counts["imported_links"]);

            // 新装 + 数据构成：导入的新库 + 携带集就位 + 用量不带
            Assert.Equal("new-app", File.ReadAllText(Path.Combine(install, "LinkPocket.exe")));
            Assert.Equal("fresh-db", File.ReadAllText(Path.Combine(install, "data", "linkpocket.db")));
            Assert.True(File.Exists(Path.Combine(install, "data", "ui-preferences.json")));
            Assert.True(File.Exists(Path.Combine(install, "data", "update-preferences.json")));
            Assert.True(File.Exists(Path.Combine(install, "data", "macros.json")));
            Assert.True(File.Exists(Path.Combine(install, "data", "ai", "preferences.json")));
            Assert.True(File.Exists(Path.Combine(install, "data", "fonts", "f.ttf")));
            Assert.True(File.Exists(Path.Combine(install, "data", "favicons", "i.png")));
            Assert.False(File.Exists(Path.Combine(install, "data", "ai", "usage.json")));

            // 数据备份：安装目录旁生成数据 zip（先于一切；更新流程不回收）
            Assert.Single(Directory.GetFiles(root, "*.data-backup-*.zip"));

            // 暂存区保留导出件（等新版首启清扫）；旧目录已删；已启动
            Assert.Equal("fake-backup-bytes", File.ReadAllText(Path.Combine(staging, "bridge", "bookmarks.lpbackup")));
            Assert.Empty(Directory.GetDirectories(root, "*.old-*"));
            Assert.Single(launched);
            Assert.Equal(Path.Combine(install, "LinkPocket.exe"), launched[0]);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 计数对账不符_回滚且保留暂存区()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = CreateBridgeInstall(root);
            var package = UpdateTestKit.CreatePackage(root,
                pkg => UpdateTestKit.WriteFile(pkg, "tools/linkpocket.exe", "new-cli"));

            var report = UpdateTestKit.CreateReport(root);
            var staging = Path.Combine(root, "staging");
            var ctx = UpdateTestKit.CreateContext(root, install, package, report, staging);
            var cli = SuccessCli(importedFolders: 1);   // 导出一致是 2 → 对不上
            var launched = new List<string>();

            var ex = Assert.Throws<UpdateFailure>(() => BridgeUpdate.Execute(ctx, cli, launched.Add, _ => { }, _ => { }));
            Assert.Contains("count mismatch", ex.Message);

            // 回滚：旧装原样；暂存区保留（里面有导出件）；未启动
            Assert.Equal("old-app", File.ReadAllText(Path.Combine(install, "LinkPocket.exe")));
            Assert.True(File.Exists(Path.Combine(staging, "bridge", "bookmarks.lpbackup")));
            Assert.Empty(Directory.GetDirectories(root, "*.old-*"));
            Assert.Empty(launched);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 导出失败_未动任何东西()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = CreateBridgeInstall(root);
            var package = UpdateTestKit.CreatePackage(root,
                pkg => UpdateTestKit.WriteFile(pkg, "tools/linkpocket.exe", "new-cli"));

            var report = UpdateTestKit.CreateReport(root);
            var ctx = UpdateTestKit.CreateContext(root, install, package, report, Path.Combine(root, "staging"));
            var cli = new FakeCli { Handler = (_, _) => new CliResult(1, "", "boom") };
            var launched = new List<string>();

            var ex = Assert.Throws<UpdateFailure>(() => BridgeUpdate.Execute(ctx, cli, launched.Add, _ => { }, _ => { }));
            Assert.Contains("boom", ex.Message);
            Assert.Single(cli.Calls);

            Assert.Equal("old-app", File.ReadAllText(Path.Combine(install, "LinkPocket.exe")));
            Assert.Empty(Directory.GetDirectories(root, "*.old-*"));
            Assert.Empty(launched);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }
}
