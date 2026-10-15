using System;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 本页写操作后的「立即真刷新」金标准（2026-10-15 口径）：
/// 新建 / 删除（链接与文件夹）/ 编辑器保存完成后，界面行由**真查询**立即更新——
/// ① 不再自造行（乐观插入已移除：显示的行必须来自查询结果）；
/// ② 不再等 300ms 事件防抖那一拍（写后立即刷，事件防抖继续兜外部变更）。
/// </summary>
public class LocalWriteRefreshTests
{
    /// <summary>自动确认对话框（删除确认直接放行；本文件不测弹窗语义——那是 DragCycleReportTests 等的地盘）。</summary>
    private sealed class AutoDialogs : IDialogService
    {
        public bool ConfirmDeleteFolder(string folderName) => true;
        public bool Confirm(string title, string message, string? confirmText = null, string iconKind = "delete-outline") => true;
        public void Alert(string title, string message) { }
    }

    private static BrowserViewModel NewVm(LinkPocket.Contracts.EngineClient client)
        => new(client, new UiPortProvider { Dialogs = new AutoDialogs() });

    [Fact]
    public async Task 新建文件夹_行来自真查询_且不等防抖_进入改名时行已就位()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.FolderCreateAsync("A");
            var vm = NewVm(client);
            await vm.LoadAsync(null);
            var before = vm.Rows.Count;

            vm.NewFolderCommand.Execute(null);
            Assert.True(await WaitUntilAsync(() => vm.IsRenaming, TimeSpan.FromSeconds(5)),
                $"新建未在超时内进入改名（状态：{vm.StatusText.Resolve()}）");

            // 行数 +1 且末行就是它：**没有**任何手动刷新调用，行是随写的立即刷新落地的
            Assert.Equal(before + 1, vm.Rows.Count);
            var created = Assert.Single(vm.SelectedRows);
            Assert.Equal(created.Id, vm.Rows[^1].Id);
            // 交叉核对：这一行的实体真在库里（显示的不是自造行）
            Assert.Contains(await client.FolderTreeAsync(), n => n.FolderId == created.Id);
            // 状态文案在刷新之后落，不被刷新链结尾的“共 N 项”盖掉
            Assert.StartsWith("已创建文件夹", vm.StatusText.Resolve());
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 删除链接_行随立即刷新消失_不等防抖()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://del.example", title: "D",
                autoFetchMetadata: false)).Data!;
            var vm = NewVm(client);
            await vm.LoadAsync(null);
            Assert.Single(vm.Rows);

            vm.SelectRowWithModifiers(vm.Rows[0], System.Windows.Input.ModifierKeys.None);
            vm.DeleteSelectionCommand.Execute(null);

            // 无任何手动刷新：行必须自己消失（写后立即刷新的落点）
            Assert.True(await WaitUntilAsync(() => vm.Rows.Count == 0, TimeSpan.FromSeconds(5)),
                $"删除后行未立即消失（状态：{vm.StatusText.Resolve()}）");
            Assert.Contains(await client.TrashListAsync(), e => e.Id == link.LinkId);   // 库里确实已进回收站
            Assert.False(vm.HasSelection);                             // 删后清选中
            Assert.Equal("已删除 1 项", vm.StatusText.Resolve());      // 状态文案不被“共 N 项”盖掉
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 删除文件夹_行随立即刷新消失()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("要删的文件夹")).Data!;
            var vm = NewVm(client);
            await vm.LoadAsync(null);
            Assert.Single(vm.Rows);

            vm.SelectRowWithModifiers(vm.Rows[0], System.Windows.Input.ModifierKeys.None);
            vm.DeleteSelectionCommand.Execute(null);

            Assert.True(await WaitUntilAsync(() => vm.Rows.Count == 0, TimeSpan.FromSeconds(5)),
                $"删除文件夹后行未立即消失（状态：{vm.StatusText.Resolve()}）");
            Assert.Contains(await client.TrashListAsync(), e => e.Id == folder.FolderId);  // 已进回收站
            Assert.Equal("已删除 1 项", vm.StatusText.Resolve());
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 编辑器保存_新链接立即出现在列表_不等防抖()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var vm = NewVm(client);
            await vm.LoadAsync(null);

            vm.NewLinkCommand.Execute(null);
            var editor = Assert.IsType<LinkEditorViewModel>(vm.EditorPage);
            editor.Url = "https://new.example/page";
            editor.LinkTitle = "新书签";
            editor.SaveCommand.Execute(null);

            Assert.True(await WaitUntilAsync(
                    () => !vm.IsEditorPageOpen && vm.Rows.Any(r => r.Name == "新书签"),
                    TimeSpan.FromSeconds(5)),
                "保存后新行未立即出现在列表（无手动刷新）");
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }
}
