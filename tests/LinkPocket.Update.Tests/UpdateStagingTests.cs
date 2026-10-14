using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket;

public class UpdateStagingTests
{
    [Fact]
    public void 安装标识_大小写与尾分隔符不敏感()
    {
        var a = UpdateStaging.InstallId(Path.Combine(Path.GetTempPath(), "LpCase", "App"));
        var b = UpdateStaging.InstallId(Path.Combine(Path.GetTempPath(), "lpcase", "app") + Path.DirectorySeparatorChar);
        var c = UpdateStaging.InstallId(Path.Combine(Path.GetTempPath(), "lpcase", "other"));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(12, a.Length);
    }

    [Fact]
    public void 清扫_保留结果文件_删除其余()
    {
        var installPath = Path.Combine(Path.GetTempPath(), "lp-sweep-" + Guid.NewGuid().ToString("N"));
        var stagingRoot = UpdateStaging.StagingRootFor(installPath);
        try
        {
            Directory.CreateDirectory(Path.Combine(stagingRoot, "bridge"));
            File.WriteAllText(Path.Combine(stagingRoot, UpdateStaging.ResultFileName), "{\"ok\":true}");
            File.WriteAllText(Path.Combine(stagingRoot, "LinkPocket-Update.exe"), "x");
            File.WriteAllText(Path.Combine(stagingRoot, "update.log"), "l");
            File.WriteAllText(Path.Combine(stagingRoot, "bridge", "bookmarks.lpbackup"), "b");

            UpdateStaging.SweepOwnStaging(installPath);

            Assert.True(File.Exists(Path.Combine(stagingRoot, UpdateStaging.ResultFileName)));
            Assert.False(File.Exists(Path.Combine(stagingRoot, "LinkPocket-Update.exe")));
            Assert.False(File.Exists(Path.Combine(stagingRoot, "update.log")));
            Assert.False(Directory.Exists(Path.Combine(stagingRoot, "bridge")));
        }
        finally
        {
            try { Directory.Delete(stagingRoot, recursive: true); } catch { /* 尽力而为 */ }
        }
    }

    [Fact]
    public void 结果读取与消费()
    {
        var installPath = Path.Combine(Path.GetTempPath(), "lp-result-" + Guid.NewGuid().ToString("N"));
        var stagingRoot = UpdateStaging.StagingRootFor(installPath);
        try
        {
            Directory.CreateDirectory(stagingRoot);
            File.WriteAllText(Path.Combine(stagingRoot, UpdateStaging.ResultFileName), "{\"ok\":true,\"mode\":\"direct\"}");

            Assert.Equal("{\"ok\":true,\"mode\":\"direct\"}", UpdateStaging.TryReadResultJson(installPath));

            UpdateStaging.ConsumeResult(installPath);
            Assert.Null(UpdateStaging.TryReadResultJson(installPath));
        }
        finally
        {
            try { Directory.Delete(stagingRoot, recursive: true); } catch { /* 尽力而为 */ }
        }
    }
}
