using LinkPocket.Contracts;
using LinkPocket.UI.Ai;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// AI 页 VM 金标准（P4 部分）：@提及面板与 chip、技能条与运行、用量行、批进度与限流读数、压缩提示条。
/// 断言口径 = 可观测结果（VM 投影 / 桩记录的入参），不测实现细节。
/// </summary>
public class AiViewModelP4Tests
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
    public Task 提及_敲at检索并选入_文本插入且chip挂上_不重复弹面板()
        => StaPump.RunAsync(async () =>
        {
            var (vm, stub) = NewVm();
            await vm.LoadAsync();
            stub.MentionCandidates.Add(new AiMentionCandidate(AiMentionKind.Folder, "F1", "工作", "@root/工作"));
            vm.ComposerText = "看看 @工";
            vm.UpdateMentionQuery(vm.ComposerText, vm.ComposerText.Length);
            StaPump.PumpFor(60);

            Assert.True(vm.IsMentionPanelOpen);
            Assert.Equal("工", stub.MentionQueries[^1]);

            vm.CommitMentionSelection();

            Assert.Equal("看看 @工作", vm.ComposerText);
            var chip = Assert.Single(vm.Mentions);
            Assert.Equal("F1", chip.Id);
            Assert.False(vm.IsMentionPanelOpen);

            // 光标停在新选入的 `@名称` 末尾：不再弹面板（已提及过的不重复提示）
            vm.UpdateMentionQuery(vm.ComposerText, vm.ComposerText.Length);
            StaPump.PumpFor(20);
            Assert.False(vm.IsMentionPanelOpen);
        });

    [Fact]
    public Task 提及_发送时只带文本里仍在的项_chip随之更新()
        => StaPump.RunAsync(async () =>
        {
            var (vm, stub) = NewVm();
            await vm.LoadAsync();
            stub.MentionCandidates.Add(new AiMentionCandidate(AiMentionKind.Folder, "F1", "工作", null));

            vm.ComposerText = "@工";
            vm.UpdateMentionQuery(vm.ComposerText, vm.ComposerText.Length);
            StaPump.PumpFor(60);
            vm.CommitMentionSelection();
            Assert.True(vm.HasMentions);

            // 文本里保留标记：发送带上提及（稳定 ID）
            vm.ComposerText = "把 @工作 里的链接整理一下";
            await vm.SendAsync();
            Assert.Equal("F1", stub.SendContexts[^1]!.Mentions!.Single().Id);
            Assert.False(vm.HasMentions);                                  // 发出即清

            // 文本里删掉标记：chip 不静默多送（当场消失）
            vm.ComposerText = "@工";
            vm.UpdateMentionQuery(vm.ComposerText, vm.ComposerText.Length);
            StaPump.PumpFor(60);
            vm.CommitMentionSelection();
            vm.ComposerText = "换个说法";
            await vm.SendAsync();
            Assert.Null(stub.SendContexts[^1]!.Mentions);
            Assert.False(vm.HasMentions);
        });

    [Fact]
    public Task 技能_有参数先展开参数行_填完运行传参()
        => StaPump.RunAsync(async () =>
        {
            var (vm, stub) = NewVm();
            await vm.LoadAsync();
            stub.Skills.Add(new AiSkill("k1", "重排", "说明", "把 {folder} 重排", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ["folder"]));
            await vm.RefreshSkillsAsync();
            Assert.True(vm.HasSkills);

            vm.BeginRunSkill(vm.Skills[0]);
            Assert.True(vm.IsSkillRunOpen);
            vm.SkillParameters[0].Value = "工作";
            await vm.RunSkillAsync();

            Assert.False(vm.IsSkillRunOpen);
            var call = Assert.Single(stub.RunSkillCalls);
            Assert.Equal("k1", call.SkillId);
            Assert.Equal("工作", call.Parameters!["folder"]);
        });

    [Fact]
    public Task 技能_无参数点击即运行()
        => StaPump.RunAsync(async () =>
        {
            var (vm, stub) = NewVm();
            await vm.LoadAsync();
            stub.Skills.Add(new AiSkill("k2", "周报", "说明", "汇总上周新增", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));
            await vm.RefreshSkillsAsync();

            vm.BeginRunSkill(vm.Skills[0]);
            Assert.False(vm.IsSkillRunOpen);
            await vm.RunSkillAsync();   // BeginRunSkill 里已发起一次；这里等它落地
            Assert.Equal("k2", stub.RunSkillCalls[0].SkillId);
        });

    [Fact]
    public Task 技能编辑器_预填与保存_门槛跟随输入()
        => StaPump.RunAsync(async () =>
        {
            var (vm, stub) = NewVm();
            await vm.LoadAsync();
            stub.MacroNames.Add("搬运宏");

            await vm.OpenSkillEditorAsync(null, preselectedTemplate: "把 {folder} 整理一下");
            Assert.True(vm.IsSkillEditorOpen);
            Assert.Equal("把 {folder} 整理一下", vm.SkillTemplate);
            Assert.False(vm.CanSaveSkill);                                 // 缺名称
            Assert.Contains("搬运宏", vm.MacroNames);

            vm.SkillName = "整理";
            vm.SkillDescription = "需要整理时";
            Assert.True(vm.CanSaveSkill);
            await vm.SaveSkillAsync();

            Assert.False(vm.IsSkillEditorOpen);
            var draft = Assert.Single(stub.SaveSkillCalls);
            Assert.Equal("整理", draft.Name);
            Assert.True(vm.HasSkills);

            await vm.DeleteSkillAsync(vm.Skills[0]);
            Assert.Single(stub.DeleteSkillCalls);
        });

    [Fact]
    public Task 用量行_读数来自会话用量()
        => StaPump.RunAsync(async () =>
        {
            var (vm, stub) = NewVm();
            await vm.LoadAsync();
            stub.SessionUsage = new AiSessionUsage(2, 3, 1200, 400);

            await vm.RefreshUsageAsync();

            Assert.Equal("ai.usage.line", vm.UsageValue.Key);
            Assert.Equal(4, vm.UsageValue.Args.Length);                    // 轮数 / 工具 / 输入 / 输出
            Assert.Equal(1200L, Convert.ToInt64(vm.UsageValue.Args.Span[2]));
        });

    [Fact]
    public Task 批进度_通知驱动确定进度_未读数时为不确定态()
        => StaPump.RunAsync(async () =>
        {
            var (vm, stub) = NewVm();
            await vm.LoadAsync();

            Assert.True(vm.ProgressTextValue.IsEmpty);            // 还没有批进度读数（进度条已退场，只留文字）

            stub.RaiseNotify(new AiNotification(AiNotificationKind.ToolProgress, "s-1",
                Progress: new AiBatchProgress("t-1", "c-1", "running", 12, 40)));

            Assert.Equal("ai.progress.steps", vm.ProgressTextValue.Key);
        });

    [Fact]
    public Task 限流_状态行如实读数_压缩_提示条入流()
        => StaPump.RunAsync(async () =>
        {
            var (vm, stub) = NewVm();
            await vm.LoadAsync();

            stub.RaiseNotify(new AiNotification(AiNotificationKind.RateLimited, "s-1",
                RateLimit: new AiRateLimitNotice("t-1", 2_500, Retried: true)));
            Assert.Equal("ai.progress.limited", vm.StatusValue.Key);
            Assert.Equal(3, Convert.ToInt32(vm.StatusValue.Args.Span[0]));   // 2.5s 向上取整 = 3 秒

            stub.RaiseNotify(new AiNotification(AiNotificationKind.ContextCompacted, "s-1",
                Compaction: new AiContextCompaction("t-1", false, 2, 800, 0)));
            Assert.Equal("ai.context.micro", LastNoticeKey(vm));

            stub.RaiseNotify(new AiNotification(AiNotificationKind.ContextCompacted, "s-1",
                Compaction: new AiContextCompaction("t-1", true, 0, 0, 0) { Failed = true }));
            Assert.Equal("ai.context.failed", LastNoticeKey(vm));
        });
}
