using LinkPocket.Contracts;

namespace ProtocolSmoke;

/// <summary>§4 Netscape 书签往返 + §5 .lpbackup 备份往返（断言语义自旧协议冒烟平移）。</summary>
internal static partial class SmokeRunner
{
    // Chrome / Edge / Firefox 导出格式的等价样本（含空文件夹、实体转义、CJK、DD 描述、about:blank 占位）
    private const string SampleBookmarks =
        """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <!-- This is an automatically generated file.
             It will be read and overwritten.
             DO NOT EDIT! -->
        <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
        <TITLE>Bookmarks</TITLE>
        <H1>Bookmarks</H1>
        <DL><p>
            <DT><A HREF="https://www.example.com/root" ADD_DATE="1700000000" ICON="https://www.example.com/favicon.ico">根级书签</A>
            <DT><H3 ADD_DATE="1600000000" LAST_MODIFIED="1700000100" PERSONAL_TOOLBAR_FOLDER="true">书签栏</H3>
            <DL><p>
                <DT><A HREF="https://www.google.com/search?a=1&amp;b=2" ADD_DATE="1600000050" ICON="https://www.google.com/favicon.ico">Google 搜索</A>
                <DT><H3 ADD_DATE="1600000100">开发 &amp; 工具</H3>
                <DL><p>
                    <DT><A HREF="https://github.com/" ADD_DATE="1600000200" LAST_MODIFIED="1650000000">GitHub</A>
                    <DD>代码托管平台 &lt;好用的&gt;
                    <DT><A HREF="javascript:void(0)" ADD_DATE="1600000300">JS 小工具</A>
                </DL><p>
                <DT><H3 ADD_DATE="1600000400">空文件夹A</H3>
                <DT><H3 ADD_DATE="1600000410">空文件夹B（有子列表）</H3>
                <DL><p>
                </DL><p>
                <DT><H3 ADD_DATE="1600000420">中文目录 · 深度</H3>
                <DL><p>
                    <DT><H3 ADD_DATE="1600000430">二级</H3>
                    <DL><p>
                        <DT><A HREF="https://cn.example.com/?q=%E4%B8%AD%E6%96%87" ADD_DATE="1600000440">中文链接</A>
                    </DL><p>
                </DL><p>
            </DL><p>
            <DT><H3 ADD_DATE="1600000500">其他书签</H3>
            <DL><p>
                <DT><A HREF="about:blank" ADD_DATE="1600000600">占位</A>
                <DT><A HREF="https://edge.example.com/" ADD_DATE="1600000700">Edge 书签</A>
            </DL><p>
        </DL><p>
        """;

