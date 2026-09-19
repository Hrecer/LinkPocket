using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 剪切/复制/粘贴的**边界清单**（Explorer 口径）：
/// <list type="bullet">
/// <item>剪切到源目录 = 无操作 + 明确状态栏提示（<b>不弹窗</b>，载荷保留）；</item>
/// <item>把文件夹粘贴进**它自己或它的子文件夹** = 拒绝 + **规范弹窗**说明（Explorer 也拒绝并报错，绝不静默）；</item>
/// <item>复制到**同目录** = 合法（引擎自动编号「名 (2)」）；</item>
/// <item>混合批 = 合法项照常执行、非法项单独弹窗说明、失败项如实报数（三者不混淆）。</item>
/// </list>
/// 弹窗断言经 <see cref="IDialogService"/> 端口注入记录件完成——与产品同一条端口路径（无 UI 时不弹系统窗）。
/// </summary>
public class PasteBoundaryTests
{
    /// <summary>记录型对话框端口（断言"是否弹窗、弹了什么"）。</summary>
    private sealed class RecordingDialogs : IDialogService
    {
        public List<(string Title, string Message)> Alerts { get; } = new();

        public bool ConfirmDeleteFolder(string folderName) => true;

        public bool Confirm(string title, string message, string confirmText = "删除", string iconKind = "delete-outline") => true;

        public void Alert(string title, string message) => Alerts.Add((title, message));
    }

    private static BrowserViewModel NewVm(LinkPocket.Contracts.EngineClient client, RecordingDialogs dialogs)
        => new(client, new UiPortProvider { Dialogs = dialogs });

    [Fact]
    public async Task 粘贴_剪切文件夹到自己的子文件夹_弹窗拒绝且不移动()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B", parentId: a.FolderId)).Data!;
            await client.LinkCreateAsync("https://a.example/1", title: "L", listId: a.FolderId, autoFetchMetadata: false);

            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            vm.SelectRowWithModifiers(vm.Rows.First(r => r.Id == a.FolderId), ModifierKeys.None);
            vm.CutCommand.Execute(null);

            await vm.LoadAsync(b.FolderId);   // 进入 A / B（A 是当前目录的祖先）
            vm.PasteCommand.Execute(null);

            Assert.True(await WaitUntilAsync(() => dialogs.Alerts.Count > 0, TimeSpan.FromSeconds(5)),
                $"成环粘贴应明确弹窗说明（状态：{vm.StatusText}）");
            var (title, message) = dialogs.Alerts[0];
            Assert.Equal("无法移动", title);
            Assert.Contains("子文件夹", message);
            Assert.Contains("A", message);

            // 未被移动：A 仍在根级；剪切载荷保留（用户可粘贴到合法位置）
            Assert.Null((await client.FolderGetAsync(a.FolderId)).ParentId);
            Assert.NotNull(vm.Clipboard.BrowserPayload);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 粘贴_复制文件夹到自己的子文件夹_弹窗标题为无法复制()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B", parentId: a.FolderId)).Data!;

            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            vm.SelectRowWithModifiers(vm.Rows.First(r => r.Id == a.FolderId), ModifierKeys.None);
            vm.CopyCommand.Execute(null);

            await vm.LoadAsync(b.FolderId);
            vm.PasteCommand.Execute(null);

            Assert.True(await WaitUntilAsync(() => dialogs.Alerts.Count > 0, TimeSpan.FromSeconds(5)),
                $"成环复制应明确弹窗说明（状态：{vm.StatusText}）");
            Assert.Equal("无法复制", dialogs.Alerts[0].Title);

