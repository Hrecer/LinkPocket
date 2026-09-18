using System.Diagnostics;
using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Engine;

/// <summary>
/// 编排命令处理器集（阶段 11，方案 4.2/4.3）：15 个注册命令
/// （macro.* 5 / undo.* 4 / staging.* 6）。
/// batch.run / batch.dry_run / batch.status 不注册——由 wire 层直路由 <see cref="BatchEngine"/>；
/// diagnostics.collect 归属 Maintenance 模块（方案 4.2 总表），引擎侧诊断数据经其 ctx.Uow 取数。
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
            new UndoListHandler(undo), new UndoUndoHandler(undo), new UndoRedoHandler(undo),
            new UndoClearHandler(undo),
            new StagingStageHandler(staging), new StagingListHandler(staging), new StagingDiscardHandler(staging),
            new StagingInspectHandler(staging), new StagingTransformHandler(staging), new StagingCommitHandler(staging),
        ];
}

// ===== macro.* =====

internal sealed class MacroSaveHandler(IMacroStore macros) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "macro.save", Category: "macro", Description: "保存命名批脚本（宏/技能库；无效脚本拒绝入库）",
        Parameters: [ParamSpec.Req<string>("name", "宏名"), ParamSpec.Req<JsonElement>("script", "批脚本（BatchScript JSON）")],
        Caps: CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = CommandArgs.RequireString(args, "name");
        var script = CommandArgs.Raw(args, "script")
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
                "缺少必填参数「script」", details: JsonSerializer.SerializeToElement(new { @param = "script" })));
        await macros.SaveAsync(name, script.GetRawText(), ctx.Ct);
        return CommandResult.Ok(JsonSerializer.SerializeToElement(name),
            ChangeSet.Of(new EntityRef("macro", name), DomainEventNames.MacroSaved, $"已保存宏「{name}」"));
    }
}

internal sealed class MacroGetHandler(IMacroStore macros) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "macro.get", Category: "macro", Description: "读取宏的批脚本定义",
        Parameters: [ParamSpec.Req<string>("name", "宏名")], Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = CommandArgs.RequireString(args, "name");
        var json = await macros.GetAsync(name, ctx.Ct)
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"宏「{name}」不存在"));
        return CommandResult.Ok(JsonSerializer.Deserialize<JsonElement>(json));
    }
}

internal sealed class MacroListHandler(IMacroStore macros) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "macro.list", Category: "macro", Description: "列出全部宏（名称 + 更新时间，不含脚本体）",
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
        Name: "macro.delete", Category: "macro", Description: "删除宏",
        Parameters: [ParamSpec.Req<string>("name", "宏名")], Caps: CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = CommandArgs.RequireString(args, "name");
        if (!await macros.DeleteAsync(name, ctx.Ct))
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"宏「{name}」不存在"));
        return CommandResult.Ok(JsonSerializer.SerializeToElement(name),
            ChangeSet.Of(new EntityRef("macro", name), DomainEventNames.MacroDeleted, $"已删除宏「{name}」"));
    }
}

internal sealed class MacroRunHandler(IMacroStore macros) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "macro.run", Category: "macro", Description: "按事务批语义运行宏（步骤嵌套派发共享本命令的工作单元；abort 整体回滚）",
        Parameters: [ParamSpec.Req<string>("name", "宏名")],
        Caps: CommandCaps.Mutation | CommandCaps.LongRunning | CommandCaps.SupportsCancellation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var name = CommandArgs.RequireString(args, "name");
        var scriptJson = await macros.GetAsync(name, ctx.Ct)
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"宏「{name}」不存在"));
        BatchScript script;
        try
        {
            script = JsonSerializer.Deserialize<BatchScript>(scriptJson, EngineJson.ScriptOptions)
                ?? throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed, $"宏「{name}」的脚本不是合法的批脚本"));
        }
        catch (JsonException)
        {
            // 坏 JSON 是输入问题而非内部错误：必须报 ProtocolMalformed，不得冒泡成 LP.INTERNAL
            throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed, $"宏「{name}」的脚本不是合法的批脚本"));
        }

        // 宏实际运行耗时（审核 2.3：此前 ElapsedMs 恒为 0，诊断面丢失「宏跑了多久」）
        var sw = Stopwatch.StartNew();
        var (results, touched, events) = await BatchEngine.RunStepsNestedAsync(
            (CommandContextImpl)ctx, script with { Name = $"macro:{name}" }, ctx.Ct);
        sw.Stop();

        var summary = $"宏「{name}」运行完成：{results.Count(r => r.Ok)}/{results.Count} 步成功";
        var report = new BatchReport(
            ctx.CorrelationId, $"macro:{name}", results.All(r => r.Ok), results,
            new ChangeSet(touched, events, summary), summary, sw.ElapsedMilliseconds, ctx.CorrelationId);
        return CommandResult.Ok(BatchEngine.ToElement(report), new ChangeSet(touched, events, summary));
    }
}

