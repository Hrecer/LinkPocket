using LinkPocket.Update;
using Xunit;

namespace LinkPocket;

public class UpdateArgsTests
{
    [Fact]
    public void 解析全部参数()
    {
        var parsed = UpdateArgs.Parse(["--package", @"C:\pkg", "--install", @"C:\app", "--wait-pid", "42"]);
        Assert.Equal(@"C:\pkg", parsed.PackagePath);
        Assert.Equal(@"C:\app", parsed.InstallPath);
        Assert.Equal(42, parsed.WaitPid);
    }

    [Fact]
    public void 缺_package_报用法错误()
        => Assert.Throws<UpdateUsageException>(() => UpdateArgs.Parse(["--install", @"C:\app"]));

    [Fact]
    public void 缺_install_报用法错误()
        => Assert.Throws<UpdateUsageException>(() => UpdateArgs.Parse(["--package", @"C:\pkg"]));

    [Fact]
    public void 未知参数报用法错误()
        => Assert.Throws<UpdateUsageException>(() => UpdateArgs.Parse(["--package", "p", "--install", "i", "--frob"]) );

    [Fact]
    public void wait_pid_非法值报用法错误()
        => Assert.Throws<UpdateUsageException>(() => UpdateArgs.Parse(["--package", "p", "--install", "i", "--wait-pid", "zero"]));
}
