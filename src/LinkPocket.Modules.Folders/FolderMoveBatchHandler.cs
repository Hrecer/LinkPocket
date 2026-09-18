using LinkPocket.Data;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Modules.Folders;

/// <summary>
/// folders.move_batch（★ 引擎能力，不接 UI）：原子移动多个文件夹。
/// 全部校验（目标存在性 + 成环 + 批次内祖先-后代冲突）先于任何变更；目标目录下同名经 <see cref="INamingPolicy"/> 自动编号。
/// </summary>
internal sealed class FolderMoveBatchHandler : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "folders.move_batch",
        Category: "folders",
        Description: "批量移动文件夹（原子：任一校验失败整批不动；目标目录同名自动编号「名 (2)」）",
        Parameters:
        [
            ParamSpec.Req<IReadOnlyList<string>>("folder_ids", "要移动的文件夹 ID 列表"),
            ParamSpec.Opt<string>("target_parent_id", "目标父目录 ID；缺省 = 根级"),
        ],
        Caps: CommandCaps.Mutation | CommandCaps.Reversible);   // 可撤销；逆向参数由处理器回填（每项一步）

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var folderIds = CommandArgs.StringArray(args, "folder_ids");
        if (folderIds.Count == 0)
            throw new EngineException(EngineErrors.Of(
                EngineErrors.RequiredParam, "folder_ids 不能为空", correlationId: ctx.CorrelationId));
        var target = CommandArgs.OptionalString(args, "target_parent_id");
        var ct = ctx.Ct;
        var uow = ctx.Uow;

        // —— 校验阶段（零副作用承诺：全部通过才开始变更）——
        // 目标存在性先行（与 folders.create/move 同序）：避免后续逐条成环检查全部白跑
        if (target != null)
            _ = await uow.Folders.FindAsync(new FolderId(target), ct)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"目标文件夹 {target} 不存在", correlationId: ctx.CorrelationId));

        var allFolders = await uow.Folders.ListAllAsync(ct);
        var movedFolders = new List<Folder>();

        // 批次内祖先-后代同行拒绝：移动后层级无法确定（后代会被从祖先子树抽走）→ 明确报错，不改语义猜测
        var batchSet = folderIds.ToHashSet(StringComparer.Ordinal);
        foreach (var fid in folderIds)
        {
            var cur = allFolders.FirstOrDefault(f => f.FolderId == fid)?.ParentId;
            while (cur != null)
            {
                if (batchSet.Contains(cur))
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.CycleDetected,
                        $"folder_ids 同时包含「{fid}」及其祖先「{cur}」：批次内存在父子关系，无法原子移动",
                        correlationId: ctx.CorrelationId));
                cur = allFolders.FirstOrDefault(f => f.FolderId == cur)?.ParentId;
            }
        }

        foreach (var fid in folderIds)
        {
            var folder = allFolders.FirstOrDefault(f => f.FolderId == fid)
                ?? throw new EngineException(EngineErrors.Of(
                    EngineErrors.EntityNotFound, $"文件夹 {fid} 不存在", correlationId: ctx.CorrelationId));
            if (target != null)
            {
                if (target == fid)
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.CycleDetected, $"不能把文件夹 {fid} 移入它自己", correlationId: ctx.CorrelationId));
                if (await uow.Trees.WouldCreateCycleAsync(new FolderId(fid), new FolderId(target), ct))
                    throw new EngineException(EngineErrors.Of(
                        EngineErrors.CycleDetected, $"把「{folder.Name}」移动到 {target} 会产生循环引用", correlationId: ctx.CorrelationId));
            }

            movedFolders.Add(folder);
        }

        // —— 同名自动编号：目标层已占用名 − 本次移出同名 → 逐个解析 ——
        var previousParentIds = movedFolders.Select(f => f.ParentId).ToHashSet(StringComparer.Ordinal);
        var renamedNotes = new List<string>();
        if (movedFolders.Count > 0)
        {
            var policy = WindowsNamingPolicy.Instance;
            // 占用名集合的比较器必须取自策略本身（命名口径只能有一处）：曾用 CurrentCulture（大小写敏感），
            // 与 DB 唯一索引（NOCASE）口径不一致 → 策略放行而索引拒绝。
            var siblings = (await uow.Folders.ChildrenOfAsync(
                    target == null ? null : new FolderId(target), ct))
                .Where(f => !movedFolders.Any(m => m.FolderId == f.FolderId))
                .Select(f => f.Name)
                .ToHashSet(policy.Comparer);

            foreach (var folder in movedFolders)
            {
                var resolved = policy.Resolve(folder.Name, siblings);
                if (!string.Equals(resolved, folder.Name, StringComparison.Ordinal))
                {
                    renamedNotes.Add($"「{folder.Name}」→「{resolved}」");
                    folder.Name = resolved;
                }

                siblings.Add(resolved);
            }
        }

        // —— 变更阶段 ——
        // 旧父快照必须在变更**之前**取（撤销载荷要带旧值；变更后再读已是新值）
        var oldParents = movedFolders.ToDictionary(f => f.FolderId, f => f.ParentId, StringComparer.Ordinal);
        foreach (var folder in movedFolders)
        {
            folder.ParentId = target;
            folder.UpdatedAt = DateTime.UtcNow;
            await uow.Folders.UpdateAsync(folder, ct);   // ListAllAsync 是分离实体，必须显式登记修改
        }

        // 新旧父链内容变化
        foreach (var parent in previousParentIds)
            await uow.Trees.TouchModifiedAsync(parent == null ? null : new FolderId(parent), ct);
        await uow.Trees.TouchModifiedAsync(target == null ? null : new FolderId(target), ct);

        var summary = $"已移动 {movedFolders.Count} 个文件夹" + (renamedNotes.Count > 0 ? $"（重命名：{string.Join("、", renamedNotes)}）" : "");

        // 撤销载荷：**每项一步**（各文件夹的旧父可能不同）——逆向 = 各自移回原父。
        // 注意：批量路径的"同名自动编号"改名不在撤销范围内（改名本身不可撤销，见契约）。
        var undo = movedFolders
            .Where(f => oldParents[f.FolderId] != target)
            .Select(f => new UndoInverseStep("folders.move",
                JsonSerializer.SerializeToElement(new { folder_id = f.FolderId, target_parent_id = oldParents[f.FolderId] })))
            .ToList();

        return CommandResult.Ok(
            new FolderMoveBatchResult(movedFolders.Count, renamedNotes),
            new ChangeSet(
                Touched: movedFolders.Select(f => new EntityRef("folder", f.FolderId)).ToList(),
                Events: [LinkPocket.Contracts.DomainEventNames.FoldersChanged],
                HumanSummary: summary),
            undo.Count > 0 ? undo : null);
    }
}