using System.Diagnostics;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Engine;

/// <summary>
/// 编排命令处理器集：15 个注册命令
/// （macro.* 5 / undo.* 4 / staging.* 6）。
/// batch.run / batch.dry_run / batch.status 不注册——由 wire 层直路由 <see cref="BatchEngine"/>；
/// diagnostics.collect 归属 Maintenance 模块（总表），引擎侧诊断数据经其 ctx.Uow 取数。
/// 逆向命令执行统一走嵌套派发（与撤销命令同事务、同审计父条目）。
/// </summary>
internal static class OrchestrationHandlers
{
    public static IReadOnlyList<ICommandHandler> CreateAll(
        IMacroStore macros, UndoCoordinator undo, StagingService staging, EngineLimits limits)
        =>
        [
            new MacroSaveHandler(macros, limits), new MacroGetHandler(macros), new MacroListHandler(macros),
            new MacroDeleteHandler(macros), new MacroRunHandler(macros, limits),
            new UndoListHandler(undo), new UndoListRedoHandler(undo), new UndoUndoHandler(undo), new UndoRedoHandler(undo),
            new UndoClearHandler(undo),
            new StagingStageHandler(staging), new StagingListHandler(staging), new StagingDiscardHandler(staging),
            new StagingInspectHandler(staging), new StagingTransformHandler(staging), new StagingCommitHandler(staging),
        ];
}

// ===== macro.* =====

internal sealed class MacroSaveHandler(IMacroStore macros, EngineLimits limits) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "macro.save", Category: "macro", Description: "Save a named batch script (macro / skill library; invalid scripts are rejected)",
        Parameters: [ParamSpec.Req<string>("name", "Macro name"),
            ParamSpec.Req<JsonElement>("script", "Batch script (BatchScript JSON)", schema: ParamSchemas.BatchScript)],
        Caps: CommandCaps.Mutation,
        Client: new("MacroSaveRawAsync", typeof(JsonElement)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = CommandArgs.RequireString(args, "name");
        var script = CommandArgs.Raw(args, "script")
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
                "required parameter 'script' is missing", details: JsonSerializer.SerializeToElement(new { @param = "script" })));

        // 落表前全量校验（形状 / 步骤上限）：描述符承诺"非法脚本被拒"，校验必须在写入前完成（校验类错误零副作用）
        var parsed = BatchDispatch.ParseScript(script);
        BatchEngine.ValidateScript(parsed, limits);

        // 干跑：**只校验、不落表**。宏走的是 IMacroStore 自己的连接（不在引擎事务内），
        // 真写下去就等于"干跑改了库"——违反不变量 3（干跑执行但不提交、零副作用）。
        if (ctx.DryRun)
            return CommandResult.Ok(JsonSerializer.SerializeToElement(name),
                ChangeSet.Of(new EntityRef("macro", name), DomainEventNames.MacroSaved, $"(dry run) would save macro '{name}'"));

        await macros.SaveAsync(name, script.GetRawText(), ctx.Ct);
        return CommandResult.Ok(JsonSerializer.SerializeToElement(name),
            ChangeSet.Of(new EntityRef("macro", name), DomainEventNames.MacroSaved, $"Macro '{name}' saved"));
    }
}

internal sealed class MacroGetHandler(IMacroStore macros) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "macro.get", Category: "macro", Description: "Read the batch script definition of a macro",
        Parameters: [ParamSpec.Req<string>("name", "Macro name")], Caps: CommandCaps.Query,
        Client: new("MacroGetAsync", typeof(JsonElement)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = CommandArgs.RequireString(args, "name");
        var json = await macros.GetAsync(name, ctx.Ct)
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"macro '{name}' does not exist"));
        return CommandResult.Ok(JsonSerializer.Deserialize<JsonElement>(json));
    }
}