// ===== undo.* =====

internal sealed class UndoListHandler(IUndoCoordinator undo) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "undo.list", Category: "undo", Description: "列出撤销栈（最近在前，上限 100 条）",
        Parameters: [], Caps: CommandCaps.Query);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var entries = await undo.ListAsync(ctx.Ct);
        return CommandResult.Ok(JsonSerializer.SerializeToElement(new { entries }, EngineJson.Options));
    }
}

internal sealed class UndoUndoHandler(UndoCoordinator undo) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "undo.undo", Category: "undo", Description: "撤销最近一条可撤销命令（嵌套派发其逆向命令；弹出后转入重做栈）",
        Parameters: [ParamSpec.Opt<string>("id", "撤销条目 ID（缺省 = 最近一条）")],
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
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, "没有可撤销的命令"));

        var result = await ctx.DispatchNestedAsync(entry.InverseCommand, entry.InverseArgs, ctx.Ct);

        var taken = await undo.TakeUndoAsync(entry.Id, ctx.Ct);
        if (taken != null) undo.MarkUndone(taken);
        return CommandResult.Ok(BatchEngine.ToElement(result.Data), result.Changes);
    }
}

internal sealed class UndoRedoHandler(UndoCoordinator undo) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "undo.redo", Category: "undo", Description: "重做：按原参数重放最近一条被撤销的命令（重放成功重新入撤销栈）",
        Parameters: [], Caps: CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        // 先取（不弹）再执行：失败时条目留在重做栈（同 undo.undo 的防丢语义）
        var redoEntries = await undo.ListRedoAsync(ctx.Ct);
        var entry = redoEntries.FirstOrDefault();
        if (entry is null)
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, "没有可重做的命令"));

        var result = await ctx.DispatchNestedAsync(entry.Command, entry.Args, ctx.Ct);

        var taken = await undo.TakeRedoAsync(ctx.Ct);
        if (taken == null) return CommandResult.Ok(BatchEngine.ToElement(result.Data), result.Changes);
        var handler = ((CommandContextImpl)ctx).Engine.Registry.Resolve(taken.Command);
        if (handler != null)
            undo.Record(handler.Descriptor, taken.Args, ctx.Caller);   // 新动作使后续重做链失效（Record 内清重做栈）
        return CommandResult.Ok(BatchEngine.ToElement(result.Data), result.Changes);
    }
}

internal sealed class UndoClearHandler(IUndoCoordinator undo) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "undo.clear", Category: "undo", Description: "清空撤销栈与重做栈",
        Parameters: [], Caps: CommandCaps.Mutation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var count = await undo.ClearAsync(ctx.Ct);
        return CommandResult.Ok(count, ChangeSet.Of(new EntityRef("undo", "*"), DomainEventNames.UndoCleared, $"已清空 {count} 条撤销/重做记录"));
    }
}

// ===== staging.* =====

internal sealed class StagingStageHandler(StagingService staging) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "staging.stage", Category: "staging", Description: "把文件拷入 AI 文件准备区（SHA-256 指纹登记）",
        Parameters: [ParamSpec.Req<string>("source_path", "源文件完整路径")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var source = CommandArgs.RequireString(args, "source_path");
        var staged = await staging.StageAsync(source, ctx.Ct);
        return CommandResult.Ok(staged, ChangeSet.Of(new EntityRef("staged", staged.StagingId), DomainEventNames.StagingStaged, $"已暂存「{staged.FileName}」"));
    }
}

