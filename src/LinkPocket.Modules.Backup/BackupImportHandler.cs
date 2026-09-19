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
/// 导入整体为引擎单事务（拓扑序父先于子），任何一步失败全部回滚；导入后全部文件夹视为变动。
/// </summary>
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

        // —— replace：清空全部数据（含回收站）。批量清空规避「逐条 Remove + 自引用 RESTRICT」的顺序炸点 ——
        if (replace)
            await uow.ClearAllDataAsync(ct);

        // —— 深度计算（带记忆化 + 循环防护）：父先于子 ——
        var folders = file.Data.Folders ?? [];
        var links = file.Data.Links ?? [];

        // 备份文件是外部输入：重复/空 key 必须报明确的输入错误，不能冒成内部异常
        var folderByKey = new Dictionary<string, BackupIO.BackupFolderData>(StringComparer.Ordinal);
        foreach (var f in folders)
        {
            if (!folderByKey.TryAdd(f.Key, f))
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.InvalidPath, $"备份文件包含重复的文件夹 key「{f.Key}」，无法导入",
                    correlationId: ctx.CorrelationId));
        }

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
                throw new EngineException(EngineErrors.Of(
                    EngineErrors.Internal, "备份文件的文件夹层级存在循环引用，无法导入", correlationId: ctx.CorrelationId));

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

        // —— 同层唯一命名（Windows 口径）——
        // 备份是外部输入，且**增量模式**下会与既有数据共存：不编号会让 v4 唯一索引直接拒绝**整包**（导入永远失败）；
        // 备份文件内部也可能自带同层重名（来自旧库时代）。规则与粘贴/书签导入完全一致：撞名自动编号「名 (2)」。
        // 占用表 = 内存累积（同批内后面的项还不在库里，查库查不到）；根层先预置既有名（replace 已清空 → 自然为空）。
        var naming = await uow.Naming.CreateTableAsync(null, ct);
        var foldersRenamed = 0;

        // —— 文件夹（临时 key → 新实体 ID 映射）——
        var keyToFolderId = new Dictionary<string, string>();
        var foldersCreated = 0;
        foreach (var t in sortedFolders)
        {
            var f = t.f;
            var resolvedName = naming.Resolve(f.Parent, f.Name);
            if (!string.Equals(resolvedName, f.Name, StringComparison.Ordinal)) foldersRenamed++;
            var folder = new Folder
            {
                Name = resolvedName,
                Description = f.Description,
                ParentId = f.Parent != null && keyToFolderId.TryGetValue(f.Parent, out var parentId) ? parentId : null,
                LinkCount = 0,
                SortOrder = f.SortOrder,
                VisitCount = f.VisitCount,
                LastVisitedAt = BackupIO.ParseNullableDateTime(f.LastVisitedAt),
                CreatedAt = BackupIO.ParseDateTime(f.CreatedAt),
                UpdatedAt = BackupIO.ParseDateTime(f.UpdatedAt),
            };
            _ = await uow.Folders.AddAsync(folder, ct);
            keyToFolderId[f.Key] = folder.FolderId;
            foldersCreated++;
        }

        // —— 书签（图标缓存文件随包恢复）——
        var linksCreated = 0;
        foreach (var l in links)
        {
            ct.ThrowIfCancellationRequested();
            string? resolvedFaviconUrl = null;
            if (!string.IsNullOrWhiteSpace(l.FaviconUrl))
                resolvedFaviconUrl = await BackupIO.RestoreFaviconFileAsync(l.FaviconUrl, file.Favicons, ct);

            _ = await uow.Links.AddAsync(new Link
            {
                Url = l.Url,
                Title = l.Title,
                Description = l.Description,
                FaviconUrl = resolvedFaviconUrl,
                ListId = l.Folder != null && keyToFolderId.TryGetValue(l.Folder, out var listId) ? listId : null,
                VisitCount = l.VisitCount,
                IsImportant = l.IsImportant,
                LastVisitedAt = BackupIO.ParseNullableDateTime(l.LastVisitedAt),
                CreatedAt = BackupIO.ParseDateTime(l.CreatedAt),
                UpdatedAt = BackupIO.ParseDateTime(l.UpdatedAt),
            }, ct);
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
            }),
            new ChangeSet(
                Touched: [new EntityRef("database", "*")],
                Events: events,
                HumanSummary: $"已导入 {foldersCreated} 个文件夹、{linksCreated} 个书签"
                               + (foldersRenamed > 0 ? $"（{foldersRenamed} 个同名已自动编号）" : "")
                               + (replace ? "（清空后导入）" : "")));
    }
}