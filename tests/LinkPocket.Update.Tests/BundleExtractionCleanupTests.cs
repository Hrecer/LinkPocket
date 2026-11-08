using LinkPocket.Update;
using Xunit;

namespace LinkPocket;

public class BundleExtractionCleanupTests
{
    [Fact]
    public void 只清理LinkPocket旧解压目录_不动其他应用()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var baseDir = Path.Combine(root, "extract");
            var old1 = Path.Combine(baseDir, "LinkPocket", "hash-1");
            var old2 = Path.Combine(baseDir, "LinkPocket", "hash-2");
            var other = Path.Combine(baseDir, "OtherApp", "hash-1");
            UpdateTestKit.WriteFile(old1, "a.dll", "x");
            UpdateTestKit.WriteFile(old2, "b.dll", "x");
            UpdateTestKit.WriteFile(other, "c.dll", "x");

            var lines = new List<string>();
            BundleExtractionCleanup.Run(lines.Add, baseDir);

            Assert.False(Directory.Exists(old1));
            Assert.False(Directory.Exists(old2));
            Assert.False(Directory.Exists(Path.Combine(baseDir, "LinkPocket")));
            Assert.True(File.Exists(Path.Combine(other, "c.dll")));
            Assert.Contains(lines, line => line.Contains("removed 2, kept 0"));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 被占用的旧解压目录_保留并如实留痕()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var baseDir = Path.Combine(root, "extract");
            var lockedDir = Path.Combine(baseDir, "LinkPocket", "hash-locked");
            var openFile = UpdateTestKit.WriteFile(lockedDir, "in-use.dll", "x");

            var lines = new List<string>();
            using (File.Open(openFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                BundleExtractionCleanup.Run(lines.Add, baseDir);
            }

            Assert.True(Directory.Exists(lockedDir));
            Assert.Contains(lines, line => line.Contains("removed 0, kept 1"));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 解压目录不存在_不抛错()
    {
        var root = UpdateTestKit.NewRoot();
        try
        {
            var lines = new List<string>();
            BundleExtractionCleanup.Run(lines.Add, Path.Combine(root, "missing"));
            Assert.Contains(lines, line => line.Contains("nothing to clean"));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }
}
