using LinkPocket.Update;
using Xunit;

namespace LinkPocket;

public class PreflightTests
{
    [Fact]
    public void 无库走甲路径()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = UpdateTestKit.CreateInstall(root);   // 空 data\
            var package = UpdateTestKit.CreatePackage(root);
            var ctx = UpdateTestKit.CreateContext(root, install, package, UpdateTestKit.CreateReport(root));

            var result = Preflight.Run(ctx, _ => { });

            Assert.Equal(UpdateMode.Direct, result.Mode);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 同纪元库走甲路径()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = UpdateTestKit.CreateInstall(root,
                data => UpdateTestKit.CreateDb(Path.Combine(data, "linkpocket.db"), 9));
            var package = UpdateTestKit.CreatePackage(root);
            var ctx = UpdateTestKit.CreateContext(root, install, package, UpdateTestKit.CreateReport(root));

            Assert.Equal(UpdateMode.Direct, Preflight.Run(ctx, _ => { }).Mode);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 更旧纪元的库走乙路径()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = UpdateTestKit.CreateInstall(root,
                data => UpdateTestKit.CreateDb(Path.Combine(data, "linkpocket.db"), 8));
            UpdateTestKit.WriteFile(install, "tools/linkpocket.exe", "old-cli");
            var package = UpdateTestKit.CreatePackage(root,
                pkg => UpdateTestKit.WriteFile(pkg, "tools/linkpocket.exe", "new-cli"));
            var ctx = UpdateTestKit.CreateContext(root, install, package, UpdateTestKit.CreateReport(root));

            Assert.Equal(UpdateMode.Bridge, Preflight.Run(ctx, _ => { }).Mode);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 更新纪元的库被拒绝_不支持降级()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = UpdateTestKit.CreateInstall(root,
                data => UpdateTestKit.CreateDb(Path.Combine(data, "linkpocket.db"), 10));
            var package = UpdateTestKit.CreatePackage(root);
            var ctx = UpdateTestKit.CreateContext(root, install, package, UpdateTestKit.CreateReport(root));

            var ex = Assert.Throws<UpdateFailure>(() => Preflight.Run(ctx, _ => { }));
            Assert.Contains("cannot downgrade", ex.Message);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 包含_data_目录被拒绝()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = UpdateTestKit.CreateInstall(root);
            var package = UpdateTestKit.CreatePackage(root, pkg => Directory.CreateDirectory(Path.Combine(pkg, "data")));
            var ctx = UpdateTestKit.CreateContext(root, install, package, UpdateTestKit.CreateReport(root));

            var ex = Assert.Throws<UpdateFailure>(() => Preflight.Run(ctx, _ => { }));
            Assert.Contains("must not contain a data", ex.Message);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 中断残留被拒绝并给恢复指引()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data.pending-20260101-000000"));
            var install = UpdateTestKit.CreateInstall(root);
            var package = UpdateTestKit.CreatePackage(root);
            var ctx = UpdateTestKit.CreateContext(root, install, package, UpdateTestKit.CreateReport(root));

            var ex = Assert.Throws<UpdateFailure>(() => Preflight.Run(ctx, _ => { }));
            Assert.Contains("interrupted", ex.Message);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 乙路径缺旧版_cli_被拒绝()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var install = UpdateTestKit.CreateInstall(root,
                data => UpdateTestKit.CreateDb(Path.Combine(data, "linkpocket.db"), 8));
            var package = UpdateTestKit.CreatePackage(root);
            var ctx = UpdateTestKit.CreateContext(root, install, package, UpdateTestKit.CreateReport(root));

            var ex = Assert.Throws<UpdateFailure>(() => Preflight.Run(ctx, _ => { }));
            Assert.Contains("old command-line client", ex.Message);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 包与安装目录相同被拒绝()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            // 手工造一个"既是安装又是包"的目录（无 data\，否则先撞"包不得含 data"）
            var same = Path.Combine(root, "same");
            Directory.CreateDirectory(same);
            UpdateTestKit.WriteFile(same, "LinkPocket.exe", "x");
            var ctx = UpdateTestKit.CreateContext(root, same, same, UpdateTestKit.CreateReport(root));

            var ex = Assert.Throws<UpdateFailure>(() => Preflight.Run(ctx, _ => { }));
            Assert.Contains("same directory", ex.Message);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }
}
