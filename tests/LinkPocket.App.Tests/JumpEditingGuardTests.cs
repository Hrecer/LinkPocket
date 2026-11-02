using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.I18n;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 跳转与浏览页编辑面的仲裁（编辑优先）：就地改名 / 整页编辑器打开时，任何入口的跳转
/// 都不得改目录、改选中或收起编辑；详情覆盖层不是编辑面，跳转先关它再落点。
/// 所有跳转入口共用 <see cref="IContentLocator"/> → <see cref="IBrowserLocateHost"/> →
/// <see cref="BrowserViewModel.NavigateAndSelectAsync"/> 这一条流水线，故回归钉在共享层。
/// </summary>
public class JumpEditingGuardTests
{
    [Fact]
    public async Task 跳转_就地改名中_拒绝跳转且编辑目录选中都不变()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("F")).Data!;
            var linkA = (await client.LinkCreateAsync("https://a.example", title: "A",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;
            var linkB = (await client.LinkCreateAsync("https://b.example", title: "B",
                autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(folder.FolderId);
            var rowA = vm.Rows.Single(r => r.Id == linkA.LinkId);
            vm.SelectRowWithModifiers(rowA, ModifierKeys.None);
            vm.BeginRenameRow(rowA);
            vm.EditingName = "A 改名中";
            Assert.Equal(linkA.LinkId, vm.EditingEntityId);
            Assert.True(vm.IsEditingEntity(linkA.LinkId));

            var navigated = await vm.NavigateAndSelectAsync(null, linkB.LinkId);

            Assert.False(navigated);
            Assert.True(vm.IsEditingInProgress);
            Assert.True(vm.IsRenaming);
            Assert.Equal("A 改名中", vm.EditingName);
            Assert.Equal(folder.FolderId, vm.CurrentFolderId);
            Assert.Contains(linkA.LinkId, vm.Selection.Ids);
            Assert.DoesNotContain(linkB.LinkId, vm.Selection.Ids);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 跳转_整页编辑器打开_拒绝跳转且编辑页保持()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("F")).Data!;
            var linkA = (await client.LinkCreateAsync("https://a.example", title: "A",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;
            var linkB = (await client.LinkCreateAsync("https://b.example", title: "B",
                autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(folder.FolderId);
            vm.OpenEditorForEdit(linkA.LinkId);

            var navigated = await vm.NavigateAndSelectAsync(null, linkB.LinkId);

            Assert.False(navigated);
            Assert.True(vm.IsEditingInProgress);
            Assert.True(vm.IsEditorPageOpen);
            Assert.Equal(folder.FolderId, vm.CurrentFolderId);
            Assert.DoesNotContain(linkB.LinkId, vm.Selection.Ids);
            vm.CloseEditorPage();
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 取消整页编辑后_编辑态归零_同一链接不再被判定为正在编辑()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://a.example", title: "A",
                autoFetchMetadata: false)).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            vm.OpenEditorForEdit(link.LinkId);

            Assert.True(vm.IsEditingInProgress);
            Assert.Equal(link.LinkId, vm.EditingEntityId);
            Assert.True(vm.IsEditingEntity(link.LinkId));

            vm.CloseEditorPage();

            Assert.False(vm.IsEditingInProgress);
            Assert.Null(vm.EditorPage);                // 旧编辑页对象必须释放
            Assert.Null(vm.EditingEntityId);           // 不得再从旧对象泄漏 ID
            Assert.False(vm.IsEditingEntity(link.LinkId));   // 取消后跨页删除不再被误阻
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 跳转_详情覆盖层打开_先关详情再选中目标且不累加旧链接()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("F")).Data!;
            var linkA = (await client.LinkCreateAsync("https://a.example", title: "A",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;
            var linkB = (await client.LinkCreateAsync("https://b.example", title: "B",
                autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(folder.FolderId);
            var rowA = vm.Rows.Single(r => r.Id == linkA.LinkId);
            await vm.OpenDetailPageAsync(rowA);
            Assert.True(vm.IsDetailPageOpen);
            Assert.True(vm.Selection.Contains(linkA.LinkId));

            var navigated = await vm.NavigateAndSelectAsync(null, linkB.LinkId);

            Assert.True(navigated);
            Assert.False(vm.IsDetailPageOpen);
            Assert.Null(vm.CurrentFolderId);
            Assert.Equal(linkB.LinkId, Assert.Single(vm.SelectedRows).Id);
            Assert.Equal(new[] { linkB.LinkId }, vm.Selection.Ids.ToArray());
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 详情返回还原选中_是覆盖式_不会把旧链接累加回来()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var linkA = (await client.LinkCreateAsync("https://a.example", title: "A",
                autoFetchMetadata: false)).Data!;
            var linkB = (await client.LinkCreateAsync("https://b.example", title: "B",
                autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var rowA = vm.Rows.Single(r => r.Id == linkA.LinkId);
            vm.SelectRowWithModifiers(rowA, ModifierKeys.None);

            vm.RestoreSelection(linkB.LinkId);

            Assert.Equal(new[] { linkB.LinkId }, vm.Selection.Ids.ToArray());
            Assert.Equal(linkB.LinkId, Assert.Single(vm.SelectedRows).Id);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 就地改名中_Esc_取消编辑且不改选中()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://a.example", title: "A",
                autoFetchMetadata: false)).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var row = vm.Rows.Single(r => r.Id == link.LinkId);
            vm.SelectRowWithModifiers(row, ModifierKeys.None);
            vm.BeginRenameRow(row);
            vm.EditingName = "未提交的输入";

            Assert.True(vm.EscapeCommand.CanExecute(null));
            vm.EscapeCommand.Execute(null);

            Assert.False(vm.IsRenaming);
            Assert.Contains(link.LinkId, vm.Selection.Ids);
            Assert.Equal("A", (await client.LinkGetAsync(link.LinkId)).Title);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 定位组件_浏览页正在编辑_返回EditingInProgress且不切页不落点()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://a.example", title: "A",
                autoFetchMetadata: false)).Data!;
            var host = new FakeLocateHost { IsEditingInProgress = true };
            var notices = new FakeNoticeService();
            var locator = new ContentLocator(client, () => host, new UiPortProvider { Notices = notices });

            var result = await locator.LocateLinkAsync(link.LinkId);

            Assert.Equal(LocateStatus.EditingInProgress, result.Status);
            Assert.NotNull(result.Message);
            Assert.True(result.UserNotified);
            Assert.Single(notices.Messages);          // 阻断提示走共享 NoticeBar，不再由调用方弹窗
            Assert.False(host.ShowBrowserCalled);
            Assert.False(host.EnterCalled);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 搜索页删除_命中正在编辑的链接_整批拒绝且不弹确认不删库()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://a.example", title: "A",
                autoFetchMetadata: false)).Data!;
            var dialogs = new FakeDialogService();
            var notices = new FakeNoticeService();
            var vm = new SearchViewModel(client, new FakeNavigationService(), dialogs,
                _ => LocValue.Empty, locator: null,
                editState: new FakeEditState { EditingId = link.LinkId }, notices: notices);
            vm.Query = "A";
            vm.SearchCommand.Execute(null);
            Assert.True(await WaitUntilAsync(() => vm.Results is { Count: > 0 }, TimeSpan.FromSeconds(5)),
                "搜索结果未在超时内出现");
            vm.Selection.SelectSingle(link.LinkId);

            vm.DeleteSelectionCommand.Execute(null);

            Assert.Equal(0, dialogs.ConfirmCalls);     // 编辑优先：连确认框都不弹
            Assert.Single(notices.Messages);           // 共享 NoticeBar 给出阻断提示
            Assert.NotNull(await client.LinkGetAsync(link.LinkId));   // 库里的链接还在
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 浏览页删除命令_就地改名中直接执行_也不删正在编辑的实体()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://a.example", title: "A",
                autoFetchMetadata: false)).Data!;
            var dialogs = new FakeDialogService();
            var notices = new FakeNoticeService();
            var vm = new BrowserViewModel(client, new UiPortProvider { Dialogs = dialogs, Notices = notices });
            await vm.LoadAsync(null);
            var row = vm.Rows.Single(r => r.Id == link.LinkId);
            vm.SelectRowWithModifiers(row, ModifierKeys.None);
            vm.BeginRenameRow(row);

            vm.DeleteSelectionCommand.Execute(null);   // 绕过 CanExecute 的直接执行

            Assert.NotNull(await client.LinkGetAsync(link.LinkId));   // 编辑目标还在
            Assert.Single(notices.Messages);                          // 共享提示条给出阻断提示
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public void 去重删除_勾选含正在编辑的链接_整批阻断并提示()
    {
        var notices = new FakeNoticeService();
        var vm = new ToolsViewModel(null!, null,
            _ => Task.FromResult(LocValue.Empty), () => Task.CompletedTask,
            new FakeEditState { EditingId = "L1" }, notices);

        Assert.True(vm.BlockDestructiveIfEditing(new[] { "L1", "L2" }));   // 整批拒绝，不删一半
        Assert.Single(notices.Messages);
        Assert.False(vm.BlockDestructiveIfEditing(new[] { "L2" }));        // 未编辑的项照常放行
        Assert.Single(notices.Messages);
    }

    private sealed class FakeLocateHost : IBrowserLocateHost
    {
        public bool IsEditingInProgress { get; set; }
        public bool ShowBrowserCalled { get; private set; }
        public bool EnterCalled { get; private set; }

        public void ShowBrowser() => ShowBrowserCalled = true;

        public Task<bool> EnterAndSelectAsync(string? folderId, string rowId)
        {
            EnterCalled = true;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeEditState : IBrowseEditState
    {
        public string? EditingId { get; init; }
        public bool IsEditing => EditingId != null;
        public string? EditingEntityId => EditingId;
        public bool IsEditingEntity(string id) => string.Equals(id, EditingId, StringComparison.Ordinal);
    }

    private sealed class FakeNoticeService : IUiNoticeService
    {
        public List<string> Messages { get; } = new();
        public void Show(string text) => Messages.Add(text);
    }

    private sealed class FakeDialogService : IDialogService
    {
        public int ConfirmCalls { get; private set; }
        public bool ConfirmDeleteFolder(string folderName) => true;
        public bool Confirm(string title, string message, string? confirmText = null, string iconKind = "delete-outline")
        {
            ConfirmCalls++;
            return true;
        }
        public void Alert(string title, string message) { }
    }

    private sealed class FakeNavigationService : INavigationService
    {
        public void OpenLinkInBrowser(string linkId) { }
        public void OpenFolderInBrowser(string folderId) { }
        public void NavigateToSearch() { }
        public Task RefreshTrashPageAsync() => Task.CompletedTask;
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