internal sealed class MacroListHandler(IMacroStore macros) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "macro.list", Category: "macro", Description: "List all macros (name + updated time, without the script body)",
        Parameters: [], Caps: CommandCaps.Query,
        Client: new("MacroListAsync", typeof(JsonElement)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var all = await macros.ListAsync(ctx.Ct);
        var list = all.Select(m => new { name = m.Name, updated_at = m.UpdatedAt });
        return CommandResult.Ok(JsonSerializer.SerializeToElement(new { macros = list }, EngineJson.Options));
    }
}

internal sealed class MacroDeleteHandler(IMacroStore macros) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "macro.delete", Category: "macro", Description: "Delete a macro",
        Parameters: [ParamSpec.Req<string>("name", "Macro name")], Caps: CommandCaps.Mutation,
        Client: new("MacroDeleteAsync", typeof(JsonElement)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = CommandArgs.RequireString(args, "name");

        // 干跑：**只校验存在性、不删**（宏走 IMacroStore 自己的连接，不在引擎事务里；
        // 真删下去 = 干跑改了库，违反不变量 3）。存在性照常校验，保证干跑与真跑的错误语义一致。
        if (ctx.DryRun)
        {
            if (await macros.GetAsync(name, ctx.Ct) is null)
                throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"macro '{name}' does not exist"));
            return CommandResult.Ok(JsonSerializer.SerializeToElement(name),
                ChangeSet.Of(new EntityRef("macro", name), DomainEventNames.MacroDeleted, $"(dry run) would delete macro '{name}'"));
        }

        if (!await macros.DeleteAsync(name, ctx.Ct))
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"macro '{name}' does not exist"));
        return CommandResult.Ok(JsonSerializer.SerializeToElement(name),
            ChangeSet.Of(new EntityRef("macro", name), DomainEventNames.MacroDeleted, $"Macro '{name}' deleted"));
    }
}

internal sealed class MacroRunHandler(IMacroStore macros, EngineLimits limits) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "macro.run", Category: "macro", Description: "Run a macro with transactional batch semantics (nested step dispatch shares this command's unit of work; abort rolls the whole batch back)",
        Parameters: [ParamSpec.Req<string>("name", "Macro name")],
        Caps: CommandCaps.Mutation | CommandCaps.LongRunning | CommandCaps.SupportsCancellation,
        Client: new("MacroRunAsync", typeof(JsonElement)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = CommandArgs.RequireString(args, "name");
        var scriptJson = await macros.GetAsync(name, ctx.Ct)
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"macro '{name}' does not exist"));
        BatchScript script;
        try
        {
            script = JsonSerializer.Deserialize<BatchScript>(scriptJson, EngineJson.ScriptOptions)
                ?? throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed, $"the script of macro '{name}' is not a valid batch script"));        }
        catch (JsonException)
        {
            // 坏 JSON 是输入问题而非内部错误：必须报 ProtocolMalformed，不得冒泡成 LP.INTERNAL
            throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed, $"the script of macro '{name}' is not a valid batch script"));
        }

        // 运行前再校验一遍形状与步骤上限（保存期已验过——存量宏与手改库里的脚本也要过这道闸）
        BatchEngine.ValidateScript(script, limits);

        // 宏实际运行耗时（此前 ElapsedMs 恒为 0，诊断面丢失「宏跑了多久」）
        var sw = Stopwatch.StartNew();
        var context = (CommandContextImpl)ctx;
        // 宏的批运行键 = 自身 correlation（与 BatchReport.BatchId 同口径）：
        // 步骤审计与 macro.run 父条目共用它，audit.query {batch_id} 即可取齐整次宏运行。
        context.BatchId ??= context.CorrelationId;
        // 宏的步骤循环同样"每步 flush"（见 <see cref="BatchEngine.RunStepsNestedAsync"/>）——flush 只写
        // 事务、不提交，"abort 整体回滚"的承诺必须由这里的显式事务兜住（否则中途失败时，前面步骤的
        // flush 已经落库）。**干跑不重开**：管道（EngineCore 的干跑路径）已开显式事务并在最后回滚，
        // 跟着它走即可；非干跑管道没有事务，由这里开、成功后提交。
        // 持事务期间的嵌套审计先缓冲（独立短连接会撞写锁）：**收口后由管道统一写出**
        //（EngineCore.ExecuteAsync 的 finally），这里只挂上（见 CommandContextImpl.NestedAuditBuffer）。
        context.NestedAuditBuffer = new List<AuditEntry>();
        var tx = ctx.DryRun ? null : ctx.Uow.BeginTransaction();
        List<BatchStepResult> results;
        List<EntityRef> touched;
        List<string> events;
        List<FieldChange> diff;
        try
        {
            if (tx != null) await tx.BeginAsync(ctx.Ct);
            (results, touched, events, diff) = await BatchEngine.RunStepsNestedAsync(
                context, script with { Name = $"macro:{name}" },
                context.UndoGroupId ?? $"macro:{Guid.NewGuid():N}", ctx.Ct);
            if (tx != null) await tx.CommitAsync(ctx.Ct);
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
        sw.Stop();

        var summary = $"Macro '{name}' finished: {results.Count(r => r.Ok)}/{results.Count} steps succeeded";
        var changes = new ChangeSet(touched, events, summary, Warnings: null, Diff: diff.Count > 0 ? diff : null);
        var report = new BatchReport(
            ctx.CorrelationId, $"macro:{name}", results.All(r => r.Ok), results,
            changes, summary, sw.ElapsedMilliseconds, ctx.CorrelationId);
        return CommandResult.Ok(BatchEngine.ToElement(report), changes);
    }
}

