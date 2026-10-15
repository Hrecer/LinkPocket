using System.IO.Compression;
using LinkPocket.Update;
using Xunit;

namespace LinkPocket;

public class DirectUpdateTests
{
    [Fact]
    public void 甲路径全流程_数据逐字节保真并剔除运行痕迹()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            string dbPath = "";
            var install = UpdateTestKit.CreateInstall(root, data =>
            {
                dbPath = UpdateTestKit.CreateDb(Path.Combine(data, "linkpocket.db"), 9);
                UpdateTestKit.WriteFile(data, "ui-preferences.json", "{\"Version\":1}");
                UpdateTestKit.WriteFile(data, "macros.json", "{\"version\":1,\"macros\":[]}");
                UpdateTestKit.WriteFile(data, "ai/preferences.json", "{\"x\":1}");
                UpdateTestKit.WriteFile(data, "ai/usage.json", "{}");
                UpdateTestKit.WriteFile(data, "linkpocket-ops.db", "ops");
                UpdateTestKit.WriteFile(data, "undo-journal.json", "u");
                UpdateTestKit.WriteFile(data, "favicons/a.png", "png");
            });
            var dbBytes = File.ReadAllBytes(dbPath);

            var package = UpdateTestKit.CreatePackage(root);
            var report = UpdateTestKit.CreateReport(root);
            var (runner, launched, _, stages) = UpdateTestKit.CreateRunner(Path.Combine(root, "staging"));

            runner.Run(new UpdateArgs(package, install, null), report);

            Assert.True(report.Ok);
            Assert.Equal("direct", report.Mode);
            Assert.Contains("convert", stages);

            // 新装内容；跳过清单兜住更新器与 update\
            Assert.Equal("new-app", File.ReadAllText(Path.Combine(install, "LinkPocket.exe")));
            Assert.True(File.Exists(Path.Combine(install, "docs", "readme.txt")));
            Assert.False(File.Exists(Path.Combine(install, "LinkPocket-Update.exe")));
            Assert.False(Directory.Exists(Path.Combine(install, "update")));

            // 数据逐字节保真（库一个字节没动）
            Assert.Equal(dbBytes, File.ReadAllBytes(Path.Combine(install, "data", "linkpocket.db")));
            Assert.Equal("{\"Version\":1}", File.ReadAllText(Path.Combine(install, "data", "ui-preferences.json")));
            Assert.True(File.Exists(Path.Combine(install, "data", "macros.json")));
            Assert.True(File.Exists(Path.Combine(install, "data", "ai", "preferences.json")));

            // 图标缓存 = 用户数据：随数据区携带、不剔除（4.0.4 起——曾剔除致"更新后图标全灭"）
            Assert.Equal("png", File.ReadAllText(Path.Combine(install, "data", "favicons", "a.png")));

            // 运行痕迹已剔除（§0 清单）
            Assert.False(File.Exists(Path.Combine(install, "data", "ai", "usage.json")));
            Assert.False(File.Exists(Path.Combine(install, "data", "linkpocket-ops.db")));
            Assert.False(File.Exists(Path.Combine(install, "data", "undo-journal.json")));

            // 数据备份：安装目录旁生成数据 zip（更新流程不回收；用户确认后自删）
            var backup = Assert.Single(Directory.GetFiles(root, "*.data-backup-*.zip"));
            using (var archive = ZipFile.OpenRead(backup))
            {
                Assert.Contains(archive.Entries, e => e.FullName == "linkpocket.db");
                Assert.Contains(archive.Entries, e => e.FullName == "favicons/a.png");
            }

            // 无残留；已启动新版
            Assert.Empty(Directory.GetDirectories(root, "*.old-*"));
            Assert.Empty(Directory.GetDirectories(root, "data.pending-*"));
            Assert.Single(launched);
            Assert.Equal(Path.Combine(install, "LinkPocket.exe"), launched[0]);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 失败在安装阶段_回滚到原样()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = UpdateTestKit.CreateInstall(root, data =>
            {
                UpdateTestKit.CreateDb(Path.Combine(data, "linkpocket.db"), 9);
                UpdateTestKit.WriteFile(data, "ui-preferences.json", "{\"Version\":1}");
            });
            var package = UpdateTestKit.CreatePackage(root);
            var report = UpdateTestKit.CreateReport(root);
            var (runner, launched, _, _) = UpdateTestKit.CreateRunner(Path.Combine(root, "staging"), failOnStage: "install");

