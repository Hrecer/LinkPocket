using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.UI.Ai;
using LinkPocket.Views;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// AI 页 VM 金标准：斜杠命令（本地解析、不走模型）与引擎审计分页。
/// 断言口径 = 可观测结果（提示条文案键 / 页签投影 / 桩记录的调用），不测实现细节。
/// </summary>
public class AiViewModelTests
{
    private static (AiViewModel Vm, StubAiAssistant Stub) NewVm()
    {
        var stub = new StubAiAssistant
        {
            Selection = new AiSelectionResolution(new AiModelSelection("gw", "m1"), null, "gw", "m1"),
        };
        stub.Sessions.Add(new AiSessionSummary("s-1", "会话", AiMode.ConfirmEach, "gw", "m1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, null));
        return (new AiViewModel(stub), stub);
    }

    private static string? LastNoticeKey(AiViewModel vm)
        => vm.Feed.LastOrDefault(i => i.Kind == AiFeedItem.ItemKind.Notice)?.NoticeValue.Key;

    [Fact]
    public async Task 斜杠命令_help_提示条入流_不发给模型()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        vm.ComposerText = "/help";

        await vm.SendAsync();

        Assert.Empty(stub.SendCalls);
        Assert.Equal("ai.slash.help", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 斜杠命令_未知命令_如实提示命令名()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        vm.ComposerText = "/不存在";

        await vm.SendAsync();

        Assert.Empty(stub.SendCalls);
        Assert.Equal("ai.slash.unknown", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 斜杠命令_mode带参数_切模式并提示()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        vm.ComposerText = "/mode auto";

        await vm.SendAsync();

        Assert.Equal(AiMode.AutoApply, vm.Mode);
        Assert.Equal(AiMode.AutoApply, Assert.Single(stub.SetModeCalls).Mode);
        Assert.Equal("ai.slash.modeSet.autoApply", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 斜杠命令_mode带非法参数_给用法提示不切换()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        vm.ComposerText = "/mode 乱写";

        await vm.SendAsync();

        Assert.Empty(stub.SetModeCalls);
        Assert.Equal("ai.slash.modeUsage", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 斜杠命令_audit_切到引擎审计页签()
    {
        var (vm, _) = NewVm();
        await vm.LoadAsync();
        vm.ComposerText = "/audit";

        await vm.SendAsync();

        Assert.True(vm.IsEngineTab);
    }

    [Fact]
    public async Task 斜杠命令_model_无启用模型_如实提示()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        stub.Providers.Add(new AiProviderInfo("gw", "gw", AiProtocol.OpenAiChat, "https://gw.test/v1",
            AiProviderSource.Custom, false, true, true, "sk-1234", AiProviderStatus.Verified, null, null, null,
            [new AiModelInfo("m1", "m1", AiModelSource.Manual, Enabled: false, null, null, true, true)]));
        vm.ComposerText = "/model";

        await vm.SendAsync();

        Assert.Empty(stub.SavePreferencesCalls);
        Assert.Equal("ai.slash.modelNone", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 斜杠命令_model带模型名_写入偏好并提示()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        stub.Providers.Add(new AiProviderInfo("gw", "gw", AiProtocol.OpenAiChat, "https://gw.test/v1",
            AiProviderSource.Custom, false, true, true, "sk-1234", AiProviderStatus.Verified, null, null, null,
            [new AiModelInfo("m1", "m1", AiModelSource.Manual, Enabled: true, null, null, true, true),
             new AiModelInfo("m2", "m2", AiModelSource.Manual, Enabled: true, null, null, true, true)]));
        vm.ComposerText = "/model m2";

        await vm.SendAsync();

        var saved = Assert.Single(stub.SavePreferencesCalls);
        Assert.Equal("m2", saved.ModelId);
        Assert.Equal("ai.slash.modelSet", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 斜杠命令_undo_按结果如实提示()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        stub.UndoResult = new AiUndoResult(2, 2, 0, null);
        vm.ComposerText = "/undo";

        await vm.SendAsync();

        Assert.Equal(1, stub.UndoCalls);
        Assert.Equal("ai.slash.undoDone", LastNoticeKey(vm));
    }

    [Fact]
    public async Task 发送_未配置时普通消息禁发_斜杠命令仍可发()
    {
        var stub = new StubAiAssistant
        {
            Selection = new AiSelectionResolution(null, AiSelectionIssue.ProviderMissing, null, null),
        };
        stub.Sessions.Add(new AiSessionSummary("s-1", "会话", AiMode.ConfirmEach, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, null));
        var vm = new AiViewModel(stub);
        await vm.LoadAsync();

        vm.ComposerText = "/help";          // 斜杠命令不走模型：未配置也可发
        Assert.True(vm.CanSend);
        await vm.SendAsync();
        Assert.Empty(stub.SendCalls);

        vm.ComposerText = "帮我整理书签";     // 普通消息需要可用模型
        Assert.False(vm.CanSend);
    }

    [Fact]
    public async Task 引擎审计_翻页携带页码_到尾页禁用下一页()
    {
        var (vm, stub) = NewVm();
        stub.AuditPageCount = 2;
        await vm.LoadAsync();
        vm.TabIndex = 3;
        await WaitUntilAsync(() => stub.AuditQueries.Count > 0);   // 页签切换触发的异步重载落地

        Assert.Equal(1, stub.AuditQueries[^1].Page);
        Assert.True(vm.CanNextAuditPage);
        Assert.False(vm.CanPrevAuditPage);

        await vm.AuditNextAsync();
        Assert.Equal(2, stub.AuditQueries[^1].Page);
        Assert.False(vm.CanNextAuditPage);
        Assert.True(vm.CanPrevAuditPage);

        await vm.AuditPrevAsync();
        Assert.Equal(1, stub.AuditQueries[^1].Page);
    }

    // ── 审批卡（P3-7）：焦点请求 / 拒绝路径 / 卡片投影 / 动作短语键覆盖 ──

    private static AiApproval Approval(
        string id = "a-1",
        string command = "folders.create",
        AiApprovalDecision? decision = null,
        int targetCount = 1,
        IReadOnlyList<string>? targetNames = null,
        int targetMore = 0,
        string? targetPath = null,
        string? allowScope = null,
        string? impact = null,
        IReadOnlyList<AiApprovalStep>? steps = null)
        => new(id, 1, "t-1", "c-1", command, false, targetCount, targetNames ?? [],
            null, impact, null, decision, decision is null ? null : "拒绝理由", 0, DateTimeOffset.UtcNow,
            targetMore, targetPath, steps, allowScope);

    [Fact]
    public async Task 审批_待批时请求把焦点给拒绝_作决定后不再抢焦点()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        var requested = new List<string>();
        vm.ApprovalFocusRequested += approvalId => requested.Add(approvalId);

        stub.RaiseNotify(new AiNotification(AiNotificationKind.ApprovalChanged, "s-1", Approval: Approval()));

        Assert.Equal("a-1", vm.OpenApprovalId);
        Assert.Equal("a-1", Assert.Single(requested));

        stub.RaiseNotify(new AiNotification(AiNotificationKind.ApprovalChanged, "s-1",
            Approval: Approval(decision: AiApprovalDecision.Reject)));

        Assert.Null(vm.OpenApprovalId);
        Assert.Single(requested);   // 已作决定的卡不再请求焦点
    }

    [Fact]
    public async Task 审批_拒绝路径把理由带回助手并清空理由输入()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        stub.RaiseNotify(new AiNotification(AiNotificationKind.ApprovalChanged, "s-1", Approval: Approval()));
        vm.ApprovalReason = "别动我的书签";

        await vm.RespondAsync(Assert.Single(vm.Approvals), AiApprovalDecision.Reject);

        var call = Assert.Single(stub.ApprovalCalls);
        Assert.Equal("s-1", call.SessionId);
        Assert.Equal("a-1", call.ApprovalId);
        Assert.Equal(AiApprovalDecision.Reject, call.Decision);
        Assert.Equal("别动我的书签", call.Reason);
        Assert.Equal("", vm.ApprovalReason);
    }

    [Fact]
    public async Task 审批卡_对象作用域逐步骤与影响面全部投影出来()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        stub.RaiseNotify(new AiNotification(AiNotificationKind.ApprovalChanged, "s-1",
            Approval: Approval(command: "batch.run", targetCount: 7, targetNames: ["工作", "临时"], targetMore: 5,
                allowScope: "batch.run", impact: "entire database",
                steps:
                [
                    new AiApprovalStep(1, "folders.create", "工作", 1, false, null),
                    new AiApprovalStep(2, "trash.purge", null, 3, true, "skip_and_log"),
                ])));

        var card = vm.Feed.Single(i => i.Kind == AiFeedItem.ItemKind.Approval);

        Assert.Equal("ai.action.batch.run", card.ActionKey);                      // 做什么
        Assert.True(card.TargetValue.IsLiteral);
        Assert.Equal("工作, 临时", card.TargetValue.Args.Span[0]);                // 动哪些对象
        Assert.True(card.HasMoreTargets);                                          // 名称截断如实标注
        Assert.Equal("ai.approve.target.more", card.MoreTargetsValue.Key);
        Assert.Equal(5, card.MoreTargetsValue.Args.Span[0]);
        Assert.True(card.HasImpact);
        Assert.Equal("ai.approve.impact.database", card.ImpactValue.Key);          // 影响面来自引擎
        Assert.True(card.HasApprovalSteps);                                        // 批 = 逐步骤影响
        Assert.Equal(2, card.ApprovalSteps.Count);
        Assert.Equal("ai.approve.steps.row", card.ApprovalSteps[0].IndexLabel.Key);
        Assert.Equal("工作", card.ApprovalSteps[0].TargetValue.Args.Span[0]);
        Assert.True(card.ApprovalSteps[1].IsDestructive);
        Assert.Equal("ai.onError.skipAndLog", card.ApprovalSteps[1].OnErrorValue.Key);
        Assert.True(card.HasAllowScope);                                           // 会话允许必须显示作用域
        Assert.Equal("ai.approve.allowScope", card.AllowScopeValue.Key);
        Assert.Equal("batch.run", card.AllowScopeValue.Args.Span[0]);
        Assert.True(card.HasPreview);
    }

    [Fact]
    public async Task 审批卡_一点可看的影响都没有_如实说明无法预览()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        stub.RaiseNotify(new AiNotification(AiNotificationKind.ApprovalChanged, "s-1",
            Approval: Approval(command: "undo.undo", targetCount: 0)));

        var card = vm.Feed.Single(i => i.Kind == AiFeedItem.ItemKind.Approval);

        Assert.Equal("ai.action.undo.undo", card.ActionKey);
        Assert.Equal("ai.approve.target.unknown", card.TargetValue.Key);
        Assert.False(card.HasPreview);
        Assert.False(card.HasApprovalSteps);
        Assert.False(card.HasAllowScope);
    }

    [Fact]
    public void 审批_每条变更命令都有动作短语键()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var keys = StringTables.Keys.ToHashSet(StringComparer.Ordinal);
            var mutations = client.Describe().Commands.Where(c => c.Caps.HasFlag(CommandCaps.Mutation)).ToArray();
            Assert.NotEmpty(mutations);

            var missing = mutations.Where(c => !keys.Contains(AiKeyMap.Action(c.Name)))
                .Select(c => c.Name).ToArray();
            Assert.True(missing.Length == 0, "缺少审批动作短语键：" + string.Join(", ", missing));

            // 键规范的段不许有下划线：命令名必须先转 camelCase 再拼键
            Assert.Equal("ai.action.folders.moveBatch", AiKeyMap.Action("folders.move_batch"));
            Assert.Equal("ai.action.trash.purgeBatch", AiKeyMap.Action("trash.purge_batch"));
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    // ── 撤销本会话 + 审计时间范围（P3-8）──────────────────────────────

    [Fact]
    public async Task 撤销本会话_有可撤销批次才给按钮_撤完按新栈快照收起()
    {
        var (vm, stub) = NewVm();
        stub.UndoableBatches = 2;
        stub.UndoResult = new AiUndoResult(2, 2, 0, null);
        await vm.LoadAsync();
        await WaitUntilAsync(() => vm.CanUndoSession);

        Assert.True(vm.CanUndoSession);

        var counted = stub.CountUndoableCalls.Count;
        await vm.UndoSessionAsync();

        Assert.Equal("s-1", Assert.Single(stub.UndoSessionCalls));
        Assert.Equal("ai.slash.undoDone", LastNoticeKey(vm));   // 回执与 /undo 同一套文案
        Assert.Equal(counted + 1, stub.CountUndoableCalls.Count);   // 撤完自己重数了一遍

        // 撤完引擎栈空了 → 面板重算"按钮给不给"（**不给会失败的按钮**）
        stub.UndoableBatches = 0;
        await vm.RefreshUndoableAsync();
        Assert.False(vm.CanUndoSession);
    }

    [Fact]
    public async Task 撤销本会话_没有可撤销批次_按钮不给也发不出命令()
    {
        var (vm, stub) = NewVm();
        stub.UndoableBatches = 0;
        await vm.LoadAsync();
        await WaitUntilAsync(() => stub.CountUndoableCalls.Count > 0);   // 刷过了（读数为 0）

        Assert.False(vm.CanUndoSession);
        await vm.UndoSessionAsync();
        Assert.Empty(stub.UndoSessionCalls);
    }

    [Fact]
    public async Task 审计时间范围_切换即回第一页并把from带给服务端()
    {
        var (vm, stub) = NewVm();
        stub.AuditPageCount = 2;
        await vm.LoadAsync();
        vm.TabIndex = 3;
        await WaitUntilAsync(() => stub.AuditQueries.Count > 0);
        Assert.Null(stub.AuditQueries[^1].From);          // 缺省 = 不限时间

        await vm.AuditNextAsync();
        Assert.Equal(2, stub.AuditQueries[^1].Page);

        var seen = stub.AuditQueries.Count;
        vm.AuditRangeIndex = 1;                           // 今天
        await WaitUntilAsync(() => stub.AuditQueries.Count > seen);
        Assert.Equal(1, stub.AuditQueries[^1].Page);      // 结果集变了 → 回第一页
        var today = stub.AuditQueries[^1].From;
        Assert.NotNull(today);
        Assert.Equal(DateTime.Today, today!.Value.Date);  // 本地日界（今天 00:00）

        vm.AuditRangeIndex = 2;                           // 近 7 天
        await WaitUntilAsync(() => stub.AuditQueries.Count > seen + 1);
        Assert.Equal(DateTime.Today.AddDays(-6), stub.AuditQueries[^1].From!.Value.Date);

        vm.AuditRangeIndex = 0;                           // 回到全部
        await WaitUntilAsync(() => stub.AuditQueries.Count > seen + 2);
        Assert.Null(stub.AuditQueries[^1].From);
    }

    /// <summary>等一个可观测副作用落地（异步重载是 fire-and-forget 的界面口径；上限 2 秒，超时即失败）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "异步重载未在 2 秒内落地");
    }

    // ── 草稿新建：连点不产空会话 / 未用草稿可回收 / 首发提升 ─────────

    [Fact]
    public async Task 新建会话_连点多次_复用同一个草稿_左栏不增行()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        var before = vm.Sessions.Count;

        for (var i = 0; i < 5; i++) await vm.NewSessionAsync();

        Assert.Equal(1, stub.CreateSessionCalls);        // 单飞：只建了一个草稿
        Assert.Equal(before, vm.Sessions.Count);         // 左栏一行没多
        Assert.Equal("s-draft1", vm.ActiveSessionId);    // 停在那个草稿上
    }

    [Fact]
    public async Task 草稿_切到别的会话_未用草稿被回收()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        await vm.NewSessionAsync();
        Assert.Empty(stub.DiscardedDrafts);              // 刚建好还在用：不回收

        await vm.OpenSessionAsync("s-1");                // 切到正式会话 → 草稿弃用

        Assert.Contains("s-draft1", stub.DiscardedDrafts);
    }

