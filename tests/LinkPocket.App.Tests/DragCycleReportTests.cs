using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 拖拽成环弹窗的判据回归（用户报障 2026-09-19）：
/// 拖 A 到**同目录下的另一个文件夹** B 明明合法，却弹出"不能移到它自己或它的子文件夹里"，而且弹窗之后
/// A 确实搬过去了 —— 根因是判定按"拖拽**途中**经过过谁"记账（拖拽必然从源行出发，起点自己就是"拖到它自己"，
/// 一置位就再也不清零）→ 修复后的唯一判据是**松手落点**。
///
/// <para>本文件锁住的语义：<see cref="BrowserViewModel.ReportBlockedDropIfCycle"/> 只在松手落点确实是被拖项
/// 自身或其后代时才弹窗；落点是合法文件夹或空白一律不弹（Windows 同为"只在真正放下时报错"）。</para>
/// </summary>
public class DragCycleReportTests
{
    private sealed class RecordingDialogs : IDialogService
    {
        public List<(string Title, string Message)> Alerts { get; } = new();

        public bool ConfirmDeleteFolder(string folderName) => true;

        public bool Confirm(string title, string message, string confirmText = "删除", string iconKind = "delete-outline") => true;

        public void Alert(string title, string message) => Alerts.Add((title, message));
    }

    private static BrowserViewModel NewVm(LinkPocket.Contracts.EngineClient client, RecordingDialogs dialogs)
        => new(client, new UiPortProvider { Dialogs = dialogs });

    /// <summary>用户报障场景：从主栏把 A 拖到同目录下的 B（合法）→ 不得弹窗。</summary>
    [Fact]
    public async Task 拖到同目录的另一个文件夹_合法落点_不弹窗()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            var items = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == a.FolderId));
            vm.ReportBlockedDropIfCycle(items, b.FolderId);      // 松手落在 B

            Assert.Empty(dialogs.Alerts);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 松手落在被拖文件夹自己身上_弹窗说明()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            var items = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == a.FolderId));
            vm.ReportBlockedDropIfCycle(items, a.FolderId);

            var (title, message) = Assert.Single(dialogs.Alerts);
            Assert.Equal("无法移动", title);
            Assert.Contains("A", message);
            Assert.Contains("子文件夹", message);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 松手落在被拖文件夹的后代身上_弹窗说明()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var sub = (await client.FolderCreateAsync("Sub", parentId: a.FolderId)).Data!;
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            var items = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == a.FolderId));
            vm.ReportBlockedDropIfCycle(items, sub.FolderId);

            Assert.Single(dialogs.Alerts);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>松手在空白/非落点 = 无操作（Explorer 口径），不算非法尝试。</summary>
    [Fact]
    public async Task 松手在空白_不弹窗()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            var items = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == a.FolderId));
            vm.ReportBlockedDropIfCycle(items, null);

            Assert.Empty(dialogs.Alerts);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>多选（A + 它的子文件夹）拖到那个子文件夹上 = 把子文件夹放进它自己 → 非法（集合成员也要判）。</summary>
    [Fact]
    public async Task 多选拖拽_落点是集合成员之一_仍判非法()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var sub = (await client.FolderCreateAsync("Sub", parentId: a.FolderId)).Data!;
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            // 树选中 A（进入 A，集合 = {A}）→ Ctrl 加选主栏行 Sub → 集合 = {A, Sub}
            var nodeA = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            await vm.SelectTreeNodeAsync(nodeA);
            vm.SelectRowWithModifiers(vm.Rows.Single(r => r.Id == sub.FolderId),
                System.Windows.Input.ModifierKeys.Control);

            var items = vm.PrepareDragFromNode(nodeA);
            Assert.Equal(2, items.Count);

            vm.ReportBlockedDropIfCycle(items, sub.FolderId);

            Assert.Single(dialogs.Alerts);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}
