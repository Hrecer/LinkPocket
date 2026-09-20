using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Backup;

/// <summary>
/// backup.import（Mutation · Destructive 两阶段确认 · FileIo）：从 .lpbackup 导入。
/// replace = false：追加导入；replace = true：先清空全部数据（含回收站）再导入（= 完全重置，走
/// <see cref="IUnitOfWork.ClearAllDataAsync"/> 批量清空，自引用 FK 由 defer_foreign_keys 事务语义兜底）。
/// </summary>
/// <remarks>
/// <para>
/// <b>清空 + 导入 = 同一个显式事务</b>（用户令 2026-09-20："确保数据是安全的"）。
/// ⚠️ 这条曾经是**假的**：引擎管道只在干跑时开事务（`EngineCore` 的 <c>dryRun ? uow.BeginTransaction() : null</c>），
/// 而非干跑的导入**没有外层事务**；<c>ClearAllDataAsync</c> 在没有外层事务时会**自建事务并当场提交**
/// ——于是"清空"与"导入"是两个独立事务：清空已落库之后若导入被取消/失败（磁盘满、进程被杀、用户点取消），
/// 用户的旧数据**已经永久没了**，新数据又没进来，且没有任何回滚。现在由本处理器显式开事务包住两步。
/// </para>
/// <para>
/// <b>校验先于写库</b>：外部输入的全部校验（重复 key / 悬空引用 / 循环引用 / 时间戳）都在开事务与清空**之前**完成。
/// </para>
/// </remarks>
internal sealed class BackupImportHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "backup.import",
        Category: "backup",
        Description: "从 .lpbackup 导入（replace=false 追加；replace=true 先清空全部数据再导入，两阶段确认）",
        Parameters:
        [
            ParamSpec.Req<string>("file_path", "备份文件路径"),
            ParamSpec.Opt<bool>("replace", "true = 清空后导入（完全重置）；缺省 = 追加导入"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Destructive | CommandCaps.FileIo | CommandCaps.LongRunning | CommandCaps.SupportsCancellation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var filePath = CommandArgs.RequireString(args, "file_path");
        var replace = CommandArgs.OptionalBool(args, "replace");
        var ct = ctx.Ct;
        var uow = ctx.Uow;

        var file = await BackupIO.ReadAsync(filePath, ct);
        if (!file.Valid)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath, string.Join("; ", file.Errors), correlationId: ctx.CorrelationId));

        // —— 深层计算 + 外部输入校验（未通过之前**一个字节都不写库**）——
        var folders = file.Data.Folders ?? [];
        var links = file.Data.Links ?? [];

        // 备份文件是外部输入：重复 key / 空 key / 未知父级一律报明确错误，绝不当成"落在根级"静默吞掉
        // （用户令 2026-09-20："确保数据是安全的"；零兼容红线：拿不准就报错，不猜意图、不顺手修正）。
        // 旧实现在这里用 `TryGetValue(...) ? parentId : null`、`... ? listId : null` 静默回落根级 —— 那是
        // **静默的层级损坏**：用户的目录树会被悄悄拍平，且没有任何提示。现在整包拒绝（事务尚未开始，库里什么都没动）。
        var folderByKey = new Dictionary<string, BackupIO.BackupFolderData>(StringComparer.Ordinal);
        foreach (var f in folders)
        {
            if (string.IsNullOrWhiteSpace(f.Key))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.InvalidPath, "备份文件包含空的文件夹 key，无法导入", correlationId: ctx.CorrelationId));
            if (!folderByKey.TryAdd(f.Key, f))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.InvalidPath, $"备份文件包含重复的文件夹 key「{f.Key}」，无法导入",
                    correlationId: ctx.CorrelationId));
        }

        var dangling = folders.Where(f => f.Parent != null && !folderByKey.ContainsKey(f.Parent))
            .Select(f => $"「{f.Name}」→ 未知父级 key `{f.Parent}`")
            .Concat(links.Where(l => l.Folder != null && !folderByKey.ContainsKey(l.Folder))
                .Select(l => $"书签「{l.Title ?? l.Url}」→ 未知目录 key `{l.Folder}`"))
            .Take(5)
            .ToList();
        if (dangling.Count > 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath,
                "备份文件的引用不完整（若有目录在导出后被拆分/篡改，请改用完整备份）：" + string.Join("；", dangling),
                correlationId: ctx.CorrelationId));

        // 时间戳：**有值但解析不了 = 拒绝整包**（旧实现静默回落 DateTime.UtcNow = 把损坏数据伪装成"刚刚创建"）。
        // 字段缺失（备份格式允许省略）则显式取当下时间——这是"当时就是不知道"，不是伪造。
        var now = DateTime.UtcNow;
        var unresolvedTime = folders
            .SelectMany(f => new[] { ("文件夹", f.Name, "created_at", f.CreatedAt), ("文件夹", f.Name, "updated_at", f.UpdatedAt) })
            .Concat(folders.Select(f => ("文件夹", f.Name, "last_visited_at", f.LastVisitedAt ?? "")))
            .Concat(links.SelectMany(l => new[]
            {
                ("书签", l.Title ?? l.Url, "created_at", l.CreatedAt),
                ("书签", l.Title ?? l.Url, "updated_at", l.UpdatedAt),
                ("书签", l.Title ?? l.Url, "last_visited_at", l.LastVisitedAt ?? ""),
            }))
            .Where(t => !BackupIO.TryParseUtc(t.Item4, out _))
            .Select(t => $"{t.Item1}「{t.Item2}」的 {t.Item3}=\"{t.Item4}\"")
            .Take(5)
            .ToList();
        if (unresolvedTime.Count > 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.InvalidPath,
                "备份文件含无法解析的时间戳（文件已损坏或被手工改动）：" + string.Join("；", unresolvedTime),
                correlationId: ctx.CorrelationId));

        var depthMemo = new Dictionary<string, int>(StringComparer.Ordinal);

        int DepthOf(string? key)
        {
            if (key == null) return -1;
            if (depthMemo.TryGetValue(key, out var known)) return known;

            var chain = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cur = key;
            while (cur != null && !depthMemo.ContainsKey(cur) && seen.Add(cur))
            {
                chain.Add(cur);
                if (!folderByKey.TryGetValue(cur, out var node)) { cur = null; break; }
                cur = node.Parent;
            }

            // 退出原因：null（到顶/未知父）/ 记忆命中 / seen 重复（环）
            if (cur != null && !depthMemo.ContainsKey(cur))
                // 外部输入的层级里有环 = **输入非法**（LP.VAL.004），不是引擎内部错误
                // （零兼容红线：拿不准的入参按真实值处理并报明确错误码，别贴 LP.SYS.003 的标签）
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.InvalidPath, "备份文件的文件夹层级存在循环引用，无法导入", correlationId: ctx.CorrelationId));

            var baseDepth = cur == null
                ? chain.Count
                : depthMemo[cur] + chain.Count;

            // 记忆化回填：链上各节点深度 = 基准递减（未知/到顶链末 = 1，越靠根越大，与拓扑序一致的相对量级）
            for (var i = 0; i < chain.Count; i++)
                depthMemo[chain[i]] = baseDepth - i;

            return baseDepth;
        }

        var sortedFolders = folders
            .Select((f, index) => (f, index, depth: DepthOf(f.Parent) + 1))
            .OrderBy(t => t.depth)
            .ThenBy(t => t.index)
            .ToList();

        // —— 显式事务边界：清空 + 建实体 + 计数回填 = 一次提交，任何失败整体回滚 ——
        // （引擎的**非干跑**路径不提供外层事务；不开这一步的话 ClearAllDataAsync 会自建事务当场提交，
        //   于是"清空已提交、导入未完成"就是一个真实的丢数据窗口。）
        //
        // ⚠️ **干跑**下引擎已经开过显式事务（`EngineCore` 的 `dryRun ? uow.BeginTransaction() : null`）：
        //    再开一层会被 EF/SQLite 拒绝（"does not support nested transactions"）→ 干跑变成 LP.SYS.001。
        //    因此本处理器只在自己**拥有**事务时才开/提交/回滚；干跑时只是"加入"引擎那个事务，
        //    由引擎在收尾时回滚（执行但不提交 = 干跑语义）。
        var ownsTransaction = !ctx.DryRun;
        ITransactionScope? tx = ownsTransaction ? uow.BeginTransaction() : null;
        if (tx is not null) await tx.BeginAsync(ct);   // 立刻开：清空用的 ExecuteDelete 绕过变更跟踪，没有活动事务就不会被回滚

        var foldersRenamed = 0;
        var foldersCreated = 0;
        var linksCreated = 0;
        var faviconUrls = new List<string>();
        try
        {
            // —— replace：清空全部数据（含回收站）。批量清空规避「逐条 Remove + 自引用 RESTRICT」的顺序炸点 ——
            // ⚠️ 位置很关键：放在**全部外部输入校验之后**（校验失败时用户数据一个字节都没动）。
            if (replace)
                await uow.ClearAllDataAsync(ct);

            // —— 同层唯一命名（Windows 口径）——
            // 备份是外部输入，且**增量模式**下会与既有数据共存：不编号会让 v4 唯一索引直接拒绝**整包**（导入永远失败）；
            // 备份文件内部也可能自带同层重名（来自旧库时代）。规则与粘贴/书签导入完全一致：撞名自动编号「名 (2)」。
            // 占用表 = 内存累积（同批内后面的项还不在库里，查库查不到）；根层先预置既有名（replace 已清空 → 自然为空）。
            var naming = await uow.Naming.CreateTableAsync(null, ct);

            // —— 文件夹（临时 key → 新实体 ID 映射）——
            var keyToFolderId = new Dictionary<string, string>();
            foreach (var t in sortedFolders)
            {
                var f = t.f;
                var resolvedName = naming.Resolve(f.Parent, f.Name);
                if (!string.Equals(resolvedName, f.Name, StringComparison.Ordinal)) foldersRenamed++;
                var folder = new Folder
                {
                    Name = resolvedName,
                    Description = f.Description,
                    ParentId = ResolveFolderKey(f.Parent, keyToFolderId, f.Name),
                    LinkCount = 0,
                    SortOrder = f.SortOrder,
                    VisitCount = f.VisitCount,
                    LastVisitedAt = BackupIO.TryParseUtc(f.LastVisitedAt, out var fVisited) ? fVisited : null,
                    CreatedAt = BackupIO.TryParseUtc(f.CreatedAt, out var fCreated) && fCreated is { } fc ? fc : now,
                    UpdatedAt = BackupIO.TryParseUtc(f.UpdatedAt, out var fUpdated) && fUpdated is { } fu ? fu : now,
                };
                _ = await uow.Folders.AddAsync(folder, ct);
                keyToFolderId[f.Key] = folder.FolderId;
                foldersCreated++;
            }

            // —— 书签 ——
            // `favicon_url` **原样先落库**（它是数据的一部分）；真正的缓存文件在**提交之后**才恢复
            // （事务外副作用不许跑在提交之前 —— 否则回滚后留下孤儿文件，见提交后的 favicon 段落）。
            foreach (var l in links)
            {
                ct.ThrowIfCancellationRequested();

                _ = await uow.Links.AddAsync(new Link
                {
                    Url = l.Url,
                    Title = l.Title,
                    Description = l.Description,
                    FaviconUrl = l.FaviconUrl,
                    ListId = ResolveFolderKey(l.Folder, keyToFolderId, l.Title ?? l.Url),
                    VisitCount = l.VisitCount,
                    IsImportant = l.IsImportant,
                    LastVisitedAt = BackupIO.TryParseUtc(l.LastVisitedAt, out var lVisited) ? lVisited : null,
                    CreatedAt = BackupIO.TryParseUtc(l.CreatedAt, out var lCreated) && lCreated is { } lc ? lc : now,
                    UpdatedAt = BackupIO.TryParseUtc(l.UpdatedAt, out var lUpdated) && lUpdated is { } lu ? lu : now,
                }, ct);
                if (!string.IsNullOrWhiteSpace(l.FaviconUrl)) faviconUrls.Add(l.FaviconUrl!);
                linksCreated++;
            }

            // —— 直接子链接计数回填 + 全部文件夹视为变动（既有 TouchAllModified 口径）。
            // ListAllAsync 是 AsNoTracking 快照，取 ID 后经 FindAsync 取跟踪态实体再改写。——
            var counts = await uow.Links.CountByFolderAsync(ct);
            var allFolderIds = (await uow.Folders.ListAllAsync(ct)).Select(f => f.FolderId)
                .Concat(keyToFolderId.Values)
                .Distinct();
            foreach (var folderId in allFolderIds)
            {
                var folder = await uow.Folders.FindAsync(new FolderId(folderId), ct);
                if (folder == null) continue;
                folder.LinkCount = counts.GetValueOrDefault(new FolderId(folder.FolderId));
                folder.UpdatedAt = DateTime.UtcNow;
            }

            await uow.CommitAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
        }
        catch
        {
            // 失败整体回滚（ClearAllDataAsync 复用本事务 → 被清空的旧数据会一起回来）。
            // ⚠️ 回滚**必须用 CancellationToken.None**：走到这里的原因极可能就是"用户/宿主取消了这次导入"，
            //    此时 ct 已取消 —— 把已取消的 token 传给 RollbackAsync 会让**回滚本身当场抛**，事务悬空。
            //    收尾动作（回滚 / 释放）一律不受业务取消令牌支配。
            if (tx is not null)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackEx)
                {
                    // 回滚失败只记日志，绝不顶替原始异常（观测面红线）
                    LpLog.Warn("备份导入失败后的回滚也失败了（请检查数据库文件）", rollbackEx, category: "modules.backup");
                }
            }
            throw;
        }
        finally
        {
            // 只处置自己开的事务（干跑时引擎那个由引擎处置）
            if (tx is not null) await tx.DisposeAsync();
        }

        // —— 图标缓存文件：**事务提交成功之后**才落盘（事务外副作用不许跑在提交之前）——
        // 干跑不落盘（执行但不提交）；写失败照抛，但**如实告知"数据已导入、只是图标没恢复"**
        // （数据已经提交，绝不能因为图标写不进去就把整个导入说成失败；也绝不静默吞掉）。
        var faviconWarnings = new List<string>();
        var faviconsRestored = 0;
        if (!ctx.DryRun && file.Favicons is { Count: > 0 })
        {
            foreach (var faviconUrl in faviconUrls.Distinct(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await BackupIO.RestoreFaviconFileAsync(faviconUrl, file.Favicons, ct);
                    faviconsRestored++;
                }
                catch (Exception favEx)
                {
                    faviconWarnings.Add($"{faviconUrl}：{favEx.GetBaseException().Message}");
                    LpLog.Warn($"备份导入后恢复图标失败（数据已导入）：{faviconUrl}", favEx, category: "modules.backup");
                }
            }
        }

        var events = new List<string> { LinkPocket.Contracts.DomainEventNames.LinksChanged, LinkPocket.Contracts.DomainEventNames.FoldersChanged };
        if (replace) events.Add(LinkPocket.Contracts.DomainEventNames.TrashChanged);

        return CommandResult.Ok(
            JsonSerializer.SerializeToElement(new
            {
                folders_created = foldersCreated,
                folders_renamed = foldersRenamed,
                links_created = linksCreated,
                total_items = foldersCreated + linksCreated,
                replace,
                favicons_restored = faviconsRestored,
                favicon_warnings = faviconWarnings,
            }),
            new ChangeSet(
                Touched: [new EntityRef("database", "*")],
                Events: events,
                HumanSummary: $"已导入 {foldersCreated} 个文件夹、{linksCreated} 个书签"
                               + (foldersRenamed > 0 ? $"（{foldersRenamed} 个同名已自动编号）" : "")
                               + (replace ? "（清空后导入）" : "")
                               + (faviconWarnings.Count > 0 ? $"（{faviconWarnings.Count} 个图标未恢复）" : ""),
                // 图标写失败如实上抛给调用方（数据已提交，但"有东西没做完"必须让用户看见）
                Warnings: faviconWarnings.Count > 0 ? faviconWarnings : null));
    }

    /// <summary>
    /// 包内文件夹 key → 本库文件夹 ID；<c>null</c> = 根级。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这里**刻意不写"取不到就落根级"的回落分支**：那正是"静默拍平用户的目录树"的形状
    /// （校验已经保证不可达，但只要哪天有人放宽校验或调整排序，同一行代码立刻重新变成静默损坏）。
    /// 拿不准就抛——这是"漏网的输入非法"，不是"这个文件夹没有父"。
    /// </remarks>
    private static string? ResolveFolderKey(
        string? key, IReadOnlyDictionary<string, string> created, string owner)
        => key is null
            ? null
            : created.TryGetValue(key, out var id)
                ? id
                : throw new EngineException(EngineErrors.Of(
                    EngineErrors.InvalidPath,
                    $"备份文件里「{owner}」指向未知目录 key「{key}」（本该在导入前被校验拦下）"));
}