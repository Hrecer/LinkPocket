using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 大输入不再按固定规模被拒绝：书签 HTML 与备份包的上限必须随数据规模走，而不是反过来
/// 把 10 万条级的合法库挡在门外。两条钉的都是"旧上限会误判、新行为可导入"。
/// </summary>
public class LargeInputTests
{
    /// <summary>
    /// 旧实现按 64MB 直接拒绝书签 HTML（10 万条带内嵌图标可远超它）。
    /// 这里造一个 **65MB 但结构合法**的文件：文件大小不是"像不像书签文件"的判据。
    /// </summary>
    [Fact]
    public async Task 超过旧64MB上限的书签HTML_仍可预检()
    {
        var (engine, _, _) = TestHost.Create();
        var htmlPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbm_large_{Guid.NewGuid():N}.html");
        try
        {
            const int blockBytes = 1024 * 1024;
            const int blocks = 65;   // 65MB > 旧 64MB 上限
            var filler = new string('x', blockBytes - 16);
            await using (var stream = new FileStream(htmlPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<!--\n");
                for (var i = 0; i < blocks; i++) await writer.WriteAsync(filler + "\n");
                await writer.WriteAsync("-->\n<DL><p>\n<DT><A HREF=\"https://large.example/one\">Large</A>\n</DL><p>\n");
            }

            var info = new FileInfo(htmlPath);
            Assert.True(info.Length > 64L * 1024 * 1024, $"夹具应超过旧上限，实际 {info.Length} 字节");

            var inspection = await engine.QueryAsync<BookmarkFileInspectionDto>(
                "bookmarks.inspect", new { file_path = htmlPath });

            Assert.True(inspection.IsValid, inspection.Error);
            Assert.Equal(1, inspection.LinkCount);
        }
        finally
        {
            if (File.Exists(htmlPath)) File.Delete(htmlPath);
        }
    }

    /// <summary>
    /// 旧实现按"图标条目 ≤ 4096"拒绝整个备份；而 10 万条书签的库带几千个域名图标很正常。
    /// 这里造 4097 个图标条目 + 空数据：应当通过完整性校验并完成导入，而不是被判成异常备份。
    /// </summary>
    [Fact]
    public async Task 超过旧4096图标条目的备份_仍可导入()
    {
        var (engine, _, _) = TestHost.Create();
        var zipPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpbackup_large_{Guid.NewGuid():N}.lpbackup");
        try
        {
            var dataBytes = Encoding.UTF8.GetBytes("""{"folders":[],"links":[]}""");
            var manifest = JsonSerializer.Serialize(new
            {
                version = "lpbackup/2.0",
                app_version = "test",
                created_at = "2026-01-01T00:00:00Z",
                created_by = "LinkPocket",
                data_sha256 = Convert.ToHexString(SHA256.HashData(dataBytes)).ToLowerInvariant(),
                statistics = new { total_folders = 0, total_links = 0, total_favicons = 4097 },
            });

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                await WriteEntryAsync(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest));
                await WriteEntryAsync(zip, "data.json", dataBytes);
                for (var i = 0; i <= 4096; i++)   // 4097 条：刚好越过旧上限
                    await WriteEntryAsync(zip, $"favicons/icon-{i:D4}.ico", [137, 80, 78, 71]);
            }

            // 破坏性命令先拿确认令牌（两阶段确认），第二次才真正执行导入。
            var args = new { file_path = zipPath, replace = false };
            var confirm = await Assert.ThrowsAsync<EngineException>(
                () => engine.ExecuteAsync<JsonElement>("backup.import", args));
            var token = confirm.Error.Details!.Value.GetProperty("confirm_token").GetString();
            var imported = await engine.ExecuteAsync<JsonElement>(
                "backup.import", args, new CallOptions(ConfirmToken: token));

            Assert.Equal(0, imported.Data.GetProperty("folders_created").GetInt32());
            Assert.Equal(0, imported.Data.GetProperty("links_created").GetInt32());
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes);
    }
}