            // 未产生副本：A 下只有 B 一个子目录
            var contents = await client.FolderContentsAsync(a.FolderId);
            Assert.Single(contents.SubFolders);
            Assert.Equal("B", contents.SubFolders[0].Name);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 粘贴_混合批_合法项执行_非法项弹窗说明_不混淆()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B", parentId: a.FolderId)).Data!;
            var l = (await client.LinkCreateAsync("https://root.example/1", title: "L1", autoFetchMetadata: false)).Data!;

            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            // 多选：A（文件夹，非法目标）+ L1（链接，合法）
            vm.SelectRowWithModifiers(vm.Rows.First(r => r.Id == a.FolderId), ModifierKeys.None);
            vm.SelectRowWithModifiers(vm.Rows.First(r => r.Id == l.LinkId), ModifierKeys.Control);
            vm.CutCommand.Execute(null);

            await vm.LoadAsync(b.FolderId);
            vm.PasteCommand.Execute(null);

            Assert.True(await WaitUntilAsync(
                    async () => (await client.LinkGetAsync(l.LinkId)).ListId == b.FolderId,
                    TimeSpan.FromSeconds(5)),
                $"合法项应照常粘贴进目标目录（状态：{vm.StatusText}）");
            Assert.True(await WaitUntilAsync(() => dialogs.Alerts.Count > 0, TimeSpan.FromSeconds(5)),
                "非法项应单独弹窗说明");
            Assert.Contains("A", dialogs.Alerts[0].Message);
            Assert.Equal("A", (await client.FolderGetAsync(a.FolderId)).Name);   // A 未受影响
            Assert.Null((await client.FolderGetAsync(a.FolderId)).ParentId);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 粘贴_剪切到源目录_只提示不弹窗_且载荷保留()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            await client.LinkCreateAsync("https://a.example/1", title: "L", listId: a.FolderId, autoFetchMetadata: false);

            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(a.FolderId);

            vm.SelectRowWithModifiers(vm.Rows.First(r => !r.IsFolder), ModifierKeys.None);
            vm.CutCommand.Execute(null);
            vm.PasteCommand.Execute(null);   // 同目录

            Assert.True(await WaitUntilAsync(() => vm.StatusText.StartsWith("剪切的项目已在当前文件夹中"), TimeSpan.FromSeconds(5)),
                $"同目录粘贴应给明确提示（状态：{vm.StatusText}）");
            Assert.Empty(dialogs.Alerts);    // 无操作 ≠ 错误：不弹窗
            Assert.NotNull(vm.Clipboard.BrowserPayload);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 粘贴_复制到同目录_合法_引擎自动编号()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("工作")).Data!;
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            vm.SelectRowWithModifiers(vm.Rows.First(r => r.Id == a.FolderId), ModifierKeys.None);
            vm.CopyCommand.Execute(null);
            vm.PasteCommand.Execute(null);   // 同目录复制 = 允许（Explorer 同口径）

            Assert.True(await WaitUntilAsync(() => vm.StatusText.StartsWith("已粘贴"), TimeSpan.FromSeconds(5)),
                $"同目录复制应成功（状态：{vm.StatusText}）");
            Assert.Empty(dialogs.Alerts);
            var names = (await client.FolderContentsAsync(null)).SubFolders.Select(f => f.Name).ToList();
            Assert.Contains("工作", names);
            Assert.Contains("工作 (2)", names);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>
    /// 拖拽落到成环目标后，视图调用 <see cref="BrowserViewModel.ReportBlockedDrop"/> —— 与粘贴**同一套文案**、
    /// 同一个规范弹窗（Windows 口径：拖拽成环也报错，不是"毫无反应"）。
    /// </summary>
    [Fact]
    public async Task 拖拽_成环落点_弹窗说明与粘贴同口径()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.FolderCreateAsync("A");
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            vm.ReportBlockedDrop(new[] { vm.Rows[0] });

            var (title, message) = Assert.Single(dialogs.Alerts);
            Assert.Equal("无法移动", title);              // 拖拽 = 移动语义
            Assert.Contains("子文件夹", message);
            Assert.Contains("A", message);
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

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(25);
        }
        return await condition();
    }
}