// ===== undo.* =====

/// <summary>
/// 撤销/重做命令的结果数据：全部步骤失败时嵌套结果为空，
/// 直接交给 <c>CommandResult&lt;JsonElement&gt;</c> 会把 null 强转成 JsonElement → NullReferenceException，
/// 把"失败并跳过"报成内部错误。空结果统一给 <c>{}</c>（形状稳定、可安全反序列化）。
/// </summary>
internal static class UndoResultData
{
    internal static JsonElement Safe(object? data)
        => BatchEngine.ToElement(data) ?? JsonSerializer.Deserialize<JsonElement>("{}");

    /// <summary>
    /// 整条记录（撤销的逆向步骤 / 重做的显式动作）是否都是"把链接还原到原位置"；
    /// 是则返回去重后的链接 ID（批量还原可以合并为一次 restore_batch）。
    /// 任何一步不是 <c>trash.restore</c> / 非 origin 落点 → null（走逐步派发的通用路径）。
    /// </summary>
    internal static List<string>? TryCollectLinkRestores(IEnumerable<(string Command, JsonElement Args)> actions)
    {
        var ids = new List<string>();
        foreach (var (command, args) in actions)
        {
            if (!string.Equals(command, "trash.restore", StringComparison.Ordinal)) return null;
            if (args.ValueKind != JsonValueKind.Object) return null;
            if (args.TryGetProperty("to", out var to)
                && to.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && !string.Equals(to.GetString(), "origin", StringComparison.Ordinal)) return null;
            if (!args.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(id.GetString())) return null;
            ids.Add(id.GetString()!);
        }
        var distinct = ids.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count > 1 ? distinct : null;
    }

    /// <summary>
    /// 整条记录是否都是"把单条链接移入回收站"（<c>links.trash</c>）；
    /// 是则返回去重后的链接 ID（可合并为一次 links.trash_batch）。
    /// </summary>
    internal static List<string>? TryCollectLinkTrashes(IEnumerable<(string Command, JsonElement Args)> actions)
    {
        var ids = new List<string>();
        foreach (var (command, args) in actions)
        {
            if (!string.Equals(command, "links.trash", StringComparison.Ordinal)) return null;
            if (args.ValueKind != JsonValueKind.Object) return null;
            if (!args.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(id.GetString())) return null;
            ids.Add(id.GetString()!);
        }
        var distinct = ids.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count > 1 ? distinct : null;
    }
}

