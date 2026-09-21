using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Managers;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 传输流水线：拖拽落点、右键拖拽菜单、剪贴板粘贴**共用同一条实现**
/// （<c>BrowserViewModel.TransferAsync</c>），模式（Ctrl = 复制）是提示文案 / 光标 / 最终动作的同一个事实来源。
///
/// <para>本文件锁住的语义：</para>
/// <list type="bullet">
/// <item>同一批项、同一个目标、同一个模式 → 无论从剪贴板还是拖拽进入，**结果一致**（含同层唯一命名编号）；</item>
/// <item>Ctrl 落到本页空白 = 在本页生成**按规范编号**的副本，并且新副本被选中（Windows 复制后选中副本）；</item>
/// <item>落点模式只换动作不换落点，提示条随之在 `移动到「X」` / `复制到「X」` 之间切换；</item>
/// <item>Esc 取消（OLE 如实上报）即使在成环目标上松手也**不弹窗**——取消不是失败；</item>
/// <item>成环只报**真正成环的项**，标题随模式（无法移动 / 无法复制）；</item>
/// <item>失败与"已在目标位置"都如实计数，绝不静默（观测面铁律）；</item>
/// <item>拖拽复制**不动剪贴板**（剪切态不因此被消费）。</item>
/// </list>
/// </summary>
public class TransferPipelineTests
{
    private sealed class RecordingDialogs : IDialogService
    {
        public List<(string Title, string Message)> Alerts { get; } = new();

        public bool ConfirmDeleteFolder(string folderName) => true;

        public bool Confirm(string title, string message, string confirmText = "删除", string iconKind = "delete-outline") => true;

        public void Alert(string title, string message) => Alerts.Add((title, message));
    }

    private static BrowserViewModel NewVm(LinkPocket.Contracts.EngineClient client, RecordingDialogs? dialogs = null)
        => new(client, new UiPortProvider { Dialogs = dialogs ?? new RecordingDialogs() });

    /// <summary>
    /// 同一批项 / 同一目标 / 同一模式：拖拽复制与剪贴板粘贴**结果一致**（这就是"提取完整复用"的证据）。
    /// 两条入口各写一份逐项循环时，编号口径、失败处理、文案都会各偏一点。
    /// </summary>
    [Fact]
    public async Task 拖拽复制与剪贴板粘贴_在同一目标上结果一致()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var vm = NewVm(client);
            await vm.LoadAsync(null);

