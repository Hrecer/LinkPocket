using LinkPocket.Contracts;
using LinkPocket.UI.Ai;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 台账行的来源/不一致投影（功能书 §7.1）：引擎 diff = 默认口径不标注；
/// 仅实体级 / AI 对账 / 引擎与对账不一致 → 各自投影到对应文案键（键存在性由 i18n 闸兜底）。
/// </summary>
public class AiChangeRowTests
{
    private static AiChange Change(AiChangeSource source, bool mismatch = false)
        => new(
            ChangeId: "d-1",
            Seq: 1,
            TurnId: "t-1",
            CallId: "c-1",
            Command: "links.update",
            Kind: AiChangeKind.Update,
            EntityType: "link",
            EntityId: "L1",
            EntityName: "标题",
            EntityPath: "@root",
            EntityExists: true,
            Fields: null,
            Outcome: AiChangeOutcome.Applied,
            ErrorCode: null,
            Undoable: false,
            Source: source,
            Truncated: false,
            Omitted: 0,
            At: DateTimeOffset.UtcNow,
            CorrelationId: null,
            BatchId: null,
            ReconcileMismatch: mismatch);

    [Fact]
    public void 来源投影_引擎diff不标注_仅实体级与对账各自成键()
    {
        var engineRow = new AiChangeRow { Change = Change(AiChangeSource.EngineDiff) };
        Assert.Null(engineRow.SourceKey);           // 引擎 diff 是默认口径，不加噪音
        Assert.False(engineRow.HasSourceNote);

        var entityOnly = new AiChangeRow { Change = Change(AiChangeSource.EntityOnly) };
        Assert.Equal("ai.diff.source.entityOnly", entityOnly.SourceKey);
        Assert.True(entityOnly.HasSourceNote);

        var reconciled = new AiChangeRow { Change = Change(AiChangeSource.Reconciled) };
        Assert.Equal("ai.diff.source.reconciled", reconciled.SourceKey);
        Assert.True(reconciled.HasSourceNote);
    }

    [Fact]
    public void 不一致投影_只在真不一致时出键()
    {
        var clean = new AiChangeRow { Change = Change(AiChangeSource.EngineDiff) };
        Assert.Null(clean.MismatchKey);
        Assert.False(clean.HasMismatch);

        var mismatch = new AiChangeRow { Change = Change(AiChangeSource.EngineDiff, mismatch: true) };
        Assert.Equal("ai.diff.reconcile.mismatch", mismatch.MismatchKey);
        Assert.True(mismatch.HasMismatch);
    }
}