internal sealed class UndoListHandler(IUndoCoordinator undo) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "undo.list", Category: "undo", Description: "List the undo stack (newest first, capped at 100 entries)",
        Parameters: [], Caps: CommandCaps.Query,
        Client: new("UndoListAsync", typeof(JsonElement)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var entries = await undo.ListAsync(ctx.Ct);
        return CommandResult.Ok(JsonSerializer.SerializeToElement(new { entries }, EngineJson.Options));
    }
}

internal sealed class UndoListRedoHandler(IUndoCoordinator undo) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "undo.list_redo", Category: "undo", Description: "List the redo stack (newest first, capped at 100 entries)",
        Parameters: [], Caps: CommandCaps.Query,
        Client: new("UndoListRedoAsync", typeof(JsonElement)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var entries = await undo.ListRedoAsync(ctx.Ct);
        return CommandResult.Ok(JsonSerializer.SerializeToElement(new { entries }, EngineJson.Options));
    }
}

internal sealed class UndoUndoHandler(UndoCoordinator undo) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "undo.undo", Category: "undo", Description: "Undo the most recent undoable action (a multi-step record rewinds in reverse; the popped entry moves to the redo stack)",
        Parameters: [ParamSpec.Opt<string>("id", "Undo entry ID (default = the most recent one)")],
        Caps: CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.OptionalString(args, "id");

        // 先取（不弹）再执行：逆向命令失败或事务回滚时条目必须仍在撤销栈（曾先 Take 后执行，
        // 失败即丢条目，用户无法重试）；执行成功后才弹栈并转入重做栈。
        // ⚠️ 必须用**带水合的 peek**（PeekUndoAsync），不能用 ListAsync：
        // 列表是**摘要视图**（大条目的步骤在日志分片里、Steps 为空）—— 从列表拿步骤会让
        // 1631 步的批量删除"一步都不跑却报 ok"（实测的假成功，用户："回溯成功了，文件夹还在回收站"）。
        var entry = await undo.PeekUndoAsync(id, ctx.Ct);
        if (entry is null)
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, "nothing to undo"));
        if (entry.Steps.Count == 0)
            // 步骤本应已就绪；到这里说明分片读不出来（外部删除/损坏）——**如实失败**，不许假成功
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound,
                "the undo record's steps are unavailable (journal payload missing)"));

        // 逆序回退：一次动作的多步（如一次粘贴多项）按**相反顺序**撤销，避免中途引用已消失的目标。
        // 单项失败不中断整批（与 UI 侧批量语义一致）：失败项如实记录并跳过，整条最终被消费
        // ——"报错并跳过"是定稿口径（绝不静默卡在同一条上让 Ctrl+Z 永远失败）。
        // 嵌套审计先缓冲，提交后由引擎统一批量落库（逐步独立连接写 = 大条目撤销的线性开销之一）。
        var ctxImpl = (CommandContextImpl)ctx;
        ctxImpl.NestedAuditBuffer = [];
        var failures = new List<string>();
        ChangeSet? lastChanges = null;
        object? lastData = null;
        var undoPhase = LpLog.IsEnabled(LogLevel.Debug) ? Stopwatch.StartNew() : null;
        var undoPhaseLast = 0L;
        void UndoPhase(string name)
        {
            if (undoPhase is null) return;
            var now = undoPhase.ElapsedMilliseconds;
            LpLog.Write(LogLevel.Debug, "engine.undo", $"undo phase={name} dt={now - undoPhaseLast}ms total={now}ms steps={entry.Steps.Count}");
            undoPhaseLast = now;
        }

        // 快速路径：整条记录都是"把链接还原到原位置"（批量删除的撤销）→ 合并为一次 restore_batch。
        // 200 步逐步派的往返开销（每步参数快照/审计/查询）是秒级卡顿的主因，合并后约降一个数量级；
        // 预检失败（快照丢失/主表冲突，写入前发生）回退逐条，保住"失败项跳过"的契约。
        var mergedRestoreIds = UndoResultData.TryCollectLinkRestores(
            entry.Steps.Select(s => (s.InverseCommand, s.InverseArgs)));
        var usedBatch = false;
        if (mergedRestoreIds is { Count: > 1 })
        {
            try
            {
                var result = await ctx.DispatchNestedAsync("trash.restore_batch",
                    new { link_ids = mergedRestoreIds, to = "origin" }, ctx.Ct);
                lastChanges = result.Changes;
                lastData = result.Data;
                usedBatch = true;
            }
            catch (EngineException ex) when (ex.Error.Code is EngineErrors.EntityNotFound or EngineErrors.DbError)
            {
                // 预检失败（某条快照已丢失 / 主表同 ID 冲突）发生在**任何写入之前**：
                // 回退逐条，保住"失败项跳过、其余照常还原"的既有契约（快速路径不能把整批拖下水）。
            }
            catch (Exception ex)
            {
                failures.Add($"trash.restore_batch: {ex.Message}");
                usedBatch = true;   // 非预检失败：不重试（可能已有部分 EF 登记）
            }
        }

        if (!usedBatch && failures.Count == 0)
        {
            var mergedTrashIds = UndoResultData.TryCollectLinkTrashes(
                entry.Steps.Select(s => (s.InverseCommand, s.InverseArgs)));
            if (mergedTrashIds is { Count: > 1 })
            {
                try
                {
                    var result = await ctx.DispatchNestedAsync("links.trash_batch",
                        new { link_ids = mergedTrashIds }, ctx.Ct);
                    lastChanges = result.Changes;
                    lastData = result.Data;
                    usedBatch = true;
                }
                catch (EngineException ex) when (ex.Error.Code is EngineErrors.EntityNotFound or EngineErrors.DbError)
                {
                    // 预检失败（某条链接已不存在）发生在写入前 → 回退逐条（失败项跳过）
                }
                catch (Exception ex)
                {
                    failures.Add($"links.trash_batch: {ex.Message}");
                    usedBatch = true;
                }
            }
        }

        if (!usedBatch && failures.Count == 0)
        {
            for (var i = entry.Steps.Count - 1; i >= 0; i--)
            {
                var step = entry.Steps[i];
                try
                {
                    var result = await ctx.DispatchNestedAsync(step.InverseCommand, step.InverseArgs, ctx.Ct);
                    lastChanges = result.Changes;
                    lastData = result.Data;
                }
                catch (Exception ex)
                {
                    failures.Add($"{step.InverseCommand}: {ex.Message}");
                }
            }
        }

        UndoPhase("steps");
        // 部分失败不转入重做栈（重放整条原始命令，对已成功回退的步骤会二次施加 → 状态错乱）
        if (failures.Count == 0)
            await undo.CompleteUndoAsync(entry.Id, ctx.Ct);          // 弹出 + 转重做 + 一次落盘
        else
            await undo.TakeUndoAsync(entry.Id, ctx.Ct);              // 失败项跳过，条目消费但不进重做栈

        UndoPhase("stack-persist");
        var summary = failures.Count == 0
            ? $"Undid {entry.Steps.Count} step(s)"
            : $"Undid {entry.Steps.Count - failures.Count} step(s), {failures.Count} failed (skipped)";
        return CommandResult.Ok(
            UndoResultData.Safe(lastData),
            new ChangeSet(
                lastChanges?.Touched ?? [],
                lastChanges?.Events ?? [],
                summary,
                failures.Count > 0 ? failures : null));
    }
}

