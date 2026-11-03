using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 撤销栈行为回归：容量淘汰方向、撤销/重做的原子收尾、批量登记合并。
/// 这些用例钉住 2026-11-03 修复的三个真实缺陷：
/// ① 满 100 条后新动作被立刻弹掉（Ctrl+Z 去撤一条很旧的操作）；
/// ② 撤销 + 重做各自多次整索引写盘（大条目撤销/重做线性变慢）；
/// ③ 重做成功后清空剩余重做链（连续 Ctrl+Y 失效）。
/// </summary>
public class UndoStackBehaviorTests
{
    private static readonly CommandDescriptor Descriptor =
        new("test.x", "test", "d", [], CommandCaps.Mutation, UndoInverse: "test.y");

    private static JsonElement Args(int n) => JsonSerializer.SerializeToElement(new { n });

    [Fact]
    public async Task 超过容量_淘汰最旧_新动作仍可撤销()
    {
        var undo = new UndoCoordinator();
        for (var i = 0; i < 105; i++) undo.Record(Descriptor, Args(i), CallerRef.Test);

        var list = await undo.ListAsync(default);
        Assert.Equal(100, list.Count);
        Assert.Equal(104, list[0].Steps[0].Args.GetProperty("n").GetInt32());   // 最新在栈顶
        Assert.Equal(5, list[^1].Steps[0].Args.GetProperty("n").GetInt32());    // 最旧 5 条被淘汰

        var taken = await undo.TakeUndoAsync(null, default);
        Assert.NotNull(taken);
        Assert.Equal(104, taken!.Steps[0].Args.GetProperty("n").GetInt32());    // 撤销的是最新动作
    }

    [Fact]
    public async Task 撤销成功_原子转入重做栈()
    {
        var undo = new UndoCoordinator();
        undo.Record(Descriptor, Args(1), CallerRef.Test);
        undo.Record(Descriptor, Args(2), CallerRef.Test);

        var peek = await undo.PeekUndoAsync(null, default);
        var completed = await undo.CompleteUndoAsync(peek!.Id, default);

        Assert.NotNull(completed);
        Assert.Equal(2, completed!.Steps[0].Args.GetProperty("n").GetInt32());
        Assert.Single(await undo.ListAsync(default));   // 只弹出被撤销的那条，n=1 仍在栈里
        var redo = await undo.ListRedoAsync(default);
        Assert.Single(redo);
        Assert.Equal(2, redo[0].Steps[0].Args.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task 重做成功_保留剩余重做链()
    {
        var undo = new UndoCoordinator();
        undo.Record(Descriptor, Args(1), CallerRef.Test);
        undo.Record(Descriptor, Args(2), CallerRef.Test);

        // 连续撤销两条 → 重做栈：2（顶）、1（底）
        for (var i = 0; i < 2; i++)
        {
            var peek = await undo.PeekUndoAsync(null, default);
            await undo.CompleteUndoAsync(peek!.Id, default);
        }

        var first = await undo.CompleteRedoAsync(CallerRef.Test, default);
        Assert.NotNull(first);
        Assert.Equal(1, first!.Steps[0].Args.GetProperty("n").GetInt32());   // 先重做最早被撤的（栈顶是 1）
        Assert.Single(await undo.ListRedoAsync(default));                     // 剩余一条仍在重做栈

        var second = await undo.CompleteRedoAsync(CallerRef.Test, default);
        Assert.Equal(2, second!.Steps[0].Args.GetProperty("n").GetInt32());
        Assert.Empty(await undo.ListRedoAsync(default));

        var undoList = await undo.ListAsync(default);
        Assert.Equal(2, undoList.Count);
        Assert.Equal(2, undoList[0].Steps[0].Args.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task 批量登记_同组合并为一条_不同组分开()
    {
        var undo = new UndoCoordinator();
        undo.RecordMany(
        [
            new UndoRegistration(Descriptor, Args(1), null, "g1"),
            new UndoRegistration(Descriptor, Args(2), null, "g1"),
            new UndoRegistration(Descriptor, Args(3), null, "g1"),
            new UndoRegistration(Descriptor, Args(9), null, "g2"),
        ], CallerRef.Test);

        var list = await undo.ListAsync(default);
        Assert.Equal(2, list.Count);
        Assert.Equal("g2", list[0].GroupId);
        Assert.Single(list[0].Steps);
        Assert.Equal("g1", list[1].GroupId);
        Assert.Equal(3, list[1].Steps.Count);
    }
}