internal sealed class StagingListHandler(StagingService staging) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "staging.list", Category: "staging", Description: "列出暂存区文件",
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
        Name: "staging.discard", Category: "staging", Description: "丢弃暂存文件（删除副本并注销登记）",
        Parameters: [ParamSpec.Req<string>("staging_id", "暂存 ID")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "staging_id");
        if (!await staging.DiscardAsync(id, ctx.Ct))
            throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"暂存文件不存在：{id}"));
        return CommandResult.Ok(JsonSerializer.SerializeToElement(id),
            ChangeSet.Of(new EntityRef("staged", id), DomainEventNames.StagingDiscarded, "已丢弃暂存文件"));
    }
}

internal sealed class StagingInspectHandler(StagingService staging) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "staging.inspect", Category: "staging", Description: "对暂存文件做只读预检（复用 bookmarks.inspect）",
        Parameters: [ParamSpec.Req<string>("staging_id", "暂存 ID")],
        Caps: CommandCaps.Query | CommandCaps.FileIo);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "staging_id");
        var staged = (await staging.ListAsync(ctx.Ct)).FirstOrDefault(f => f.StagingId == id)
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.EntityNotFound, $"暂存文件不存在：{id}"));
        var inspection = await ctx.DispatchNestedAsync("bookmarks.inspect", new { file_path = staged.FullPath }, ctx.Ct);
        // 以 JsonElement 落形（内部 DTO 对编排消费者保持黑盒，wire/Client 两侧同形）
        return CommandResult.Ok(BatchEngine.ToElement(inspection.Data));
    }
}

internal sealed class StagingTransformHandler(StagingService staging) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "staging.transform", Category: "staging", Description: "对暂存文件执行纯函数变换管道（filter_links/rename_folder/map_field/strip_prefix/dedupe/reencode；dry_run 只出预览）",
        Parameters: [ParamSpec.Req<string>("staging_id", "暂存 ID"), ParamSpec.Req<JsonElement>("ops", "变换算子数组 [{op, args}]"), ParamSpec.Opt<bool>("dry_run", "预演（缺省 false）")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo | CommandCaps.SupportsCancellation);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "staging_id");
        var dryRun = CommandArgs.OptionalBool(args, "dry_run");
        var opsJson = CommandArgs.Raw(args, "ops")
            ?? throw new EngineException(EngineErrors.Of(EngineErrors.RequiredParam,
                "缺少必填参数「ops」", details: JsonSerializer.SerializeToElement(new { @param = "ops" })));
        var ops = opsJson.ValueKind == JsonValueKind.Array
            ? opsJson.EnumerateArray().Select(o =>
            {
                // 每个算子必须是对象形态 [{op, args}]：非对象元素显式报 TypeMismatch（
                // 直接 TryGetProperty 会在底层抛路径异常并被引擎兜成 INTERNAL）
                if (o.ValueKind != JsonValueKind.Object)
                    throw new EngineException(EngineErrors.Of(EngineErrors.TypeMismatch,
                        "ops 的每个算子必须是对象 { op, args? }"));
                return new TransformOp(
                    CommandArgs.RequireString(o, "op"),
                    o.TryGetProperty("args", out var a) ? a.Clone() : JsonSerializer.Deserialize<JsonElement>("{}"));
            })
            : throw new EngineException(EngineErrors.Of(EngineErrors.TypeMismatch, "ops 必须是算子对象数组"));
        var report = await staging.TransformAsync(id, [.. ops], dryRun, ctx.Ct);
        return CommandResult.Ok(report);
    }
}

internal sealed class StagingCommitHandler(StagingService staging) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        Name: "staging.commit", Category: "staging", Description: "把暂存文件转交正式命令执行（file_path 自动并入参数；与本命令同一事务）",
        Parameters: [ParamSpec.Req<string>("staging_id", "暂存 ID"), ParamSpec.Req<string>("command", "目标命令（如 bookmarks.import）"), ParamSpec.Opt<JsonElement>("args", "附加参数")],
        Caps: CommandCaps.Mutation | CommandCaps.FileIo);

    public async Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
    {
        var id = CommandArgs.RequireString(args, "staging_id");
        var command = CommandArgs.RequireString(args, "command");
        var extra = CommandArgs.Raw(args, "args");
        var merged = staging.BuildCommitArgs(id, extra);
        var result = await ctx.DispatchNestedAsync(command, merged, ctx.Ct);
        return CommandResult.Ok(BatchEngine.ToElement(result.Data), result.Changes);
    }
}