internal sealed class UndoRedoHandler(UndoCoordinator undo) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "undo.redo", Category: "undo", Description: "Redo: replay the most recently undone action in forward order (a successful replay re-enters the undo stack)",
        Parameters: [], Caps: CommandCaps.Mutation,
        Client: new("RedoAsync", typeof(JsonElement)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        // 先取（不弹）再执行：失败时条目留在重做栈（同 undo.undo 的防丢语义）。
        // ⚠️ 与 undo.undo 同理：必须用**带水合的 peek**（ListRedoAsync 是摘要视图，大条目 Steps 为空 ⇒ 一步不跑却报 ok）。
        var entry = await undo.PeekRedoAsync(ctx.Ct);
        if (entry is null)
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, "nothing to redo"));
        if (entry.Steps.Count == 0)
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound,
                "the redo record's steps are unavailable (journal payload missing)"));


        // 正序重放（撤销时是逆序，重做对称回来）。重做动作 = 步骤显式给出者优先
        //（创建类必须显式：重放 links.create 会生成**新 ID**，原 ID 丢失且回收站留旧快照），否则重放原命令原参数。
        // 嵌套审计先缓冲（同 undo.undo）：提交后统一批量落库。
        ((CommandContextImpl)ctx).NestedAuditBuffer = [];
        var failures = new List<string>();
        ChangeSet? lastChanges = null;
        object? lastData = null;

        // 快速路径（与 undo.undo 对称）：整条记录重做 = 批量还原链接 → 合并为一次 restore_batch。
        // 300 步逐条重做实测 ~0.7s；合并后与批量撤销同一量级。预检失败同样回退逐条。
        var mergedRestoreIds = UndoResultData.TryCollectLinkRestores(
            entry.Steps.Select(s => (s.RedoAction.Command, s.RedoAction.Args)));
        var usedBatch = false;
        if (mergedRestoreIds is { Count: > 1 })
        {
            try
            {
                var result = await ctx.DispatchNestedAsync("trash.restore_batch",
                    new { link_ids = mergedRestoreIds, to = "origin" }, ctx.Ct);
                lastChanges = result.Changes;
                lastData = result.Data;
                usedBatch = true;
            }
            catch (EngineException ex) when (ex.Error.Code is EngineErrors.EntityNotFound or EngineErrors.DbError)
            {
                // 预检失败（快照缺失 / 主表冲突）发生在写入前 → 回退逐条（失败项跳过）
            }
            catch (Exception ex)
            {
                failures.Add($"trash.restore_batch: {ex.Message}");
                usedBatch = true;
            }
        }

        if (!usedBatch && failures.Count == 0)
        {
            var mergedTrashIds = UndoResultData.TryCollectLinkTrashes(
                entry.Steps.Select(s => (s.RedoAction.Command, s.RedoAction.Args)));
            if (mergedTrashIds is { Count: > 1 })
            {
                try
                {
                    var result = await ctx.DispatchNestedAsync("links.trash_batch",
                        new { link_ids = mergedTrashIds }, ctx.Ct);
                    lastChanges = result.Changes;
                    lastData = result.Data;
                    usedBatch = true;
                }
                catch (EngineException ex) when (ex.Error.Code is EngineErrors.EntityNotFound or EngineErrors.DbError)
                {
                    // 预检失败（某条链接已不存在）发生在写入前 → 回退逐条（失败项跳过）
                }
                catch (Exception ex)
                {
                    failures.Add($"links.trash_batch: {ex.Message}");
                    usedBatch = true;
                }
            }
        }

        if (!usedBatch && failures.Count == 0)
        {
            foreach (var step in entry.Steps)
            {
                var action = step.RedoAction;
                try
                {
                    var result = await ctx.DispatchNestedAsync(action.Command, action.Args, ctx.Ct);
                    lastChanges = result.Changes;
                    lastData = result.Data;
                }
                catch (Exception ex)
                {
                    failures.Add($"{action.Command}: {ex.Message}");
                }
            }
        }

        if (failures.Count > 0)
        {
            // 部分失败：消费条目但不重新入撤销栈（否则会留下"半重放"的可撤销记录，语义混乱）
            await undo.TakeRedoAsync(ctx.Ct);
            return CommandResult.Ok(UndoResultData.Safe(lastData),
                new ChangeSet(lastChanges?.Touched ?? [], lastChanges?.Events ?? [],
                    $"Redo failed for {failures.Count} step(s) (skipped)",
                    failures));
        }

        // 重新入撤销栈：直接复用原步骤（重做重放的是同一动作，其逆向不变），
        // 保留剩余重做链，且整条**只落盘一次**（原实现逐步 Record = 每条重做多次整索引写盘 + 清空剩余重做链）。
        var taken = await undo.CompleteRedoAsync(ctx.Caller, ctx.Ct);
        if (taken == null)
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, "nothing to redo"));

        return CommandResult.Ok(UndoResultData.Safe(lastData), lastChanges);
    }
}