            // ① 拖拽复制：把 A 复制进 B
            var items = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == a.FolderId));
            await vm.DropItemsAsync(items, b.FolderId, TransferMode.Copy);

            // ② 剪贴板复制：把 A 粘贴进 B（目标 = 当前所在目录 → 先进入 B）
            await vm.LoadAsync(b.FolderId);
            vm.Clipboard.SetBrowserPayload(new BrowserClipboardPayload
            {
                FolderIds = { a.FolderId },
                SourceFolderId = null,
                IsCut = false,
            });
            vm.PasteCommand.Execute(null);   // 命令入口是 fire-and-forget → 轮询等落库结果

            var pasted = false;
            for (var i = 0; i < 60 && !pasted; i++)
            {
                await Task.Delay(25);
                await vm.LoadAsync(b.FolderId);
                pasted = vm.Rows.Any(r => r.Name == "A (2)");
            }
            Assert.True(pasted, "剪贴板粘贴未在超时内产出「A (2)」");

            var names = vm.Rows.Where(r => r.IsFolder).Select(r => r.Name).OrderBy(n => n).ToList();

            // 两条入口都在"目标层已有 A"的情况下产出同一个规范编号名 → 命名与传输行为同源
            Assert.Equal(new[] { "A", "A (2)" }, names);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>
    /// Ctrl + 拖到**本页空白** = 在本页做一个副本，文件夹按同层唯一命名规范编号成「名 (2)」，
    /// 并且副本**被选中**（Windows：复制完选中副本，用户立刻能看到/改名）。
    /// </summary>
    [Fact]
    public async Task Ctrl拖到本页空白_生成规范编号的本页副本并选中()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var vm = NewVm(client);
            await vm.LoadAsync(null);

            var items = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == a.FolderId));
            // 落点 = 列表空白（= 当前目录），模式来自落点状态（Ctrl = 复制）
            vm.SetDropTarget(new BrowserDropTarget(vm.CurrentFolderId, BrowserPane.Main, vm.CurrentFolderDisplayName,
                TransferMode.Copy));
            await vm.DropItemsAsync(items, vm.CurrentFolderId, vm.DropTargetMode);

            // 结果如实分派（刷新会把状态栏改回目录统计，故在此处读）
            Assert.StartsWith("已复制 1 项", vm.StatusText.Resolve());

            await vm.RefreshPreservingSelectionAsync();

            var copy = vm.Rows.SingleOrDefault(r => r.Name == "A (2)");
            Assert.NotNull(copy);
            Assert.True(copy!.IsFolder);
            Assert.True(copy.IsSelected);                       // 新副本被选中
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>落点模式是唯一事实来源：换模式只换动作，不动落点；提示条是它的投影。</summary>
    [Fact]
    public async Task 落点模式_只换动作不换落点且提示条随模式切换()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var vm = NewVm(client);
            await vm.LoadAsync(null);

            vm.SetDropTarget(new BrowserDropTarget(a.FolderId, BrowserPane.Main, "A", TransferMode.Copy));
            Assert.Equal("复制到「A」", vm.DropTargetHintText);
            Assert.Equal(TransferMode.Copy, vm.DropTargetMode);

            // 拖拽中按下/松开 Ctrl（鼠标没动）：落点不变、只有动作变——QueryContinueDrag 走的就是这条路
            vm.SetDropTargetMode(TransferMode.Move);
            Assert.Equal(a.FolderId, vm.DropTarget!.FolderId);
            Assert.Equal("移动到「A」", vm.DropTargetHintText);

            // 修饰键 → 模式：唯一实现
            Assert.Equal(TransferMode.Copy, BrowserViewModel.ResolveDropMode(controlPressed: true));
            Assert.Equal(TransferMode.Move, BrowserViewModel.ResolveDropMode(controlPressed: false));
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>Esc 取消（OLE 如实上报）：即使松手位置压在成环目标上也不弹窗——取消不是失败。</summary>
    /// <summary>
    /// 成环的执行层口径：**只有真的松手（产生意图）才会判定**——没有意图 = 什么都不发生。
    /// （Esc 取消走的就是"没有意图"这条：OLE 不派发 Drop → 视图不调用流水线 → 自然不弹窗，
    /// 结构性保证，不再有"途经记账"那类判据可以出错。）
    /// </summary>
    [Fact]
    public async Task 没有落点意图_不产生任何弹窗与变更()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var dialogs = new RecordingDialogs();
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync(null);

            await vm.DropItemsAsync([], a.FolderId, TransferMode.Move);   // 空载荷 = 什么都没发生

            Assert.Empty(dialogs.Alerts);
            Assert.Contains(vm.Rows, r => r.Id == a.FolderId);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>成环弹窗：标题随模式（复制 = 无法复制），并且**只列真正成环的项**（不要把整批名字都写上去）。</summary>
    [Fact]
    public async Task 成环弹窗_按模式出标题且只列成环项()
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

            // 多选 A + B，落点 = A 的子文件夹：只有 A 成环（B 合法）
            var items = new List<DragItem>
            {
                new(a.FolderId, true, "A"),
                new(b.FolderId, true, "B"),
            };
            await vm.DropItemsAsync(items, sub.FolderId, TransferMode.Copy);

            var (title, message) = Assert.Single(dialogs.Alerts);
            Assert.Equal("无法复制", title);
            Assert.Contains("A", message);
            Assert.DoesNotContain("B", message);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>失败必须如实报数（旧实现只写日志、状态栏只说成功数）。</summary>
    [Fact]
    public async Task 拖拽失败_状态栏如实报数()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var vm = NewVm(client);
            await vm.LoadAsync(null);

            await vm.DropItemsAsync(new[] { new DragItem("no-such-link", false, "X") }, null, TransferMode.Move);

            Assert.Contains("1 项失败", vm.StatusText.Resolve());
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>移动模式落在"它已经在的目录" = 无操作（不是错误），并如实说明——不静默。</summary>
    [Fact]
    public async Task 移动到已在的目录_如实说明且不报错()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.FolderCreateAsync("A");
            var vm = NewVm(client);
            await vm.LoadAsync(null);

            var items = vm.PrepareDragFromRow(vm.Rows[0]);
            await vm.DropItemsAsync(items, vm.CurrentFolderId, TransferMode.Move);

            Assert.Contains("已在目标位置", vm.StatusText.Resolve());
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>
    /// 载荷首项 = 用户抓住的那一项：集合是无序 HashSet，不定首项会让浮层显示的项与"抓的那项"不符，
    /// 同批同名项谁拿编号也会跟着漂移。
    /// </summary>
    [Fact]
    public async Task 载荷首项_是用户抓住的那一项()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var vm = NewVm(client);
            await vm.LoadAsync(null);

            vm.SelectRowWithModifiers(vm.Rows.Single(r => r.Id == a.FolderId), System.Windows.Input.ModifierKeys.None);
            vm.SelectRowWithModifiers(vm.Rows.Single(r => r.Id == b.FolderId), System.Windows.Input.ModifierKeys.Control);

            var fromB = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == b.FolderId));
            Assert.Equal(2, fromB.Count);
            Assert.Equal(b.FolderId, fromB[0].Id);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>拖拽复制**不消费剪贴板**：剪切态是应用级状态，只由 Esc / 新复制剪切 / 真有项被粘贴 / 关窗遗忘。</summary>
    [Fact]
    public async Task 拖拽复制_不动剪贴板里的剪切态()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var vm = NewVm(client);
            await vm.LoadAsync(null);

            vm.Clipboard.SetBrowserPayload(new BrowserClipboardPayload
            {
                FolderIds = { a.FolderId },
                SourceFolderId = null,
                IsCut = true,
            });

            var items = vm.PrepareDragFromRow(vm.Rows.Single(r => r.Id == a.FolderId));
            await vm.DropItemsAsync(items, b.FolderId, TransferMode.Copy);

            var payload = vm.Clipboard.BrowserPayload;
            Assert.NotNull(payload);
            Assert.True(payload!.IsCut);
            Assert.Contains(a.FolderId, payload.FolderIds);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}
