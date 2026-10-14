using System.Text.Json.Nodes;
using LinkPocket.Update;
using Xunit;

namespace LinkPocket;

public class SettingsChainTests
{
    private static (string Root, string Data, string Package) Setup(string? manifest = null, Action<string>? data = null)
    {
        var root = UpdateTestKit.NewRoot();
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);
        data?.Invoke(dataDir);
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(Path.Combine(package, "update"));
        if (manifest is not null) UpdateTestKit.WriteFile(package, "update/settings-manifest.json", manifest);
        return (root, dataDir, package);
    }

    private static void AddStep(string package, string name, string json)
        => UpdateTestKit.WriteFile(Path.Combine(package, "update", "steps"), name, json);

    private static UpdateReport Run(string root, string data, string package, string? from = "4.0.0", string? to = "4.1.0")
    {
        var report = UpdateTestKit.CreateReport(root);
        SettingsChain.Run(data, package, Path.Combine(root, "staging"), from, to, report, _ => { });
        return report;
    }

    [Fact]
    public void 步骤按序应用_版本对空档直通()
    {
        var (root, data, package) = Setup(data: d => UpdateTestKit.WriteFile(d, "f.json", "{\"version\":1,\"a\":7}"));
        try
        {
            AddStep(package, "4.0.0-4.1.0.json",
                "{\"from\":\"4.0.0\",\"to\":\"4.1.0\",\"changes\":[{\"file\":\"f.json\",\"expect_version\":1,\"set_version\":2,\"ops\":[{\"op\":\"rename\",\"from\":\"a\",\"to\":\"b\"}]}]}");
            AddStep(package, "4.2.0-4.3.0.json",
                "{\"from\":\"4.2.0\",\"to\":\"4.3.0\",\"changes\":[{\"file\":\"f.json\",\"expect_version\":2,\"set_version\":3,\"ops\":[{\"op\":\"rename\",\"from\":\"b\",\"to\":\"c\"}]}]}");

            Run(root, data, package, from: "4.0.0", to: "4.3.0");

            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "f.json")))!.AsObject();
            Assert.Equal(3, json["version"]!.GetValue<int>());
            Assert.Equal(7, json["c"]!.GetValue<int>());
            Assert.False(json.ContainsKey("a"));
            Assert.False(json.ContainsKey("b"));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 低于当前版本的步不应用()
    {
        var (root, data, package) = Setup(data: d => UpdateTestKit.WriteFile(d, "f.json", "{\"version\":1,\"a\":7}"));
        try
        {
            AddStep(package, "4.0.0-4.1.0.json",
                "{\"from\":\"4.0.0\",\"to\":\"4.1.0\",\"changes\":[{\"file\":\"f.json\",\"expect_version\":1,\"set_version\":2,\"ops\":[{\"op\":\"rename\",\"from\":\"a\",\"to\":\"b\"}]}]}");

            Run(root, data, package, from: "4.1.0", to: "4.2.0");

            Assert.Equal("{\"version\":1,\"a\":7}", File.ReadAllText(Path.Combine(data, "f.json")));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 版本不符_丢弃并记单()
    {
        var (root, data, package) = Setup(data: d => UpdateTestKit.WriteFile(d, "f.json", "{\"version\":3,\"a\":7}"));
        try
        {
            AddStep(package, "4.0.0-4.1.0.json",
                "{\"from\":\"4.0.0\",\"to\":\"4.1.0\",\"changes\":[{\"file\":\"f.json\",\"expect_version\":1,\"set_version\":2,\"ops\":[{\"op\":\"remove\",\"path\":\"a\"}]}]}");

            var report = Run(root, data, package);

            Assert.False(File.Exists(Path.Combine(data, "f.json")));
            Assert.Contains(report.Notes, note => note.Contains("expects"));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 目标非_json_对象_丢弃()
    {
        var (root, data, package) = Setup(data: d => UpdateTestKit.WriteFile(d, "f.json", "[1,2]"));
        try
        {
            AddStep(package, "4.0.0-4.1.0.json",
                "{\"from\":\"4.0.0\",\"to\":\"4.1.0\",\"changes\":[{\"file\":\"f.json\",\"ops\":[{\"op\":\"remove\",\"path\":\"a\"}]}]}");

            var report = Run(root, data, package);

            Assert.False(File.Exists(Path.Combine(data, "f.json")));
            Assert.Contains(report.Notes, note => note.Contains("not a JSON object"));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 坏步骤_整包拒绝且未动文件()
    {
        var (root, data, package) = Setup(data: d => UpdateTestKit.WriteFile(d, "f.json", "{\"version\":1,\"a\":7}"));
        try
        {
            AddStep(package, "4.0.0-4.1.0.json", "{oops");

            Assert.Throws<UpdateFailure>(() => Run(root, data, package));

            Assert.Equal("{\"version\":1,\"a\":7}", File.ReadAllText(Path.Combine(data, "f.json")));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 未知_op_拒绝()
    {
        var (root, data, package) = Setup(data: d => UpdateTestKit.WriteFile(d, "f.json", "{\"version\":1}"));
        try
        {
            AddStep(package, "4.0.0-4.1.0.json",
                "{\"from\":\"4.0.0\",\"to\":\"4.1.0\",\"changes\":[{\"file\":\"f.json\",\"ops\":[{\"op\":\"frobnicate\"}]}]}");

            var ex = Assert.Throws<UpdateFailure>(() => Run(root, data, package));
            Assert.Contains("unknown op", ex.Message);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 重复版本对_拒绝()
    {
        var (root, data, package) = Setup(data: d => UpdateTestKit.WriteFile(d, "f.json", "{\"version\":1}"));
        try
        {
            var step = "{\"from\":\"4.0.0\",\"to\":\"4.1.0\",\"changes\":[{\"file\":\"f.json\",\"ops\":[{\"op\":\"remove\",\"path\":\"a\"}]}]}";
            AddStep(package, "a.json", step);
            AddStep(package, "b.json", step);

            var ex = Assert.Throws<UpdateFailure>(() => Run(root, data, package));
            Assert.Contains("duplicate", ex.Message);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 清单对账_版本不符丢弃()
    {
        var (root, data, package) = Setup(
            manifest: "{\"version\":1,\"stores\":[{\"file\":\"s.json\",\"version\":2}]}",
            data: d => UpdateTestKit.WriteFile(d, "s.json", "{\"version\":1}"));
        try
        {
            var report = Run(root, data, package);

            Assert.False(File.Exists(Path.Combine(data, "s.json")));
            Assert.Contains(report.Notes, note => note.Contains("expects 2"));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 清单对账_版本相符原样携带()
    {
        var (root, data, package) = Setup(
            manifest: "{\"version\":1,\"stores\":[{\"file\":\"s.json\",\"version\":2}]}",
            data: d => UpdateTestKit.WriteFile(d, "s.json", "{\"version\":2,\"x\":1}"));
        try
        {
            Run(root, data, package);

            Assert.Equal("{\"version\":2,\"x\":1}", File.ReadAllText(Path.Combine(data, "s.json")));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 清单缺版本字段_如实记录并携带()
    {
        var (root, data, package) = Setup(
            manifest: "{\"version\":1,\"stores\":[{\"file\":\"s.json\",\"version\":2}]}",
            data: d => UpdateTestKit.WriteFile(d, "s.json", "{\"x\":1}"));
        try
        {
            var report = Run(root, data, package);

            Assert.True(File.Exists(Path.Combine(data, "s.json")));
            Assert.Contains(report.Notes, note => note.Contains("no version"));
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 坏清单_拒绝()
    {
        var (root, data, package) = Setup(
            manifest: "not-json",
            data: d => UpdateTestKit.WriteFile(d, "s.json", "{\"version\":1}"));
        try
        {
            var ex = Assert.Throws<UpdateFailure>(() => Run(root, data, package));
            Assert.Contains("manifest", ex.Message);
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void set_default_不覆盖已有值()
    {
        var (root, data, package) = Setup(data: d =>
            UpdateTestKit.WriteFile(d, "f.json", "{\"version\":1,\"language\":{\"mode\":\"fixed\"}}"));
        try
        {
            AddStep(package, "4.0.0-4.1.0.json",
                "{\"from\":\"4.0.0\",\"to\":\"4.1.0\",\"changes\":[{\"file\":\"f.json\",\"ops\":[{\"op\":\"set_default\",\"path\":\"language.mode\",\"value\":\"auto\"}]}]}");

            Run(root, data, package);

            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "f.json")))!.AsObject();
            Assert.Equal("fixed", json["language"]!["mode"]!.GetValue<string>());
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void set_default_父存在时才写入()
    {
        var (root, data, package) = Setup(data: d =>
            UpdateTestKit.WriteFile(d, "f.json", "{\"version\":1,\"language\":{}}"));
        try
        {
            AddStep(package, "4.0.0-4.1.0.json",
                "{\"from\":\"4.0.0\",\"to\":\"4.1.0\",\"changes\":[{\"file\":\"f.json\",\"ops\":[{\"op\":\"set_default\",\"path\":\"language.mode\",\"value\":\"auto\"}]}]}");

            Run(root, data, package);

            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "f.json")))!.AsObject();
            Assert.Equal("auto", json["language"]!["mode"]!.GetValue<string>());
        }
        finally { UpdateTestKit.Cleanup(root); }
    }

    [Fact]
    public void 中途失败_恢复快照()
    {
        // f1 可写、f2 只读 → 第二个 change 的原子替换失败 → 快照恢复必须把 f1 还原到转换前
        var (root, data, package) = Setup(data: d =>
        {
            UpdateTestKit.WriteFile(d, "f1.json", "{\"version\":1,\"a\":1}");
            var f2 = UpdateTestKit.WriteFile(d, "f2.json", "{\"version\":1,\"a\":1}");
            File.SetAttributes(f2, FileAttributes.ReadOnly);
        });
        try
        {
            AddStep(package, "4.0.0-4.1.0.json",
                "{\"from\":\"4.0.0\",\"to\":\"4.1.0\",\"changes\":["
                + "{\"file\":\"f1.json\",\"ops\":[{\"op\":\"rename\",\"from\":\"a\",\"to\":\"b\"}]},"
                + "{\"file\":\"f2.json\",\"ops\":[{\"op\":\"rename\",\"from\":\"a\",\"to\":\"b\"}]}]}");

            var ex = Record.Exception(() => Run(root, data, package));

            Assert.NotNull(ex);
            Assert.Equal("{\"version\":1,\"a\":1}", File.ReadAllText(Path.Combine(data, "f1.json")));
        }
        finally
        {
            var f2 = Path.Combine(data, "f2.json");
            if (File.Exists(f2)) File.SetAttributes(f2, FileAttributes.Normal);
            UpdateTestKit.Cleanup(root);
        }
    }
}