internal sealed class UndoClearHandler(IUndoCoordinator undo) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "undo.clear", Category: "undo", Description: "Clear the undo and redo stacks",
        Parameters: [], Caps: CommandCaps.Mutation,
        Client: new("UndoClearAsync", typeof(int)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var count = await undo.ClearAsync(ctx.Ct);
        return CommandResult.Ok(count, ChangeSet.Of(new EntityRef("undo", "*"), DomainEventNames.UndoCleared, $"Cleared {count} undo/redo record(s)"));
    }
}

// ===== staging.* =====

internal sealed class StagingStageHandler(StagingService staging) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "staging.stage", Category: "staging", Description: "Copy a file into the AI staging area (registered by SHA-256 fingerprint)",
        Parameters: [ParamSpec.Req<string>("source_path", "Source file absolute path")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo,
        Client: new("StageAsync", typeof(StagedFile)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var source = CommandArgs.RequireString(args, "source_path");
        if (ctx.DryRun)
        {
            // 干跑零副作用：不拷贝、不登记（拷进暂存区就是落盘）；校验照做、影响面如实预告
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                throw new EngineException(EngineErrors.Of(EngineErrors.InvalidPath,
                    $"file does not exist: {source}", details: JsonSerializer.SerializeToElement(new { @param = "source_path" })));
            var info = new FileInfo(source);
            return CommandResult.Ok(
                JsonSerializer.SerializeToElement(
                    new { dry_run = true, source_path = source, file_name = info.Name, size_bytes = info.Length },
                    EngineJson.Options),
                ChangeSet.Of(new EntityRef("staged", "*"), DomainEventNames.StagingStaged, $"Would stage '{info.Name}'"));
        }

        var staged = await staging.StageAsync(source, ctx.Ct);
        return CommandResult.Ok(staged, ChangeSet.Of(new EntityRef("staged", staged.StagingId), DomainEventNames.StagingStaged, $"Staged '{staged.FileName}'"));
    }
}

