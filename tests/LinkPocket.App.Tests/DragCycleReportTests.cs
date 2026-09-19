using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 成环（把文件夹放进它自己或它的子文件夹里）的**统一口径**（2026-09-19 用户要求）：
/// 判定只在**执行层**（传输流水线 `TransferAsync` 的 blocked 收集）——拖拽落点、右键拖拽菜单、剪切粘贴
/// **共用同一个规范弹窗**；拖拽**悬停不再用"禁用光标 + 无提示"**（那是另一套口径），落点照常高亮 + 提示，
/// 松手后才明白告诉你为什么不能做。
///
/// <para>历史：曾按"拖拽**途经**过谁"记账 → 拖 A 到同目录的 B 也会在移动成功后误弹窗（用户报障，
/// `8d9d757` 修）。现在连"判据"本身都不存在：只有 Drop 事件（= 真正松手的那一下）才产生意图，
/// 意图交给执行层判定——"途经误报"在结构上不可能再出现；Esc 取消 = 不产生意图 = 什么都不做。</para>
///
/// <para>口径细节：只报**真正成环的那些项**（多选里只有一项成环时不要把整批名字都列出来）；
/// 标题按动作（无法移动 / 无法复制）；一项被拒绝**不影响整批里的其它项**（与粘贴逐项语义一致）。</para>
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

    /// <summary>用户报障场景：从主栏把 A 拖到同目录下的 B（合法）→ 不得弹窗，而且要真的搬过去。</summary>
    [Fact]
    public async Task 拖到同目录的另一个文件夹_合法落点_不弹窗且真的移动了()
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
            await vm.DropItemsAsync(items, b.FolderId, TransferMode.Move);

            Assert.Empty(dialogs.Alerts);
            await vm.LoadAsync(b.FolderId);
            Assert.Contains(vm.Rows, r => r.Id == a.FolderId);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 落在被拖文件夹自己身上_弹窗说明()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            var items = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == a.FolderId));
            await vm.DropItemsAsync(items, a.FolderId, TransferMode.Move);

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
    public async Task 落在被拖文件夹的后代身上_弹窗说明()
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
            await vm.DropItemsAsync(items, sub.FolderId, TransferMode.Move);

            Assert.Single(dialogs.Alerts);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>落到"当前所在文件夹"的空白上：被拖项不是它自己 → 合法（已在目标位置 = 无操作），不弹窗。</summary>
    [Fact]
    public async Task 落在当前目录空白_被拖项不是它_不弹窗()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            await client.FolderCreateAsync("B");
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            var items = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == a.FolderId));
            await vm.DropItemsAsync(items, vm.CurrentFolderId, TransferMode.Move);

            Assert.Empty(dialogs.Alerts);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>
    /// 用户实测场景（截图）：在 A 里把**A 自己**（从左栏树）拖到列表空白 → 空白 = 当前文件夹 = A 自己 → 成环。
    /// 必须弹规范弹窗，而且**弹窗时拖拽浮层已经摘除**（视图在拖拽循环退出后才执行，不在 Drop 回调里弹）。
    /// </summary>
    [Fact]
    public async Task 把当前文件夹拖到它自己的空白上_弹窗说明且未移动()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            await client.FolderCreateAsync("Sub", parentId: a.FolderId);
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(a.FolderId);

            var nodeA = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            var items = vm.PrepareDragFromNode(nodeA);
            await vm.DropItemsAsync(items, vm.CurrentFolderId, TransferMode.Move);   // 空白 = 当前文件夹 = A

            var (title, message) = Assert.Single(dialogs.Alerts);
            Assert.Equal("无法移动", title);
            Assert.Contains("A", message);

            await vm.LoadAsync(null);
            Assert.Contains(vm.Rows, r => r.Id == a.FolderId);   // 确实没被搬走
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>多选（A + 它的子文件夹？不——A + B）拖到 A 的子文件夹上：只有 A 成环 → 只报 A。</summary>
    [Fact]
    public async Task 多选拖拽_落点是集合成员之一_只报成环那一项()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var sub = (await client.FolderCreateAsync("Sub", parentId: a.FolderId)).Data!;
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            var items = new List<DragItem>
            {
                new(a.FolderId, true, "A"),
                new(b.FolderId, true, "B"),
            };
            await vm.DropItemsAsync(items, sub.FolderId, TransferMode.Move);

            var (_, message) = Assert.Single(dialogs.Alerts);
            Assert.Contains("A", message);
            Assert.DoesNotContain("B", message);

            // 逐项语义（与粘贴一致）：被拒的那一项不动，其它项照做
            await vm.LoadAsync(sub.FolderId);
            Assert.Contains(vm.Rows, r => r.Id == b.FolderId);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>复制模式成环 = 无法复制（标题按动作，不写死"移动"）。</summary>
    [Fact]
    public async Task 复制模式成环_标题是无法复制()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            var items = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == a.FolderId));
            await vm.DropItemsAsync(items, a.FolderId, TransferMode.Copy);

            var (title, _) = Assert.Single(dialogs.Alerts);
            Assert.Equal("无法复制", title);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}
