using System.Text.Json;
using LinkPocket.Contracts;

namespace ProtocolSmoke;

/// <summary>
/// §10 编排层：批（事务回滚/continue/独立/dry_run/status/ref 模板）→ 宏 → 撤销/重做
/// → Staging 全生命周期 → diagnostics.collect → audit_log/idempotency 落表。
/// </summary>
internal static partial class SmokeRunner
{
    private static async Task SectionOrchestration(SmokeState s)
    {
        // —— §10.1 wire batch.run 事务批 + {ref} 模板：建目录 → 引用上一步结果在目录内建链接 ——
        // 编排命令 wire 语义 = 返回数据本体（与查询同形），不套 { ok, data } 外壳
        var report = JsonDocument.Parse(await s.Wire.HandleAsync("""
            {"jsonrpc":"2.0","id":101,"method":"batch.run","params":{"script":{
              "name":"smoke批","scope":"transactional",
              "steps":[
                {"ref":"mk","command":"folders.create","args":{"name":"批目录"}},
                {"ref":"mklink","command":"links.create","args":{"list_id":"{mk.id}","url":"https://batch.example.com/","title":"批链接"}}
              ]}}}
            """)).RootElement.GetProperty("result");
        Asserts.That(report.GetProperty("ok").GetBoolean(), "事务批报告 ok 应为 true");
        Asserts.That(report.GetProperty("steps").GetArrayLength() == 2, "报告应含两个步骤结果");
        var folderId = report.GetProperty("steps")[0].GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("folders.create 结果缺 id");
        Asserts.That(folderId.Length > 0, "模板引用源步骤应有 id");

        var inFolder = await s.Client.QueryAsync<PagedLinksDto>("links.list", new { list_id = folderId });
        Asserts.That(inFolder.TotalCount == 1,
            "{ref} 模板应把上一步目录 id 传入 links.create（目录内 1 条链接）");

        // —— §10.2 事务批 abort 回滚：第 2 步缺参失败 → LP.STATE.004，第 1 步已回滚 ——
        var totalBefore = (await s.Client.LinkStatsAsync()).Total;
        var abortedScript = new BatchScript("abort批",
        [
            new BatchStep("a", "links.create", JsonSerializer.SerializeToElement(new { url = "https://rollback.example.com/", title = "回滚链接" })),
            new BatchStep("b", "folders.create", JsonSerializer.SerializeToElement(new { })),   // 缺 name → REQUIRED_PARAM
        ]);
        var abortError = await AssertBatchAbortedAsync(s, abortedScript);
        Asserts.That(abortError.Error.Code == EngineErrors.BatchAborted, "abort 失败应抛 LP.STATE.004");
        var totalAfterAbort = (await s.Client.LinkStatsAsync()).Total;
        Asserts.That(totalAfterAbort == totalBefore, "事务批 abort 后第 1 步的写入应已回滚");

        // —— §10.3 continue 策略：中间步失败不阻断，报告 2 成功 1 失败 ——
        var continueScript = new BatchScript("continue批",
        [
            new BatchStep("a", "links.create", JsonSerializer.SerializeToElement(new { url = "https://c1.example.com/", title = "c1" }), ErrorPolicy.Continue),
            new BatchStep("bad", "folders.create", JsonSerializer.SerializeToElement(new { }), ErrorPolicy.Continue),
            new BatchStep("c", "links.create", JsonSerializer.SerializeToElement(new { url = "https://c2.example.com/", title = "c2" }), ErrorPolicy.SkipAndLog),
        ]);
        var continueReport = await s.Client.RunBatchAsync(continueScript);
        Asserts.That(!continueReport.Ok && continueReport.Steps.Count == 3, "continue 批应返回报告（Ok=false）");
        Asserts.That(continueReport.Steps[1].ErrorCode == EngineErrors.RequiredParam, "失败步应带错误码");
        Asserts.That(continueReport.Steps[2].Ok, "SkipAndLog 的后续步应继续执行");
        var totalAfterContinue = (await s.Client.LinkStatsAsync()).Total;
        Asserts.That(totalAfterContinue == totalBefore + 2, "continue 批成功的两步应已提交");

        // —— §10.4 batch.dry_run 零副作用 ——
        var dry = await s.Client.DryRunBatchAsync(new BatchScript("dry批",
        [
            new BatchStep("d", "links.create", JsonSerializer.SerializeToElement(new { url = "https://dry.example.com/", title = "dry" })),
        ]));
        Asserts.That(dry.Ok, "dry_run 批报告 ok 应为 true");
        var totalAfterDry = (await s.Client.LinkStatsAsync()).Total;
        Asserts.That(totalAfterDry == totalAfterContinue, "dry_run 批不应产生任何写入");

        // —— §10.5 batch.status：报告关联的批 ID 状态应为 completed ——
        var status = s.Client.BatchStatusOf(continueReport.BatchId);
        Asserts.That(status is { } && status.State == "failed" && status.CompletedSteps == 3,
            "批状态应反映完成口径（有失败 = failed，步数已计满）");

        // —— §10.6 宏：save → list → run → get → delete ——
        var macroScript = new BatchScript("宏内脚本",
        [
            new BatchStep("mf", "folders.create", JsonSerializer.SerializeToElement(new { name = "宏目录" })),
        ]);
        await s.Client.MacroSaveAsync("smoke宏", macroScript);
        var macroList = await s.Client.MacroListAsync();
        Asserts.That(macroList.GetProperty("macros").EnumerateArray().Any(m => m.GetProperty("name").GetString() == "smoke宏"),
            "macro.list 应含刚保存的宏");
        var macroRun = await s.Client.MacroRunAsync("smoke宏");
        Asserts.That(macroRun.Ok, "macro.run 应成功");
        await s.Client.MacroGetAsync("smoke宏");   // 可读回脚本
        await s.Client.MacroDeleteAsync("smoke宏");
        var gone = await AssertEngineErrorAsync(() => s.Client.MacroGetAsync("smoke宏"));
        Asserts.That(gone.Error.Code == EngineErrors.EntityNotFound, "删除后的宏应不存在");

        // —— §10.7 撤销/重做：links.trash（UndoInverse = trash.restore）→ undo.undo → undo.redo → undo.clear ——
        var link = (await s.Client.LinkCreateAsync("https://undo.example.com/", "撤销链接")).Data
            ?? throw new InvalidOperationException("links.create 未返回 LinkDto");
        var linkId = link.LinkId;
        await s.Client.LinkTrashAsync(linkId);
        var statsInTrash = (await s.Client.LinkStatsAsync()).Trash;

        var undoList = await s.Client.UndoListAsync();
        Asserts.That(undoList.GetProperty("entries").GetArrayLength() >= 1, "links.trash 后撤销栈应有条目");
        await s.Client.UndoAsync();   // 撤销 = trash.restore
        Asserts.That((await s.Client.LinkStatsAsync()).Trash == statsInTrash - 1,
            "undo.undo 后链接应已从回收站还原");

        await s.Client.RedoAsync();   // 重做 = 重放 links.trash
        Asserts.That((await s.Client.LinkStatsAsync()).Trash == statsInTrash,
            "undo.redo 后链接应重新入回收站");
        await s.Client.UndoClearAsync();

        // —— §10.8 Staging 全生命周期：stage → list → inspect → transform(dry/落盘) → commit → discard ——
        var htmlPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpsmoke_bm_{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(htmlPath,
            """<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><p><DT><A HREF="https://staging.example.com/a">A</A></DL><p>""",
            System.Text.Encoding.UTF8);
        var staged = (await s.Client.StageAsync(htmlPath)).Data ?? throw new Exception("staging.stage 未返回登记");
        Asserts.That(staged.Sha256.Length == 64, "暂存登记应含 SHA-256 指纹");

        var stagedList = await s.Client.StagingListAsync();
        Asserts.That(stagedList.GetProperty("files").GetArrayLength() >= 1, "staging.list 应含暂存文件");

        var inspected = await s.Client.QueryAsync<JsonElement>("staging.inspect", new { staging_id = staged.StagingId });
        Asserts.That(inspected.GetProperty("is_valid").GetBoolean(), "staging.inspect 应复用 bookmarks.inspect 判定有效");

        var linksJsonPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpsmoke_links_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(linksJsonPath, """
            [
              {"url":"https://t.example.com/1","title":"T1","folder":"源目录"},
              {"url":"https://t.example.com/2","title":"T2","folder":"源目录"},
              {"url":"https://t.example.com/1","title":"重复","folder":"源目录"}
            ]
            """, System.Text.Encoding.UTF8);
        var stagedLinks = (await s.Client.StageAsync(linksJsonPath)).Data ?? throw new Exception("staging.stage 未返回登记");

        var dryTransform = (await s.Client.StagingTransformAsync(stagedLinks.StagingId,
            [new TransformOp("dedupe", JsonSerializer.SerializeToElement(new { by = "url" })), new TransformOp("rename_folder", JsonSerializer.SerializeToElement(new { from = "源目录", to = "新目录" }))],
            dryRun: true)).Data ?? throw new Exception("staging.transform 未返回报告");
        Asserts.That(dryTransform.ItemsBefore == 3 && dryTransform.ItemsAfter == 2,
            "dedupe 预演应报告 3 → 2");
        Asserts.That((dryTransform.PreviewJson ?? "").Length > 0, "预演应返回预览 JSON");
        var liveTransform = (await s.Client.StagingTransformAsync(stagedLinks.StagingId,
            [new TransformOp("dedupe", JsonSerializer.SerializeToElement(new { by = "url" })), new TransformOp("rename_folder", JsonSerializer.SerializeToElement(new { from = "源目录", to = "新目录" }))],
            dryRun: false)).Data ?? throw new Exception("staging.transform 未返回报告");
        Asserts.That(!liveTransform.DryRun && liveTransform.ItemsAfter == 2, "落盘变换应完成去重");
        // 复检落盘：再次 dry_run 以当前文件内容为输入，ItemsBefore 应为去重后的 2
        var verifyTransform = (await s.Client.StagingTransformAsync(stagedLinks.StagingId,
            [new TransformOp("filter_links", JsonSerializer.SerializeToElement(new { field = "url", op = "contains", value = "t.example.com" }))],
            dryRun: true)).Data ?? throw new Exception("staging.transform 未返回报告");
        Asserts.That(verifyTransform.ItemsBefore == 2, "落盘变换应已持久化（复检输入项 = 2）");

        // commit：把变换后的 staged JSON 转交正式命令（file_path 自动并入；extra 参数与 file_path 合并下发）
        var committed = await s.Client.StagingCommitAsync(stagedLinks.StagingId, "folders.create", new { name = "新目录" });
        Asserts.That(committed.Ok, "staging.commit → 正式命令应成功");
        Asserts.That(committed.Data.ValueKind == JsonValueKind.Object
                     && committed.Data.GetProperty("name").GetString() == "新目录",
            "commit 应把 file_path 与附加参数一并下发目标命令");

        await s.Client.ExecuteAsync<JsonElement>("staging.discard", new { staging_id = staged.StagingId });
        File.Delete(htmlPath);
        File.Delete(linksJsonPath);

        // —— §10.9 diagnostics.collect（Maintenance 模块）：schema 版本 + 表计数（脱敏）——
        // 版本 = 完整版本链的最高版本（v2 基线 + v3/v4/v5/v6 演进）；运行时可观测读数在 §11 校验
        var diag = await s.Client.CollectDiagnosticsAsync();
        Asserts.That(diag.GetProperty("schema_version").GetInt32() == 6, "诊断应报 schema v6（v2 基线 + v3..v6 演进）");
        Asserts.That(diag.TryGetProperty("counts", out _), "诊断应含各表计数");
        // logging 段：冒烟宿主未装配日志管道 → 如实 wired=false（不填假值）；审计段给行数与最旧时刻
        Asserts.That(diag.GetProperty("logging").GetProperty("wired").GetBoolean() == false,
            "冒烟宿主未装配日志管道，logging.wired 应如实为 false");
        Asserts.That(diag.GetProperty("audit").GetProperty("rows").GetInt64() > 0, "审计段应报已有审计行数");

        // —— §10.10 audit_log / idempotency 落表直查 + audit.query / audit.prune 读侧 ——
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={s.DbPath}");
        await db.OpenAsync();
        var auditCount = await ScalarAsync(db, "SELECT COUNT(*) FROM audit_log");
        Asserts.That(auditCount > 0, $"audit_log 表应有落库条目（实际 {auditCount}）");

        // audit.query：倒序分页 + 只取顶层（嵌套子记录不得混进分页语义）——经引擎读侧（DTO 公开，进程内直取）
        var auditQuery = await s.Client.QueryAsync<LinkPocket.Modules.Maintenance.AuditPagedResult>(
            "audit.query", new { per_page = 5, is_nested = false });
        Asserts.That(auditQuery.Total > 0, "audit.query 应能读到审计行");
        Asserts.That(auditQuery.Items.Count <= 5, "per_page 应生效");
        var pruned = await s.Client.ExecuteAsync<LinkPocket.Modules.Maintenance.AuditPruneResult>(
            "audit.prune", new { keep_days = 90 }, new CallOptions(DryRun: true));
        Asserts.That(pruned.Ok && pruned.Data!.KeepDays == 90,
            "audit.prune（dry_run）应回报保留天数且零副作用（dry_run 不消耗确认）");

        var first = (await s.Client.FolderCreateAsync("幂等目录", o: new CallOptions(IdempotencyKey: "smoke-idem-1"))).Data
            ?? throw new Exception("folders.create 未返回 FolderDto");
        var second = (await s.Client.FolderCreateAsync("幂等目录", o: new CallOptions(IdempotencyKey: "smoke-idem-1"))).Data
            ?? throw new Exception("folders.create 未返回 FolderDto");
        Asserts.That(second.FolderId == first.FolderId,
            "幂等键重复调用应返回首次结果");
        var idemRows = await ScalarAsync(db, "SELECT COUNT(*) FROM idempotency WHERE key = 'smoke-idem-1'");
        Asserts.That(idemRows == 1, "幂等结果应已落 idempotency 表");
        await db.DisposeAsync();

        // —— §10.11 logs.query / logs.level（S2b 日志读侧）：未装配如实报错 → 装配后能查到 ——
        var unwired = await AssertEngineErrorAsync(() => s.Client.QueryAsync<JsonElement>("logs.query"));
        Asserts.That(unwired.Error.Code == "LP.STATE.005",
            $"未装配日志管道时 logs.query 应报 LP.STATE.005，实际 {unwired.Error.Code}");
        var unwiredLevel = await AssertEngineErrorAsync(() => s.Client.ExecuteAsync<JsonElement>(
            "logs.level", new { level = "debug" }));
        Asserts.That(unwiredLevel.Error.Code == "LP.STATE.005",
            $"未装配日志管道时 logs.level 应报 LP.STATE.005，实际 {unwiredLevel.Error.Code}");

        var logsDir = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpsmoke_logs_{Guid.NewGuid():N}");
        LinkPocket.Composition.EngineComposer.ConfigureLogging(
            new LoggingOptions { Directory = logsDir, MinimumLevel = LogLevel.Info });
        try
        {
            LpLog.Info("冒烟：日志读侧", "smoke");
            var queried = await s.Client.QueryAsync<LogQueryResult>("logs.query", new { category = "smoke" });
            Asserts.That(queried.Items.Any(r => r.Message == "冒烟：日志读侧"), "logs.query 内存源应读到刚写的记录");
            Asserts.That(queried.MinimumLevel == LogLevel.Info && queried.Source == LogSource.Memory,
                "logs.query 应回显当前最低级别与读取来源");

            var leveled = await s.Client.ExecuteAsync<LinkPocket.Modules.Maintenance.LogsLevelResult>(
                "logs.level", new { level = "debug" });
            Asserts.That(leveled.Ok && leveled.Data!.Level == "debug" && leveled.Data!.Previous == "info",
                "logs.level 应回报切换前后的级别");

            var fromFile = await s.Client.QueryAsync<LogQueryResult>("logs.query", new { source = "file" });
            Asserts.That(fromFile.Source == LogSource.File && fromFile.FilesRead >= 1
                         && fromFile.Items.Any(r => r.Message == "冒烟：日志读侧"),
                "logs.query source=file 应回读刚写入的日志文件");
        }
        finally
        {
            LpLog.Shutdown();
            try { Directory.Delete(logsDir, recursive: true); } catch { /* 冒烟清理尽力而为 */ }
        }

        Console.WriteLine("[OK] §10 编排层：批(事务回滚/continue/dry_run/status/ref 模板) + 宏 + 撤销重做 + Staging + 诊断 + 审计/幂等落表 + 日志读侧");
    }

    private static async Task<long> ScalarAsync(Microsoft.Data.Sqlite.SqliteConnection db, string sql)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result ?? 0L);
    }

    /// <summary>断言批以 BatchAborted 失败并返回异常（Asserts 无泛型 Throws 助手，就地实现）。</summary>
    private static async Task<EngineException> AssertBatchAbortedAsync(SmokeState s, BatchScript script)
    {
        try
        {
            await s.Client.RunBatchAsync(script);
        }
        catch (EngineException ex)
        {
            return ex;
        }
        throw new Exception("断言失败: 预期 BatchAborted，实际批执行成功");
    }

    /// <summary>断言调用以 EngineException 失败并返回异常。</summary>
    private static async Task<EngineException> AssertEngineErrorAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (EngineException ex)
        {
            return ex;
        }
        throw new Exception("断言失败: 预期 EngineException，实际调用成功");
    }
}