internal sealed class StagingListHandler(StagingService staging) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "staging.list", Category: "staging", Description: "List staged files",
        Parameters: [], Caps: CommandCaps.Query,
        Client: new("StagingListAsync", typeof(JsonElement)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var files = await staging.ListAsync(ctx.Ct);
        return CommandResult.Ok(JsonSerializer.SerializeToElement(new { files }, EngineJson.Options));
    }
}

internal sealed class StagingDiscardHandler(StagingService staging) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "staging.discard", Category: "staging", Description: "Discard a staged file (deletes the copy and unregisters it)",
        Parameters: [ParamSpec.Req<string>("staging_id", "Staging ID")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "staging_id");
        if (ctx.DryRun)
        {
            // 干跑零副作用：不删暂存副本、不注销登记；存在性照查（校验错误零副作用照报）
            if ((await staging.ListAsync(ctx.Ct)).All(f => f.StagingId != id))
                throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"staged file does not exist: {id}"));
            return CommandResult.Ok(
                JsonSerializer.SerializeToElement(new { dry_run = true, staging_id = id }, EngineJson.Options),
                ChangeSet.Of(new EntityRef("staged", id), DomainEventNames.StagingDiscarded, "Would discard staged file"));
        }

        if (!await staging.DiscardAsync(id, ctx.Ct))
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"staged file does not exist: {id}"));
        return CommandResult.Ok(JsonSerializer.SerializeToElement(id),
            ChangeSet.Of(new EntityRef("staged", id), DomainEventNames.StagingDiscarded, "Staged file discarded"));
    }
}