    [Fact]
    public async Task 草稿_会话变更通知_不进左栏列表()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        var before = vm.Sessions.Count;

        stub.RaiseNotify(new AiNotification(AiNotificationKind.SessionChanged, "s-draft9",
            Session: new AiSessionSummary("s-draft9", "", AiMode.ConfirmEach, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, null, AiSessionPersistence.Deferred)));

        Assert.Equal(before, vm.Sessions.Count);         // 草稿通知被过滤，左栏不动
        Assert.DoesNotContain(vm.Sessions, s => s.SessionId == "s-draft9");
    }

    [Fact]
    public async Task 草稿_提升通知_进左栏列表()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        await vm.NewSessionAsync();
        var before = vm.Sessions.Count;

        // 引擎在草稿被提升为正式会话时发 SessionChanged(Immediate)。
        stub.RaiseNotify(new AiNotification(AiNotificationKind.SessionChanged, "s-draft1",
            Session: new AiSessionSummary("s-draft1", "你好", AiMode.ConfirmEach, "gw", "m1",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 0, null, AiSessionPersistence.Immediate)));

        Assert.Equal(before + 1, vm.Sessions.Count);
        Assert.Contains(vm.Sessions, s => s.SessionId == "s-draft1" && s.Title == "你好");
    }

    [Fact]
    public async Task 关页_未用草稿被回收()
    {
        var (vm, stub) = NewVm();
        await vm.LoadAsync();
        await vm.NewSessionAsync();

        vm.Dispose();

        Assert.Contains("s-draft1", stub.DiscardedDrafts);
    }
}