            Assert.Throws<InvalidOperationException>(() => runner.Run(new UpdateArgs(package, install, null), report));

            Assert.Equal("old-app", File.ReadAllText(Path.Combine(install, "LinkPocket.exe")));
            Assert.True(File.Exists(Path.Combine(install, "data", "linkpocket.db")));
            Assert.Equal("{\"Version\":1}", File.ReadAllText(Path.Combine(install, "data", "ui-preferences.json")));
            Assert.Empty(Directory.GetDirectories(root, "*.old-*"));
            Assert.Empty(Directory.GetDirectories(root, "data.pending-*"));
            Assert.Empty(launched);

            // 数据备份在"动任何东西"之前完成——失败回滚也不回收它（用户确认后自删）
            Assert.Single(Directory.GetFiles(root, "*.data-backup-*.zip"));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 失败在转换阶段_完整反向回滚()
    {
        // convert 在"数据已回位"之后失败 → 回滚要走最长反向路径：数据先挪出、删新装、旧目录回位、数据再回位
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = UpdateTestKit.CreateInstall(root, data =>
            {
                UpdateTestKit.CreateDb(Path.Combine(data, "linkpocket.db"), 9);
                UpdateTestKit.WriteFile(data, "ui-preferences.json", "{\"Version\":1}");
            });
            var package = UpdateTestKit.CreatePackage(root);
            var report = UpdateTestKit.CreateReport(root);
            var (runner, launched, _, _) = UpdateTestKit.CreateRunner(Path.Combine(root, "staging"), failOnStage: "convert");

            Assert.Throws<InvalidOperationException>(() => runner.Run(new UpdateArgs(package, install, null), report));

            Assert.Equal("old-app", File.ReadAllText(Path.Combine(install, "LinkPocket.exe")));
            Assert.True(File.Exists(Path.Combine(install, "data", "linkpocket.db")));
            Assert.Equal("{\"Version\":1}", File.ReadAllText(Path.Combine(install, "data", "ui-preferences.json")));
            Assert.Empty(Directory.GetDirectories(root, "*.old-*"));
            Assert.Empty(Directory.GetDirectories(root, "data.pending-*"));
            Assert.Empty(launched);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 跳过清单生效()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = UpdateTestKit.CreateInstall(root);
            var package = UpdateTestKit.CreatePackage(root, pkg =>
            {
                UpdateTestKit.WriteFile(pkg, "extra.bin", "x");
                UpdateTestKit.WriteFile(pkg, "update/skip-install.txt", "extra.bin\n");
            });
            var (runner, _, _, _) = UpdateTestKit.CreateRunner(Path.Combine(root, "staging"));

            runner.Run(new UpdateArgs(package, install, null), UpdateTestKit.CreateReport(root));

            Assert.False(File.Exists(Path.Combine(install, "extra.bin")));
            Assert.True(File.Exists(Path.Combine(install, "docs", "readme.txt")));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 有转换步但版本读不出_拒绝且零副作用()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = UpdateTestKit.CreateInstall(root, data =>
            {
                UpdateTestKit.CreateDb(Path.Combine(data, "linkpocket.db"), 9);
                UpdateTestKit.WriteFile(data, "ui-preferences.json", "{\"Version\":1}");
            });
            var package = UpdateTestKit.CreatePackage(root, pkg =>
                UpdateTestKit.WriteFile(pkg, "update/steps/4.0.0-4.1.0.json",
                    "{\"from\":\"4.0.0\",\"to\":\"4.1.0\",\"changes\":[{\"file\":\"ui-preferences.json\",\"ops\":[{\"op\":\"remove\",\"path\":\"x\"}]}]}"));
            var report = UpdateTestKit.CreateReport(root);
            var (runner, launched, _, _) = UpdateTestKit.CreateRunner(Path.Combine(root, "staging"));

            var ex = Assert.Throws<UpdateFailure>(() => runner.Run(new UpdateArgs(package, install, null), report));
            Assert.Contains("could not be determined", ex.Message);

            // 零副作用：旧装原样、数据原样、无残留、未启动
            Assert.Equal("old-app", File.ReadAllText(Path.Combine(install, "LinkPocket.exe")));
            Assert.Equal("{\"Version\":1}", File.ReadAllText(Path.Combine(install, "data", "ui-preferences.json")));
            Assert.Empty(Directory.GetDirectories(root, "*.old-*"));
            Assert.Empty(Directory.GetDirectories(root, "data.pending-*"));
            Assert.Empty(launched);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }
}
