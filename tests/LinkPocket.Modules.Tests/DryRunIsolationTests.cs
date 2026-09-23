using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 干跑隔离（G6 收口）：FileIo / 网络类命令在 <c>DryRun</c> 下**零副作用**——不落盘、不联网、不入队；
/// 校验照做、影响面如实预告（"先预演再执行"不再打折）。
/// </summary>
public class DryRunIsolationTests
{
    [Fact]
    public async Task 导出三命令_干跑不落盘_真实执行才写文件()
    {
        var (engine, _, _) = TestHost.Create();
        await engine.ExecuteAsync<LinkDto>("links.create", new { url = "https://dry.example/1", title = "甲" });
        var dir = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpdry_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // links.export：干跑不产生文件，但如实预告将写的字节数
            var linkTarget = Path.Combine(dir, "links.json");
            var dryLinks = await engine.ExecuteAsync<LinkExportResult>("links.export",
                new { file_path = linkTarget }, new CallOptions(DryRun: true));
            Assert.False(File.Exists(linkTarget));
            Assert.True(dryLinks.Data!.FileBytes > 0);

            var liveLinks = await engine.ExecuteAsync<LinkExportResult>("links.export", new { file_path = linkTarget });
            Assert.True(File.Exists(linkTarget));
            Assert.True(liveLinks.Data!.FileBytes > 0);

            // bookmarks.export：同口径（干跑连原子写的临时文件都不留）
            var marksTarget = Path.Combine(dir, "marks.html");
            var dryMarks = await engine.ExecuteAsync<JsonElement>("bookmarks.export",
                new { file_path = marksTarget }, new CallOptions(DryRun: true));
            Assert.False(File.Exists(marksTarget));
            Assert.True(dryMarks.Data.GetProperty("file_bytes").GetInt64() > 0);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));

            await engine.ExecuteAsync<JsonElement>("bookmarks.export", new { file_path = marksTarget });
            Assert.True(File.Exists(marksTarget));

            // backup.export：干跑不打包（临时文件也算落盘），只报影响面、不谎报字节数
            var backupTarget = Path.Combine(dir, "backup.lpbackup");
            var dryBackup = await engine.ExecuteAsync<JsonElement>("backup.export",
                new { output_path = backupTarget }, new CallOptions(DryRun: true));
            Assert.False(File.Exists(backupTarget));
            Assert.True(dryBackup.Data.GetProperty("dry_run").GetBoolean());
            Assert.True(dryBackup.Data.GetProperty("total_links").GetInt32() >= 1);
            Assert.False(dryBackup.Data.TryGetProperty("file_bytes", out _));

            await engine.ExecuteAsync<JsonElement>("backup.export", new { output_path = backupTarget });
            Assert.True(File.Exists(backupTarget));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task 图标预取_干跑不入队()
    {
        var (engine, _, _) = TestHost.Create();
        var queueId = Guid.NewGuid().ToString("N");
        var link = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://pf-dry.example/", favicon_url = $"http://127.0.0.1:1/{queueId}.ico" })).Data!;

        var dry = await engine.ExecuteAsync<JsonElement>("favicon.prefetch",
            new { link_ids = new[] { link.LinkId } }, new CallOptions(DryRun: true));
        Assert.True(dry.Data.GetProperty("dry_run").GetBoolean());
        Assert.Equal(1, dry.Data.GetProperty("would_queue").GetInt32());   // 如实预告，但不入队（入队即触发下载与缓存落盘）
    }
}