    private static async Task SectionBookmarks(SmokeState s)
    {
        // 旧版语义：书签往返在全新库上进行（Reset 返回新引擎：旧引用指向已删除的库文件，必须弃用）
        var client = s.Reset();
        var workDir = s.WorkDir;
        var samplePath = Path.Combine(workDir, "sample_bookmarks.html");
        await File.WriteAllTextAsync(samplePath, SampleBookmarks, new System.Text.UTF8Encoding(false));

        // 只读预检
        var inspection = (await client.BookmarksInspectAsync(samplePath));
        Asserts.That(inspection.IsValid, $"预检应判定为有效书签文件：{inspection.Error}");
        Asserts.That(inspection.Format.Contains("NETSCAPE-Bookmark-file-1"), "预检应识别 Netscape 格式");
        Asserts.That(inspection.FolderCount == 7, $"预检文件夹数应为 7，实际 {inspection.FolderCount}");
        Asserts.That(inspection.LinkCount == 6, $"预检书签数应为 6，实际 {inspection.LinkCount}");
        Asserts.That(inspection.SkippedCount == 1, $"应跳过 1 条 about:blank，实际 {inspection.SkippedCount}");
        Asserts.That(inspection.MaxDepth == 3, $"最大嵌套应为 3 层，实际 {inspection.MaxDepth}");

        // 导入（引擎版返回 folders_created / links_created）
        s.Events.Clear();
        var imported = (await client.BookmarksImportAsync(samplePath)).Data!;
        Asserts.That(imported.GetProperty("folders_created").GetInt32() == 7,
            $"导入文件夹数应为 7，实际 {imported.GetProperty("folders_created").GetInt32()}");
        Asserts.That(imported.GetProperty("links_created").GetInt32() == 6,
            $"导中书签数应为 6，实际 {imported.GetProperty("links_created").GetInt32()}");
        Asserts.That(s.Events.Contains("links.changed") && s.Events.Contains("folders.changed"),
            "导入应推 links.changed / folders.changed");

        // 结构还原
        var tree = (await client.FolderTreeAsync());
        Asserts.That(tree.Count == 7, $"库内文件夹应为 7，实际 {tree.Count}");
        string FolderId(string name) => tree.First(f => f.Name == name).FolderId;
        Asserts.That(tree.First(f => f.Name == "书签栏").ParentId == null, "「书签栏」应落在根级");
        Asserts.That(tree.First(f => f.Name == "二级").ParentId == FolderId("中文目录 · 深度"),
            "「二级」的父文件夹应为「中文目录 · 深度」");

        var links = (await client.LinkListAsync(perPage: 0)).Links;
        Asserts.That(links.Count == 6, $"库内书签应为 6，实际 {links.Count}");
        Asserts.That(links.Count(l => l.ListId == FolderId("空文件夹A")) == 0,
            "空文件夹（无子 <DL>）不应抢兄弟子树");
        Asserts.That(links.Count(l => l.ListId == FolderId("开发 & 工具")) == 2, "「开发 & 工具」应含 2 条书签");

        var google = links.First(l => l.Title == "Google 搜索");
        Asserts.That(google.Url == "https://www.google.com/search?a=1&b=2", "&amp; 应解码为 &");
        Asserts.That(google.FaviconUrl == "https://www.google.com/favicon.ico", "ICON 应还原 favicon_url");
        var github = links.First(l => l.Title == "GitHub");
        Asserts.That(github.Description == "代码托管平台 <好用的>", "<DD> 描述应解码导入");
        Asserts.That(github.CreatedAt == DateTimeOffset.FromUnixTimeSeconds(1600000200).UtcDateTime,
            "ADD_DATE 应还原为创建时间");
        Asserts.That(github.UpdatedAt == DateTimeOffset.FromUnixTimeSeconds(1650000000).UtcDateTime,
            "LAST_MODIFIED 应还原为更新时间");
        Asserts.That(links.Any(l => l.Title == "根级书签" && l.ListId == null), "根级书签应落在根级");
        Asserts.That(!links.Any(l => l.Title == "占位"), "about:blank 条目应被跳过");

        // 导出自校验
        var exportA = Path.Combine(workDir, "export_a.html");
        var exported = (await client.BookmarksExportAsync(exportA)).Data!;
        Asserts.That(File.Exists(exportA), "导出应创建文件");
        Asserts.That(exported.GetProperty("file_path").GetString() == Path.GetFullPath(exportA),
            "导出应返回文件完整路径");

        var exportBytes = await File.ReadAllBytesAsync(exportA);
        Asserts.That(!(exportBytes.Length >= 3 && exportBytes[0] == 0xEF && exportBytes[1] == 0xBB && exportBytes[2] == 0xBF),
            "导出应为 UTF-8 无 BOM");
        var exportText = await File.ReadAllTextAsync(exportA);
        Asserts.That(exportText.StartsWith("<!DOCTYPE NETSCAPE-Bookmark-file-1>"), "导出首行应为 Netscape DOCTYPE");

        var exportInspection = (await client.BookmarksInspectAsync(exportA));
        Asserts.That(exportInspection.IsValid && exportInspection.FolderCount == 7 && exportInspection.LinkCount == 6
            && exportInspection.SkippedCount == 0 && exportInspection.MaxDepth == 3,
            "导出产物应能被自身解析且条目数一致");

        // 二次往返：全新库重导入 → 再导出 → 逐行一致
        client = s.Reset();
        var reimported = (await client.BookmarksImportAsync(exportA)).Data!;
        Asserts.That(reimported.GetProperty("folders_created").GetInt32() + reimported.GetProperty("links_created").GetInt32() == 13,
            "往返导入条目数应为 13");

        var exportB = Path.Combine(workDir, "export_b.html");
        await client.BookmarksExportAsync(exportB);

        // 文件夹 LAST_MODIFIED 导入时被内核按内容变动刷新（应用语义）→ 比对时归一
        static string Normalize(string line)
            => line.Contains("<H3")
                ? System.Text.RegularExpressions.Regex.Replace(line, "LAST_MODIFIED=\"\\d+\"", "LAST_MODIFIED=\"X\"")
                : line;

        var linesA = (await File.ReadAllLinesAsync(exportA)).Where(l => l.Length > 0).Select(Normalize).OrderBy(l => l, StringComparer.Ordinal).ToList();
        var linesB = (await File.ReadAllLinesAsync(exportB)).Where(l => l.Length > 0).Select(Normalize).OrderBy(l => l, StringComparer.Ordinal).ToList();
        Asserts.That(linesA.Count == linesB.Count, $"往返行数应一致（{linesA.Count} vs {linesB.Count}）");
        var diff = linesA.Zip(linesB).FirstOrDefault(p => p.First != p.Second);
        Asserts.That(diff == default, $"往返导出应逐行一致，差异：A「{diff.First}」≠ B「{diff.Second}」");

        var treeAfter = (await client.FolderTreeAsync());
        Asserts.That(treeAfter.Count == 7, "往返后文件夹应为 7");
        Asserts.That((await client.LinkListAsync(perPage: 0)).Links.Count == 6, "往返后书签应为 6");

        Console.WriteLine("[OK] §4 书签往返：预检计数 / 结构还原 / 实体解码 / UTF-8 无 BOM / 二次导出逐行一致");
    }

