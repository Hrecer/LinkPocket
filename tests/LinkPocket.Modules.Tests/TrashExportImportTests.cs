using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using LinkPocket.Modules.Trash;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// trash.export / trash.import（lptrash，4.0.0 批次 C）：
/// 往返保真（**原 ID 保留** / 字段逐项 / 层级镜像）、origin 三级解析（id → canonical 路径 → 根回落 + 计数如实）、
/// 空回收站前置（<c>LP.STATE.006</c>）、文件校验（版本门 / SHA-256 / 包内一致性）与干跑零副作用。
/// </summary>
public class TrashExportImportTests
{
    private static string TempFilePath()
        => Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lptrash_{Guid.NewGuid():N}.lptrash");

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { /* 临时物交给系统清理 */ }
        }
    }

    [Fact]
    public async Task 往返_原ID与字段保真_还原沿路径落回新树()
    {
        var file = TempFilePath();
        try
        {
            // —— A：造内容 ——（独立书签 L 在「工作」；单元「资料/子」带书签 M 整体入站）
            var (engineA, _, _) = TestHost.Create();
            var work = (await engineA.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" })).Data!;
            var l = (await engineA.ExecuteAsync<LinkDto>("links.create",
                new { url = "https://a.example/", title = "L", list_id = work.FolderId })).Data!;
            await engineA.ExecuteAsync<LinkTrashResult>("links.trash", new { id = l.LinkId });

            var doc = (await engineA.ExecuteAsync<FolderDto>("folders.create", new { name = "资料" })).Data!;
            var sub = (await engineA.ExecuteAsync<FolderDto>("folders.create", new { name = "子", parent_id = doc.FolderId })).Data!;
            var m = (await engineA.ExecuteAsync<LinkDto>("links.create",
                new { url = "https://b.example/", title = "M", list_id = sub.FolderId })).Data!;
            await engineA.ExecuteAsync<FolderDeleteResult>("folders.delete", new { folder_id = doc.FolderId, cascade = "trash_links" });

            var listBefore = await engineA.QueryAsync<List<TrashEntryDto>>("trash.list", null);

            var exported = await engineA.ExecuteAsync<JsonElement>("trash.export", new { output_path = file });
            Assert.Equal(2, exported.Data.GetProperty("total_units").GetInt32());
            Assert.Equal(2, exported.Data.GetProperty("total_links").GetInt32());
            Assert.Equal("lptrash/1.0", exported.Data.GetProperty("format_version").GetString());
            Assert.True(File.Exists(file));

            // —— B：新装（路径同名但全为新 ID），导入 ——
            var (engineB, _, _) = TestHost.Create();
            var workB = (await engineB.ExecuteAsync<FolderDto>("folders.create", new { name = "工作" })).Data!;
            _ = (await engineB.ExecuteAsync<FolderDto>("folders.create", new { name = "资料" })).Data!;

            var imported = await engineB.ExecuteAsync<JsonElement>("trash.import", new { file_path = file });
            Assert.Equal(2, imported.Data.GetProperty("total_units").GetInt32());
            Assert.Equal(2, imported.Data.GetProperty("total_links").GetInt32());
            // origin 解析：单元「资料」原在根 = by_id；
            // 「子」与 L 的原 ID 已死 → 沿 canonical 路径解析到新树 = by_path；
            // M 的原目录「子」**不在活树**（它在回收站镜像里）→ 解析失败 → 回落计数如实 +1。
            // （M 随单元整树还原时按回收站层级回位，不受此影响。）
            var origins = imported.Data.GetProperty("origins");
            Assert.Equal(1, origins.GetProperty("resolved_by_id").GetInt32());
            Assert.Equal(2, origins.GetProperty("resolved_by_path").GetInt32());
            Assert.Equal(1, origins.GetProperty("root_fallback").GetInt32());
            Assert.Contains("M", imported.Data.GetProperty("fell_back_entries")
                .EnumerateArray().Select(e => e.GetString()));

            // 原 ID 保留：平铺列表逐条同 ID
            var listAfter = await engineB.QueryAsync<List<TrashEntryDto>>("trash.list", null);
            Assert.Equal(
                listBefore.Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal),
                listAfter.Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal));

            // 单元内部结构镜像保真（子单元 + 单元内书签）
            var contents = await engineB.QueryAsync<List<TrashEntryDto>>("trash.unit_contents", new { id = doc.FolderId });
            Assert.Contains(contents, e => e.Id == sub.FolderId);
            Assert.Contains(contents, e => e.Id == m.LinkId);

            // 还原：L 沿 canonical 路径落回**新树**的「工作」；单元落根、子随单元、M 随子
            var restored = (await engineB.ExecuteAsync<TrashRestoreResult>("trash.restore", new { id = l.LinkId })).Data!;
            Assert.Equal(workB.FolderId, restored.ListId);
            Assert.False(restored.FellBackToRoot);

            await engineB.ExecuteAsync<TrashRestoreUnitResult>("trash.restore_unit", new { unit_id = doc.FolderId });
            var docAfter = await engineB.QueryAsync<FolderDto>("folders.get", new { folder_id = doc.FolderId });
            Assert.Null(docAfter.ParentId);                       // 原在根 → 落根
            var subAfter = await engineB.QueryAsync<FolderDto>("folders.get", new { folder_id = sub.FolderId });
            Assert.Equal(doc.FolderId, subAfter.ParentId);        // 子随单元（镜像层级保真）
            var mAfter = await engineB.QueryAsync<LinkDto>("links.get", new { id = m.LinkId });
            Assert.Equal(sub.FolderId, mAfter.ListId);
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Fact]
    public async Task 非空回收站_拒绝且原样不动()
    {
        var file = TempFilePath();
        try
        {
            var (engineA, _, _) = TestHost.Create();
            var a = (await engineA.ExecuteAsync<LinkDto>("links.create", new { url = "https://x.example/" })).Data!;
            await engineA.ExecuteAsync<LinkTrashResult>("links.trash", new { id = a.LinkId });
            await engineA.ExecuteAsync<JsonElement>("trash.export", new { output_path = file });

            // B 先自己放一条进站 → 目标非空 → 拒绝
            var (engineB, _, _) = TestHost.Create();
            var b = (await engineB.ExecuteAsync<LinkDto>("links.create", new { url = "https://y.example/" })).Data!;
            await engineB.ExecuteAsync<LinkTrashResult>("links.trash", new { id = b.LinkId });

            var ex = await Assert.ThrowsAsync<EngineException>(
                () => engineB.ExecuteAsync<JsonElement>("trash.import", new { file_path = file }));
            Assert.Equal(EngineErrors.TargetNotEmpty, ex.Error.Code);
            Assert.Contains("empty", ex.Error.Message, StringComparison.OrdinalIgnoreCase);

            // 目标原样未动（仍只有 B 自己那一条）
            var list = await engineB.QueryAsync<List<TrashEntryDto>>("trash.list", null);
            Assert.Single(list);
            Assert.Equal(b.LinkId, list[0].Id);
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Fact]
    public async Task 断链回落根_计数如实暴露()
    {
        var file = TempFilePath();
        try
        {
            var (engineA, _, _) = TestHost.Create();
            var temp = (await engineA.ExecuteAsync<FolderDto>("folders.create", new { name = "临时" })).Data!;
            var t = (await engineA.ExecuteAsync<LinkDto>("links.create",
                new { url = "https://t.example/", title = "T", list_id = temp.FolderId })).Data!;
            await engineA.ExecuteAsync<LinkTrashResult>("links.trash", new { id = t.LinkId });
            await engineA.ExecuteAsync<JsonElement>("trash.export", new { output_path = file });

            // B 里没有「临时」这条路 → 三级解析全失败 → 记 NULL + 计数如实
            var (engineB, _, _) = TestHost.Create();
            var imported = await engineB.ExecuteAsync<JsonElement>("trash.import", new { file_path = file });
            var origins = imported.Data.GetProperty("origins");
            Assert.Equal(0, origins.GetProperty("resolved_by_path").GetInt32());
            Assert.Equal(1, origins.GetProperty("root_fallback").GetInt32());
            Assert.Contains("T", imported.Data.GetProperty("fell_back_entries")
                .EnumerateArray().Select(e => e.GetString()));

            // 还原 = 落根；回落暴露发生在**导入**结果的计数里（above）——
            // 还原侧只认"快照 origin 为 NULL = 原在根"的既有语义（导入已把死引用如实清成 NULL）。
            var restored = (await engineB.ExecuteAsync<TrashRestoreResult>("trash.restore", new { id = t.LinkId })).Data!;
            Assert.Null(restored.ListId);
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Fact]
    public async Task 版本门与完整性_未知主版本_篡改_坏JSON都明确拒绝()
    {
        var file = TempFilePath();
        try
        {
            var (engineA, _, _) = TestHost.Create();
            var a = (await engineA.ExecuteAsync<LinkDto>("links.create", new { url = "https://v.example/" })).Data!;
            await engineA.ExecuteAsync<LinkTrashResult>("links.trash", new { id = a.LinkId });
            await engineA.ExecuteAsync<JsonElement>("trash.export", new { output_path = file });
            var (engineB, _, _) = TestHost.Create();

            // ① 未来主版本：lptrash/2.0 → 拒绝（升级应用）
            ReplaceEntry(file, "manifest.json", SetManifestVersion(ReadEntry(file, "manifest.json"), "lptrash/2.0"));
            var v2 = await Assert.ThrowsAsync<EngineException>(
                () => engineB.ExecuteAsync<JsonElement>("trash.import", new { file_path = file }));
            Assert.Contains("unsupported trash format version", v2.Error.Message);

            // ② 别的家族：lpbackup/1.0 → 拒绝
            ReplaceEntry(file, "manifest.json", SetManifestVersion(ReadEntry(file, "manifest.json"), "lpbackup/1.0"));
            var wrongFamily = await Assert.ThrowsAsync<EngineException>(
                () => engineB.ExecuteAsync<JsonElement>("trash.import", new { file_path = file }));
            Assert.Contains("unsupported trash format version", wrongFamily.Error.Message);

            // ③ SHA-256 篡改：改 data.json 不更新哈希 → 拒绝
            ReplaceEntry(file, "manifest.json", SetManifestVersion(ReadEntry(file, "manifest.json"), "lptrash/1.0"));
            ReplaceEntry(file, "data.json", ReadEntry(file, "data.json") + " ");
            var tampered = await Assert.ThrowsAsync<EngineException>(
                () => engineB.ExecuteAsync<JsonElement>("trash.import", new { file_path = file }));
            Assert.Contains("integrity check failed", tampered.Error.Message);

            // ④ 包内一致性：重复链接 ID（改数据 + 同步哈希）→ 拒绝
            var data = JsonNode.Parse(ReadEntry(file, "data.json").TrimEnd())!;
            var links = (JsonArray)data["links"]!;
            links.Add(links[0]!.DeepClone());
            ReplaceEntry(file, "data.json", data.ToJsonString());
            RecomputeSha(file);
            var dup = await Assert.ThrowsAsync<EngineException>(
                () => engineB.ExecuteAsync<JsonElement>("trash.import", new { file_path = file }));
            Assert.Contains("duplicate bookmark id", dup.Error.Message);
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Fact]
    public async Task 干跑_全量校验零副作用()
    {
        var file = TempFilePath();
        try
        {
            var (engineA, _, _) = TestHost.Create();
            var a = (await engineA.ExecuteAsync<LinkDto>("links.create", new { url = "https://dry.example/" })).Data!;
            await engineA.ExecuteAsync<LinkTrashResult>("links.trash", new { id = a.LinkId });
            await engineA.ExecuteAsync<JsonElement>("trash.export", new { output_path = file });

            var (engineB, _, _) = TestHost.Create();
            var dry = await engineB.ExecuteAsync<JsonElement>("trash.import",
                new { file_path = file }, new CallOptions(DryRun: true));
            Assert.True(dry.Data.GetProperty("dry_run").GetBoolean());
            Assert.Equal(1, dry.Data.GetProperty("total_links").GetInt32());

            // 零副作用：回收站仍为空；真实的导入随后照常成功
            Assert.Empty(await engineB.QueryAsync<List<TrashEntryDto>>("trash.list", null));
            var real = await engineB.ExecuteAsync<JsonElement>("trash.import", new { file_path = file });
            Assert.Equal(1, real.Data.GetProperty("total_links").GetInt32());
            Assert.Single(await engineB.QueryAsync<List<TrashEntryDto>>("trash.list", null));
        }
        finally
        {
            Cleanup(file);
        }
    }

    // —— zip 包改写小工具（包格式 = zip + manifest.json + data.json，可直接编辑）——

    private static string ReadEntry(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static void ReplaceEntry(string path, string name, string content)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        zip.GetEntry(name)!.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static string SetManifestVersion(string manifestJson, string version)
    {
        var node = JsonNode.Parse(manifestJson)!;
        node["version"] = version;
        return node.ToJsonString();
    }

    /// <summary>按当前 data.json 重算 SHA-256 并写回 manifest（模拟"改了数据还同步了哈希"的包）。</summary>
    private static void RecomputeSha(string path)
    {
        var dataBytes = Encoding.UTF8.GetBytes(ReadEntry(path, "data.json"));
        var sha = Convert.ToHexString(SHA256.HashData(dataBytes)).ToLowerInvariant();
        var manifest = JsonNode.Parse(ReadEntry(path, "manifest.json"))!;
        manifest["data_sha256"] = sha;
        ReplaceEntry(path, "manifest.json", manifest.ToJsonString());
    }
}
