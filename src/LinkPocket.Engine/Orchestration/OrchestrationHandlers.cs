using System.Diagnostics;
using System.Text.Json;
using LinkPocket.Contracts;
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
        IMacroStore macros, UndoCoordinator undo, StagingService staging)
        =>
        [
            new MacroSaveHandler(macros), new MacroGetHandler(macros), new MacroListHandler(macros),
            new MacroDeleteHandler(macros), new MacroRunHandler(macros),
            new UndoListHandler(undo), new UndoListRedoHandler(undo), new UndoUndoHandler(undo), new UndoRedoHandler(undo),
            new UndoClearHandler(undo),
            new StagingStageHandler(staging), new StagingListHandler(staging), new StagingDiscardHandler(staging),
            new StagingInspectHandler(staging), new StagingTransformHandler(staging), new StagingCommitHandler(staging),
        ];
}

// ===== macro.* =====

internal sealed class MacroSaveHandler(IMacroStore macros) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "macro.save", Category: "macro", Description: "Save a named batch script (macro / skill library; invalid scripts are rejected)",
        Parameters: [ParamSpec.Req<string>("name", "Macro name"), ParamSpec.Req<JsonElement>("script", "Batch script (BatchScript JSON)")],
        Caps: CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = CommandArgs.RequireString(args, "name");
        var script = CommandArgs.Raw(args, "script")
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
                "required parameter 'script' is missing", details: JsonSerializer.SerializeToElement(new { @param = "script" })));

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
        Parameters: [ParamSpec.Req<string>("name", "Macro name")], Caps: CommandCaps.Query);

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
        Parameters: [], Caps: CommandCaps.Query);

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
        Parameters: [ParamSpec.Req<string>("name", "Macro name")], Caps: CommandCaps.Mutation);

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

internal sealed class MacroRunHandler(IMacroStore macros) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "macro.run", Category: "macro", Description: "Run a macro with transactional batch semantics (nested step dispatch shares this command's unit of work; abort rolls the whole batch back)",
        Parameters: [ParamSpec.Req<string>("name", "Macro name")],
        Caps: CommandCaps.Mutation | CommandCaps.LongRunning | CommandCaps.SupportsCancellation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = CommandArgs.RequireString(args, "name");
        var scriptJson = await macros.GetAsync(name, ctx.Ct)
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"macro '{name}' does not exist"));
        BatchScript script;
        try
        {
            script = JsonSerializer.Deserialize<BatchScript>(scriptJson, EngineJson.ScriptOptions)
                ?? throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed, $"the script of macro '{name}' is not a valid batch script"));
        }
        catch (JsonException)
        {
            // 坏 JSON 是输入问题而非内部错误：必须报 ProtocolMalformed，不得冒泡成 LP.INTERNAL
            throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed, $"the script of macro '{name}' is not a valid batch script"));
        }

        // 宏实际运行耗时（此前 ElapsedMs 恒为 0，诊断面丢失「宏跑了多久」）
        var sw = Stopwatch.StartNew();
        var context = (CommandContextImpl)ctx;
        var (results, touched, events, diff) = await BatchEngine.RunStepsNestedAsync(
            context, script with { Name = $"macro:{name}" },
            context.UndoGroupId ?? $"macro:{Guid.NewGuid():N}", ctx.Ct);
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

internal sealed class UndoListHandler(IUndoCoordinator undo) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "undo.list", Category: "undo", Description: "List the undo stack (newest first, capped at 100 entries)",
        Parameters: [], Caps: CommandCaps.Query);

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
        Parameters: [], Caps: CommandCaps.Query);

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
        var entries = await undo.ListAsync(ctx.Ct);
        var entry = id is null
            ? entries.FirstOrDefault()
            : entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (entry is null)
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, "nothing to undo"));

        // 逆序回退：一次动作的多步（如一次粘贴多项）按**相反顺序**撤销，避免中途引用已消失的目标。
        // 单项失败不中断整批（与 UI 侧批量语义一致）：失败项如实记录并跳过，整条最终被消费
        // ——"报错并跳过"是定稿口径（绝不静默卡在同一条上让 Ctrl+Z 永远失败）。
        var failures = new List<string>();
        ChangeSet? lastChanges = null;
        object? lastData = null;
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

        // 部分失败不转入重做栈（重做会重放整条原始命令，对已成功回退的步骤会二次施加 → 状态错乱）
        var taken = await undo.TakeUndoAsync(entry.Id, ctx.Ct);
        if (taken != null && failures.Count == 0) undo.MarkUndone(taken);

        var summary = failures.Count == 0
            ? $"Undid {entry.Steps.Count} step(s)"
            : $"Undid {entry.Steps.Count - failures.Count} step(s), {failures.Count} failed (skipped)";
        return CommandResult.Ok(
            BatchEngine.ToElement(lastData),
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
        Parameters: [], Caps: CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        // 先取（不弹）再执行：失败时条目留在重做栈（同 undo.undo 的防丢语义）
        var redoEntries = await undo.ListRedoAsync(ctx.Ct);
        var entry = redoEntries.FirstOrDefault();
        if (entry is null)
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, "nothing to redo"));

        // 正序重放（撤销时是逆序，重做对称回来）。重做动作 = 步骤显式给出者优先
        //（创建类必须显式：重放 links.create 会生成**新 ID**，原 ID 丢失且回收站留旧快照），否则重放原命令原参数。
        var failures = new List<string>();
        ChangeSet? lastChanges = null;
        object? lastData = null;
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

        var taken = await undo.TakeRedoAsync(ctx.Ct);
        if (taken == null || failures.Count > 0)
        {
            // 部分失败：不重新入撤销栈（否则会留下"半重放"的可撤销记录，语义混乱）
            return CommandResult.Ok(BatchEngine.ToElement(lastData),
                new ChangeSet(lastChanges?.Touched ?? [], lastChanges?.Events ?? [],
                    failures.Count == 0 ? null : $"Redo failed for {failures.Count} step(s) (skipped)",
                    failures.Count > 0 ? failures : null));
        }

        // 重新入撤销栈：逆向步骤直接复用原记录（重做重放的是同一动作，其逆向不变），
        // 无需再读一次旧值；分组 ID 沿用，保证"撤销→重做→再撤销"的批量语义一致。
        foreach (var step in taken.Steps)
        {
            var handler = ((CommandContextImpl)ctx).Engine.Registry.Resolve(step.Command);
            if (handler == null) continue;
            undo.Record(handler.Descriptor, step.Args, ctx.Caller,
                [new UndoInverseStep(step.InverseCommand, step.InverseArgs, step.Redo)], taken.GroupId);
        }

        return CommandResult.Ok(BatchEngine.ToElement(lastData), lastChanges);
    }
}

internal sealed class UndoClearHandler(IUndoCoordinator undo) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "undo.clear", Category: "undo", Description: "Clear the undo and redo stacks",
        Parameters: [], Caps: CommandCaps.Mutation);

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
        Caps: CommandCaps.Mutation | CommandCaps.FileIo);

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
        Parameters: [], Caps: CommandCaps.Query);

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
        Parameters: [ParamSpec.Req<string>("staging_id", "Staging ID"), ParamSpec.Req<JsonElement>("ops", "Transform operator array [{op, args}]"), ParamSpec.Opt<bool>("dry_run", "Dry run (default false)")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo | CommandCaps.SupportsCancellation);

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