    private static async Task SectionBackup(SmokeState s)
    {
        // 旧版语义：备份场景在全新库上构建（阶段一 = 构建 + 导出）
        var client = s.Reset();
        s.Events.Clear();

        // 场景：同父重名 + 名含「>」+ 回收站内容（单独删除 + 整树删除）→ 备份 → 全新库恢复
        var work1 = (await client.FolderCreateAsync("工作")).Data!;
        var work2 = (await client.FolderCreateAsync("工作")).Data!;
        var odd = (await client.FolderCreateAsync("A > B", parentId: work1.FolderId)).Data!;
        await client.LinkCreateAsync("https://b.example.com/1", "链接一", listId: work1.FolderId);
        await client.LinkCreateAsync("https://b.example.com/2", "链接二", listId: work2.FolderId);
        await client.LinkCreateAsync("https://b.example.com/3", "链接三", listId: odd.FolderId);
        await client.LinkCreateAsync("https://b.example.com/root", "根级", listId: null);
        var trashMe = (await client.LinkCreateAsync("https://b.example.com/trashme", "回收我", listId: work1.FolderId)).Data!;
        await client.LinkTrashAsync(trashMe.LinkId);
        var delFolder = (await client.FolderCreateAsync("删我", parentId: work2.FolderId)).Data!;
        await client.LinkCreateAsync("https://b.example.com/sub", "子链接", listId: delFolder.FolderId);
        await client.FolderDeleteAsync(delFolder.FolderId);

        var backupPath = Path.Combine(s.WorkDir, "test_backup.lpbackup");
        var tamperedPath = Path.Combine(s.WorkDir, "tampered_backup.lpbackup");
        foreach (var stale in new[] { backupPath, tamperedPath })
            if (File.Exists(stale)) File.Delete(stale);

        await client.BackupExportAsync(backupPath);
        Asserts.That(File.Exists(backupPath), "备份导出后文件应存在");

        static string ReadZipEntry(string zipPath, string entryName)
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(entryName);
            Asserts.That(entry != null, $"备份包应含 {entryName}");
            using var reader = new StreamReader(entry!.Open());
            return reader.ReadToEnd();
        }