internal sealed class StagingInspectHandler(StagingService staging) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "staging.inspect", Category: "staging", Description: "Read-only preflight on a staged file (reuses bookmarks.inspect)",
        Parameters: [ParamSpec.Req<string>("staging_id", "Staging ID")],
        Caps: CommandCaps.Query | CommandCaps.FileIo);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "staging_id");
        var staged = (await staging.ListAsync(ctx.Ct)).FirstOrDefault(f => f.StagingId == id)
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"staged file does not exist: {id}"));
        var inspection = await ctx.DispatchNestedAsync("bookmarks.inspect", new { file_path = staged.FullPath }, ctx.Ct);
        // 以 JsonElement 落形（内部 DTO 对编排消费者保持黑盒，wire/Client 两侧同形）
        return CommandResult.Ok(BatchEngine.ToElement(inspection.Data));
    }
}

internal sealed class StagingTransformHandler(StagingService staging) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "staging.transform", Category: "staging", Description: "Run a pure-function transform pipeline over a staged file (filter_links/rename_folder/map_field/strip_prefix/dedupe/reencode; dry_run returns a preview only)",
        Parameters: [ParamSpec.Req<string>("staging_id", "Staging ID"), ParamSpec.Req<JsonElement>("ops", "Transform operator array [{op, args}]", schema: ParamSchemas.StagingOps), ParamSpec.Opt<bool>("dry_run", "Dry run (default false)")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo | CommandCaps.SupportsCancellation,
        Client: new("StagingTransformAsync", typeof(StagingTransformReport)));

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "staging_id");
        // 命令级干跑（CallOptions.DryRun）与参数级 dry_run 同义：transform 会落盘改写暂存文件，两路都必须只预览
        var dryRun = CommandArgs.OptionalBool(args, "dry_run") || ctx.DryRun;
        var opsJson = CommandArgs.Raw(args, "ops")
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
                "required parameter 'ops' is missing", details: JsonSerializer.SerializeToElement(new { @param = "ops" })));
        var ops = opsJson.ValueKind == JsonValueKind.Array
            ? opsJson.EnumerateArray().Select(o =>
            {
                // 每个算子必须是对象形态 [{op, args}]：非对象元素显式报 TypeMismatch（
                // 直接 TryGetProperty 会在底层抛路径异常并被引擎兜成 INTERNAL）
                if (o.ValueKind != JsonValueKind.Object)
                    throw new EngineException(EngineErrors.Of(EngineErrors.TypeMismatch,
                        "every ops entry must be an object { op, args? }"));
                return new TransformOp(
                    CommandArgs.RequireString(o, "op"),
                    o.TryGetProperty("args", out var a) ? a.Clone() : JsonSerializer.Deserialize<JsonElement>("{}"));
            })
            : throw new EngineException(EngineErrors.Of(EngineErrors.TypeMismatch, "ops must be an array of operator objects"));
        var report = await staging.TransformAsync(id, [.. ops], dryRun, ctx.Ct);
        return CommandResult.Ok(report);
    }
}

internal sealed class StagingCommitHandler(StagingService staging) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "staging.commit", Category: "staging", Description: "Hand a staged file to a real command (file_path is merged into the arguments; same transaction as this command)",
        Parameters: [ParamSpec.Req<string>("staging_id", "Staging ID"), ParamSpec.Req<string>("command", "Target command (e.g. bookmarks.import)"), ParamSpec.Opt<JsonElement>("args", "Extra arguments")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "staging_id");
        var command = CommandArgs.RequireString(args, "command");
        var extra = CommandArgs.Raw(args, "args");
        // 干跑（ctx.DryRun）经 DispatchNestedAsync 原样透传给目标命令：目标自检 DryRun（本命令自身零副作用），
        // 暂存副本也不删——"转交 + 清理"里的清理归 staging.discard
        var merged = staging.BuildCommitArgs(id, extra);
        var result = await ctx.DispatchNestedAsync(command, merged, ctx.Ct);
        return CommandResult.Ok(BatchEngine.ToElement(result.Data), result.Changes);
    }
}
