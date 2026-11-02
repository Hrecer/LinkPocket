using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 拖到根级（「全部书签」）：链接的根级移动必须真的落库。
///
/// <para>根因回归：<c>links.update</c> 的 <c>list_id</c> **缺省 = 归属不变**，传 null 表达不了"移到根级"
/// （命令成功、链接没动）；根级移动只认 <c>links.move_batch</c>（<c>target_list_id</c> 缺省 = 根）。
/// 旧实现把 null 传给 update，界面表现为"提示条说移动到「全部书签」、松手却毫无反应"——
/// 不只是拖拽，剪切粘贴到根目录同样受影响。</para>
/// </summary>
public class DropToRootTests
{
    private sealed class RecordingDialogs : IDialogService
    {
        public List<(string Title, string Message)> Alerts { get; } = new();

        public bool ConfirmDeleteFolder(string folderName) => true;

        public bool Confirm(string title, string message, string? confirmText = null, string iconKind = "delete-outline") => true;

        public void Alert(string title, string message) => Alerts.Add((title, message));
    }

    private static BrowserViewModel NewVm(LinkPocket.Contracts.EngineClient client, RecordingDialogs dialogs)
        => new(client, new UiPortProvider { Dialogs = dialogs });

    [Fact]
    public async Task 链接拖到根目录_真的移到根级()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("文件夹")).Data!;
            var link = (await client.LinkCreateAsync("https://example.com/root-move", "根级书签",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(folder.FolderId);
            Assert.Contains(vm.Rows, r => r.Id == link.LinkId);

            var items = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == link.LinkId));
            await vm.DropItemsAsync(items, targetFolderId: null, TransferMode.Move);

            Assert.Empty(dialogs.Alerts);
            // 源目录的行当场消失（传输收尾真刷新）
            Assert.DoesNotContain(vm.Rows, r => r.Id == link.LinkId);
            // 引擎事实：归属真的为空（不是"命令报成功但没动"）
            var after = await client.LinkGetAsync(link.LinkId!);
            Assert.NotNull(after);
            Assert.Null(after!.ListId);
            // 根目录能看到它
            await vm.LoadAsync(null);
            Assert.Contains(vm.Rows, r => r.Id == link.LinkId);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>已经在根级的链接再拖到根级 = 无操作（Skipped），不报错、不产生第二份。</summary>
    [Fact]
    public async Task 根级链接再拖到根目录_无操作不报错()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://example.com/already-root", "根级书签",
                autoFetchMetadata: false)).Data!;
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            var items = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == link.LinkId));
            await vm.DropItemsAsync(items, targetFolderId: null, TransferMode.Move);

            Assert.Empty(dialogs.Alerts);
            var after = await client.LinkGetAsync(link.LinkId!);
            Assert.NotNull(after);
            Assert.Null(after!.ListId);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}