        var manifestText = ReadZipEntry(backupPath, "manifest.json");
        var dataText = ReadZipEntry(backupPath, "data.json");
        Asserts.That(manifestText.Contains("data_sha256"), "manifest 应含 data.json 的 SHA-256");
        // 跨版本迁移的关键诊断信息：包要写清"是哪个应用版本、哪个格式版本产出的"（按 JSON 读，不按字符串形状猜）
        using var manifest = System.Text.Json.JsonDocument.Parse(manifestText);
        var root = manifest.RootElement;
        var formatVersion = root.GetProperty("version").GetString() ?? "";
        var appVersion = root.TryGetProperty("app_version", out var av) ? av.GetString() : null;
        // 只断言家族前缀：主次版本由 `BackupIO.FormatVersion` 唯一持有，测试再写一遍字号就成了第二个事实源
        Asserts.That(formatVersion.StartsWith("lpbackup/", StringComparison.Ordinal),
            $"manifest 应写明 lpbackup 家族格式版本标识（实际 {formatVersion}）");
        var selfVersion = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                typeof(LinkPocket.Contracts.EngineException).Assembly)?.InformationalVersion;
        Asserts.That(!string.IsNullOrWhiteSpace(appVersion) && appVersion == selfVersion,
            $"manifest 的 app_version 应是当前应用版本（实际 {appVersion}，期望 {selfVersion}）");
        Asserts.That(dataText.Contains("\"key\""), "data.json 应使用临时 key 表达层级");
        Asserts.That(!dataText.Contains("folder_id") && !dataText.Contains("link_id"),
            "备份格式不应携带任何数据库 ID（防撞）");
        Asserts.That(!dataText.Contains("回收我") && !dataText.Contains("删我"),
            "回收站内容不应出现在备份里");

        // 全新库 + 两阶段确认导入（backup.import 为破坏性命令）
        // 阶段二起改用 Reset() 返回的新引擎 fresh：下文一切调用（含篡改导入）都必须走它
        var fresh = s.Reset();
        var ex = await AssertThrowsAsync(() => fresh.BackupImportAsync(backupPath));
        Asserts.That(ex.Error.Code == EngineErrors.ConfirmRequired, "backup.import 无令牌应报 LP.SEC.003");
        var token = ex.Error.Details!.Value.GetProperty("confirm_token").GetString();
        var import = (await fresh.BackupImportAsync(backupPath, o: new CallOptions(ConfirmToken: token))).Data!;
        Asserts.That(import.GetProperty("folders_created").GetInt32() == 3,
            $"活数据文件夹应恢复 3 个，实际 {import.GetProperty("folders_created").GetInt32()}");
        Asserts.That(import.GetProperty("links_created").GetInt32() == 4,
            $"活数据书签应恢复 4 条，实际 {import.GetProperty("links_created").GetInt32()}");

        var afterTree = (await fresh.FolderTreeAsync());
        // 同层唯一命名（v4）后同父重名已不可能存在：种子里的两个「工作」在创建时就被编号为「工作」「工作 (2)」，
        // 备份往返（名字键格式）必须原样保留这两个名字——这是最容易串位的地方
        Asserts.That(afterTree.Count(f => f.Name == "工作") == 1 && afterTree.Count(f => f.Name == "工作 (2)") == 1,
            "备份往返应原样保留「工作」与「工作 (2)」两个文件夹");
        var afterLinks = (await fresh.LinkListAsync(perPage: 0)).Links;
        Asserts.That(afterLinks.Count == 4, "恢复后活数据书签应为 4 条");
        Asserts.That(afterLinks.First(l => l.Title == "链接一").ListId != afterLinks.First(l => l.Title == "链接二").ListId,
            "重名文件夹下的书签不应串位");
        Asserts.That(afterLinks.First(l => l.Title == "链接三").ListId == afterTree.First(f => f.Name == "A > B").FolderId,
            "名字含「>」的文件夹应正常恢复");
        Asserts.That(afterLinks.Any(l => l.Title == "根级" && l.ListId == null), "根级书签应恢复到根级");
        Asserts.That(!(await fresh.TrashListAsync()).Any(), "回收站不备份：导入后平铺应为空");
        Asserts.That(!(await fresh.TrashTreeAsync()).Any(), "回收站不备份：导入后树应为空");

        // 篡改 data.json → SHA-256 完整性校验拒绝（LP.VAL.004，消息说明完整性）
        // 确认令牌一次性消费：篡改导入先重新取得新令牌，再持新令牌触发真正的校验拒绝
        using (var src = System.IO.Compression.ZipFile.OpenRead(backupPath))
        using (var dst = System.IO.Compression.ZipFile.Open(tamperedPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var entry in src.Entries)
            {
                var newEntry = dst.CreateEntry(entry.FullName);
                using var srcStream = entry.Open();
                using var dstStream = newEntry.Open();
                if (entry.FullName == "data.json")
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(new StreamReader(srcStream).ReadToEnd() + " ");
                    dstStream.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    srcStream.CopyTo(dstStream);
                }
            }
        }

        var tamperedGate = await AssertThrowsAsync(() => fresh.BackupImportAsync(tamperedPath));
        Asserts.That(tamperedGate.Error.Code == EngineErrors.ConfirmRequired, "篡改导入同样先走确认门");
        var tamperedToken = tamperedGate.Error.Details!.Value.GetProperty("confirm_token").GetString();
        var tampered = await AssertThrowsAsync(() => fresh.BackupImportAsync(tamperedPath, o: new CallOptions(ConfirmToken: tamperedToken)));
        Asserts.That(tampered.Error.Code == EngineErrors.InvalidPath, "被篡改备份应被拒绝（LP.VAL.004）");
        Asserts.That(tampered.Error.Message.Contains("integrity"), $"错误信息应说明完整性校验失败（引擎侧英文技术文案），实际：{tampered.Error.Message}");

        Console.WriteLine("[OK] §5 备份往返：临时 key 身份 / SHA-256 篡改拒绝 / 回收站不备份 / 两阶段确认导入");
    }

    private static async Task<EngineException> AssertThrowsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (EngineException ex)
        {
            return ex;
        }
        throw new Exception("应抛 EngineException");
    }
}
