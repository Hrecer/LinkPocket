using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Input;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using LinkPocket.Views;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 回收站页 VM（"回收站浏览器"，2026-09-19 用户令：最彻底复用浏览页组件）：
/// 树含链接叶子 + 虚根「回收站」、主栏平铺（默认删除时间倒序）、面包屑 + 地址栏、唯一选中集合、
/// 永久删除（批量）、**无撤销/无剪贴板/站内不可搬移**的键位收口（站内搬移已整体移除）。
/// </summary>
public class TrashViewModelTests
{
    private sealed class RecordingDialogs : IDialogService
    {
        public List<(string Title, string Message)> Alerts { get; } = new();
        public List<(string Title, string Message)> Confirms { get; } = new();

        public bool ConfirmDeleteFolder(string folderName) => true;

        public bool Confirm(string title, string message, string confirmText = "删除", string iconKind = "delete-outline")
        {
            Confirms.Add((title, message));
            return true;
        }

        public void Alert(string title, string message) => Alerts.Add((title, message));
    }

    private static TrashViewModel NewVm(EngineClient client, RecordingDialogs? dialogs = null)
        => new(client, new UiPortProvider { Dialogs = dialogs ?? new RecordingDialogs() });

    /// <summary>造数据：单元 A（直挂 L1 + 子单元 B；B 内 L2）+ 根级单独删除的 L0。</summary>
    private static async Task<(string unitA, string unitB, string l0, string l1, string l2)> SeedAsync(EngineClient client)
    {
        var a = (await client.FolderCreateAsync("A")).Data!;
        var b = (await client.FolderCreateAsync("B", parentId: a.FolderId)).Data!;
        var l1 = (await client.LinkCreateAsync("https://a.example/", "A 链接", autoFetchMetadata: false, listId: a.FolderId)).Data!;
        var l2 = (await client.LinkCreateAsync("https://b.example/", "B 链接", autoFetchMetadata: false, listId: b.FolderId)).Data!;
        var l0 = (await client.LinkCreateAsync("https://root.example/", "根级链接", autoFetchMetadata: false)).Data!;

        await client.LinkTrashAsync(l0.LinkId);                     // 单独删除
        await client.FolderDeleteAsync(a.FolderId);                 // 整单元（A + B + L1 + L2）
        return (a.FolderId, b.FolderId, l0.LinkId, l1.LinkId, l2.LinkId);
    }

    [Fact]
    public async Task 树_含链接叶子与虚根_计数与排序与浏览页同口径()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var (unitA, unitB, l0, l1, l2) = await SeedAsync(client);
            var vm = NewVm(client);
            await vm.LoadAsync();

            // 虚根「回收站」：不是实体、恒展开；计数 = 根级单独删除的链接数
            var root = Assert.Single(vm.Tree);
            Assert.True(root.IsRoot);
            Assert.True(root.IsExpanded);
            Assert.Equal(1, root.LinkCount);

            // 根的直接子：单元在前、链接在后（各自名称升序）
            Assert.Collection(root.Children,
                n => { Assert.Equal(unitA, n.Id); Assert.False(n.IsLink); },
                n => { Assert.Equal(l0, n.Id); Assert.True(n.IsLink); });

            // 单元 A：子单元 B 在前、直挂链接 L1 在后（这就是"回收站左栏也能看到链接"）
            var nodeA = root.Children.Single(n => n.Id == unitA);
            Assert.Equal(2, nodeA.LinkCount);   // 子树计数（L1 + L2）
            Assert.Collection(nodeA.Children,
                n => Assert.Equal(unitB, n.Id),
                n => Assert.Equal(l1, n.Id));

            var nodeB = nodeA.Children.Single(n => n.Id == unitB);
            Assert.Single(nodeB.Children, n => n.Id == l2);
            Assert.Equal(1, nodeB.LinkCount);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 主栏_默认删除时间倒序_进单元只看该层()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var (unitA, unitB, l0, l1, l2) = await SeedAsync(client);
            var vm = NewVm(client);
            await vm.LoadAsync();

            // 根层 = 单元 A + 单独删除的 L0；默认排序 = 删除时间倒序（不假设两次删除的时间戳必然不同）
            Assert.Equal(2, vm.Rows.Count);
            Assert.Contains(vm.Rows, r => r.Id == unitA && r.IsFolder);
            Assert.Contains(vm.Rows, r => r.Id == l0 && !r.IsFolder);
            Assert.Equal("deleted_at", vm.SortField);
            Assert.False(vm.SortAscending);
            var times = vm.Rows.Select(r => r.DeletedAt).ToList();
            Assert.True(times.SequenceEqual(times.OrderByDescending(t => t)), "默认排序必须是删除时间倒序");

            // 进单元 A：只显示该层直接内容（子单元 B + 直挂链接 L1），深层内容随子单元再打开
            await vm.NavigateAsync(unitA);
            Assert.Equal(2, vm.Rows.Count);
            Assert.Contains(vm.Rows, r => r.Id == unitB && r.IsFolder);
            Assert.Contains(vm.Rows, r => r.Id == l1 && !r.IsFolder);
            Assert.DoesNotContain(vm.Rows, r => r.Id == l2);

            // 面包屑：回收站 / A
            Assert.Equal(new[] { "回收站", "A" }, vm.Breadcrumbs.Select(c => c.Name).ToArray());
            Assert.True(vm.Breadcrumbs[^1].IsLast);

            // 返回上级 → 根
            await vm.NavigateAsync(vm.GetParentId(vm.CurrentUnitId));
            Assert.True(vm.IsAtRoot);

            // 点名称列排序：名称升序（平铺混排，不再按组）
            vm.ApplySort("name", ascending: true);
            var names = vm.Rows.Select(r => r.Name).ToList();
            Assert.Equal(names.OrderBy(n => n, System.StringComparer.CurrentCulture).ToList(), names);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 地址栏_按名解析_候选补全_根段名可省略()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var (unitA, unitB, _, _, _) = await SeedAsync(client);
            var vm = NewVm(client);
            await vm.LoadAsync();

            vm.EnterPathEditCommand.Execute(null);
            Assert.True(vm.IsPathEditing);
            Assert.Equal("回收站", vm.PathEditText);

            // 逐级解析：回收站 / A / B（根段名可省略）
            vm.PathEditText = "A/B";
            vm.ConfirmPathCommand.Execute(null);
            Assert.False(vm.IsPathEditing);
            Assert.Equal(unitB, vm.CurrentUnitId);

            // 候选：输入 "A" 时给出以 A 开头的单元名（Tab 补全走同一条）
            await vm.NavigateAsync(null);
            vm.EnterPathEditCommand.Execute(null);
            vm.PathEditText = "A";
            Assert.Contains("A", vm.PathCandidates);

            // 路径不存在：标红 + 明确报错，不导航
            vm.PathEditText = "不存在的单元";
            vm.ConfirmPathCommand.Execute(null);
            Assert.True(vm.IsPathInvalid);
            Assert.Equal(unitA, vm.GetParentId(unitB));   // A 仍在快照里（数据未动）
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 选中_唯一集合与投影_多选可批量()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var (unitA, _, l0, _, _) = await SeedAsync(client);
            var vm = NewVm(client);
            await vm.LoadAsync();

            var rowA = vm.Rows.Single(r => r.Id == unitA);
            var row0 = vm.Rows.Single(r => r.Id == l0);

            vm.SelectRowWithModifiers(rowA, ModifierKeys.None);
            Assert.True(vm.HasSelection && vm.SelectionCount == 1);
            Assert.True(rowA.IsSelected);              // 行 = 集合投影
            var root = Assert.Single(vm.Tree);
            Assert.True(root.Children.Single(n => n.Id == unitA).IsSelected);   // 树 = 同一个集合的投影

            vm.SelectRowWithModifiers(row0, ModifierKeys.Control);
            Assert.Equal(2, vm.SelectionCount);        // Ctrl 加选

            vm.ClearSelectionCommand.Execute(null);    // Esc / 点空白 = 清空
            Assert.False(vm.HasSelection);

            vm.SelectAllCommand.Execute(null);
            Assert.Equal(vm.Rows.Count, vm.SelectionCount);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 详情栏_选中投影_单选多选与清空同步()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var (unitA, _, l0, _, _) = await SeedAsync(client);
            var vm = NewVm(client);
            await vm.LoadAsync();

            // 空选中 = 空占位（右栏 = 选中集合的投影，视图不另设刷新入口）
            Assert.True(vm.Details.IsPlaceholder);

            // 单选单元：只读 + 文件夹态 + 类型 / 原位置 / 删除时间 / ID
            var rowA = vm.Rows.Single(r => r.Id == unitA);
            vm.SelectRowWithModifiers(rowA, ModifierKeys.None);
            Assert.True(vm.Details.HasSelection && vm.Details.IsSingle && vm.Details.IsFolder);
            Assert.True(vm.Details.IsReadOnly);
            Assert.Equal(rowA.Name, vm.Details.DisplayName);
            Assert.Equal(unitA, vm.Details.IdText);
            Assert.Equal(new[] { "类型", "原位置", "删除时间", "ID" }, vm.Details.Rows.Select(r => r.Label));

            // 单选书签：链接态 + 网址行 + 网址卡复制命令已接（原先是死按钮）
            var row0 = vm.Rows.Single(r => r.Id == l0);
            vm.SelectRowWithModifiers(row0, ModifierKeys.None);
            Assert.True(vm.Details.IsLink && !vm.Details.IsFolder);
            Assert.Equal(row0.Url, vm.Details.UrlText);
            Assert.Contains(vm.Details.Rows, r => r.Label == "网址");
            Assert.NotNull(vm.Details.CopyUrlCommand);

            // 多选：只报项数与类型分布（不再恒为 0）
            vm.SetSelection(new[] { unitA, l0 });
            Assert.True(vm.Details.IsMulti);
            Assert.Equal(2, vm.Details.SelectedTotal);
            Assert.Equal(1, vm.Details.SelectedFolders);
            Assert.Equal(1, vm.Details.SelectedLinks);
            Assert.Empty(vm.Details.Rows);

            // 清空 = 回空占位
            vm.ClearSelectionCommand.Execute(null);
            Assert.True(vm.Details.IsPlaceholder);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 永久删除_批量_带确认_清空选中()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var dialogs = new RecordingDialogs();
            var (unitA, _, l0, _, _) = await SeedAsync(client);
            var vm = NewVm(client, dialogs);
            await vm.LoadAsync();

            vm.SetSelection(new[] { unitA, l0 });
            vm.PurgeSelectionCommand.Execute(null);

            // 命令是"fire-and-forget"（与页面同构）→ 轮询等它落库（本地 SQLite，毫秒级）
            TrashOverviewDto overview = new();
            for (var i = 0; i < 100; i++)
            {
                overview = await client.TrashOverviewAsync();
                if (overview.Folders.Count == 0 && overview.Links.Count == 0) break;
                await Task.Delay(20);
            }

            Assert.Contains("不可恢复", dialogs.Confirms.Single().Message);
            Assert.Empty(overview.Folders);
            Assert.Empty(overview.Links);
            Assert.False(vm.HasSelection);                  // 条目已消失：绝不残留选中
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 还原_混合选择_回原位置_选中清空_状态栏计数()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var (unitA, _, l0, _, _) = await SeedAsync(client);
            var vm = NewVm(client);
            await vm.LoadAsync();

            Assert.False(vm.RestoreSelectionCommand.CanExecute(null));   // D-a：空选中 → 禁用
            vm.SetSelection(new[] { unitA, l0 });            // 混合：单元 + 单独删除的链接
            vm.RestoreSelectionCommand.Execute(null);

            TrashOverviewDto overview = new();
            for (var i = 0; i < 100; i++)                    // fire-and-forget → 轮询落库
            {
                overview = await client.TrashOverviewAsync();
                if (overview.Folders.Count == 0 && overview.Links.Count == 0) break;
                await Task.Delay(20);
            }

            Assert.Empty(overview.Folders);
            Assert.Empty(overview.Links);
            Assert.False(vm.HasSelection);                    // 条目已离开回收站：选中清空
            Assert.False(vm.RestoreSelectionCommand.CanExecute(null));   // 清空后再次禁用
            Assert.Contains("已还原 2 项到原位置", vm.StatusText);

            // 缺省 = 原位置（D3）：单元 A（含子夹 B 与两条链接）与 L0 都回主表（二者原位均为根）
            Assert.Contains(await client.FolderTreeAsync(), f => f.FolderId == unitA && f.ParentId == null);
            Assert.Contains((await client.LinkListAsync(perPage: 0)).Links, l => l.LinkId == l0 && l.ListId == null);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 还原到根目录_显式落根_不回原位置()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            // 链接原位在「原位夹」（夹仍在主表）：缺省会回夹内——显式"到根目录"必须落根
            var folder = (await client.FolderCreateAsync("原位夹")).Data!;
            var link = (await client.LinkCreateAsync("https://restore-root.example", title: "R",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;
            await client.LinkTrashAsync(link.LinkId);

            var vm = NewVm(client);
            await vm.LoadAsync();                             // 单独删除的链接在回收站根，平铺可见
            vm.SetSelection(new[] { link.LinkId });
            vm.RestoreSelectionToRootCommand.Execute(null);

            LinkDto? got = null;
            for (var i = 0; i < 100; i++)
            {
                try { got = await client.LinkGetAsync(link.LinkId); break; }
                catch { await Task.Delay(20); }
            }
            Assert.NotNull(got);
            Assert.Null(got!.ListId);                         // 显式到根目录（而非回「原位夹」）
            Assert.Contains("到根目录", vm.StatusText);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 侧栏_描述链路与定制动作面_复用共享框架与既有命令()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://desc.example", title: "带描述",
                description: "这是一段描述", autoFetchMetadata: false)).Data!;
            var link2 = (await client.LinkCreateAsync("https://desc2.example", title: "第二条",
                autoFetchMetadata: false)).Data!;
            await client.LinkTrashAsync(link.LinkId);
            await client.LinkTrashAsync(link2.LinkId);

            var vm = NewVm(client);
            await vm.LoadAsync();
            vm.SetSelection(new[] { link.LinkId });

            // 描述链路（架构缺口回归：DTO → 行 → 共享详情栏）
            Assert.True(vm.Details.HasDescription);
            Assert.Equal("这是一段描述", vm.Details.DescriptionText);

            // 动作面 = 共享框架内定制：详情 + 打开（打开网站）+ 还原/还原到根目录/永久删除；无 编辑/重命名
            Assert.True(vm.Details.IsReadOnly);
            Assert.True(vm.Details.ShowOpenAction);
            Assert.True(vm.Details.ShowOpenWebsiteButton);       // 链接行：两枚药丸（详情 / 打开）
            Assert.True(vm.Details.ShowDeleteAction);
            Assert.True(vm.Details.ShowRestoreAction);
            Assert.True(vm.Details.ShowRestoreToRootAction);
            Assert.True(vm.Details.StackedActions);              // 286 宽右栏：药丸一行 + 图标钮一行
            Assert.False(vm.Details.ShowEditAction);
            Assert.Equal("永久删除", vm.Details.DeleteActionLabel);
            Assert.True(vm.Details.HasActions);

            // 命令 = 复用本页既有能力（不是第二套业务逻辑）
            Assert.Same(vm.OpenSelectionCommand, vm.Details.OpenCommand);
            Assert.Same(vm.PurgeSelectionCommand, vm.Details.DeleteCommand);
            Assert.Same(vm.RestoreSelectionCommand, vm.Details.RestoreCommand);
            Assert.Same(vm.RestoreSelectionToRootCommand, vm.Details.RestoreToRootCommand);
            Assert.True(vm.Details.OpenCommand!.CanExecute(null));
            Assert.True(vm.Details.DeleteCommand!.CanExecute(null));
            Assert.True(vm.Details.RestoreCommand!.CanExecute(null));
            Assert.True(vm.Details.RestoreToRootCommand!.CanExecute(null));

            // 多选：只留永久删除（文案按页定制；两枚还原钮与药丸一并收起）
            vm.SelectAllCommand.Execute(null);
            Assert.True(vm.Details.IsMulti);
            Assert.False(vm.Details.ShowOpenAction);
            Assert.True(vm.Details.ShowDeleteAction);
            Assert.False(vm.Details.ShowRestoreAction);
            Assert.False(vm.Details.ShowRestoreToRootAction);
            Assert.Equal("永久删除所选", vm.Details.DeleteSelectionLabel);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 回收站_详情覆盖层打开时_处置动作让位()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://gate-trash.example", title: "覆盖层门",
                autoFetchMetadata: false)).Data!;
            await client.LinkTrashAsync(link.LinkId);

            var vm = NewVm(client);
            await vm.LoadAsync();
            vm.SetSelection(new[] { link.LinkId });
            Assert.True(vm.PurgeSelectionCommand.CanExecute(null));
            Assert.True(vm.RestoreSelectionCommand.CanExecute(null));

            vm.IsDetailOverlayOpen = true;                       // 视图打开只读覆盖层 → 危险键让位
            Assert.False(vm.PurgeSelectionCommand.CanExecute(null));
            Assert.False(vm.RestoreSelectionCommand.CanExecute(null));
            Assert.False(vm.RestoreSelectionToRootCommand.CanExecute(null));

            vm.IsDetailOverlayOpen = false;                      // 关闭覆盖层 → 复位
            Assert.True(vm.PurgeSelectionCommand.CanExecute(null));
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 详情覆盖层_共享详情页_数据与动作面_与浏览页同源()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://overlay.example", title: "覆盖层项",
                description: "覆盖层描述", autoFetchMetadata: false)).Data!;
            await client.LinkTrashAsync(link.LinkId);

            var vm = NewVm(client);
            await vm.LoadAsync();
            vm.OpenLinkDetail(vm.Rows.Single(r => r.Id == link.LinkId));

            Assert.True(vm.IsDetailOverlayOpen);
            Assert.Equal("覆盖层项", vm.DetailPane.Title);
            Assert.Equal("https://overlay.example", vm.DetailPane.Url);
            Assert.True(vm.DetailPane.HasDescription);
            Assert.Equal("覆盖层描述", vm.DetailPane.Description);
            Assert.Equal(3, vm.DetailPane.Rows.Count);                   // 原位置 / 删除时间 / ID
            // 动作面（共享面内定制）= 三枚等大药丸：打开网站 / 还原 / 还原到根目录 + 永久删除；无编辑
            Assert.Equal("打开", vm.DetailPane.OpenLabel);
            Assert.Equal("open-in-new", vm.DetailPane.OpenIconKind);
            Assert.Equal(PillTone.Tonal, vm.DetailPane.OpenTone);
            Assert.True(vm.DetailPane.ShowOpenAction);
            Assert.True(vm.DetailPane.ShowRestoreAction);
            Assert.True(vm.DetailPane.ShowRestoreToRootAction);
            Assert.False(vm.DetailPane.ShowEditAction);
            Assert.True(vm.DetailPane.ShowDeleteAction);
            Assert.Equal("永久删除", vm.DetailPane.DeleteActionLabel);
            Assert.Equal(PillTone.Primary, vm.DetailPane.RestoreTone);
            Assert.Equal(PillTone.Tonal, vm.DetailPane.RestoreToRootTone);
            Assert.NotNull(vm.DetailPane.BackCommand);
            // 覆盖层打开期间，**作用于选中**的处置键让位；关闭后复位
            Assert.False(vm.RestoreSelectionCommand.CanExecute(null));
            vm.CloseDetailOverlayCommand.Execute(null);
            Assert.False(vm.IsDetailOverlayOpen);
            Assert.True(vm.RestoreSelectionCommand.CanExecute(null));
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 详情覆盖层_条目离开回收站后自动关闭()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://overlay2.example", title: "覆盖层项2",
                autoFetchMetadata: false)).Data!;
            await client.LinkTrashAsync(link.LinkId);

            var vm = NewVm(client);
            await vm.LoadAsync();
            vm.OpenLinkDetail(vm.Rows.Single(r => r.Id == link.LinkId));
            Assert.True(vm.IsDetailOverlayOpen);

            // 别处把该条目还原（引擎侧）→ VM 刷新收尾"条目消失即关"
            await client.TrashRestoreBatchAsync(new[] { link.LinkId }, Array.Empty<string>());
            await vm.LoadAsync();
            Assert.False(vm.IsDetailOverlayOpen);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 详情覆盖层_动作按展示项生效_不依赖选中且不受覆盖层门禁()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://detail-act.example", title: "详情动作",
                autoFetchMetadata: false)).Data!;
            await client.LinkTrashAsync(link.LinkId);

            var vm = NewVm(client);
            await vm.LoadAsync();
            vm.OpenLinkDetail(vm.Rows.Single(r => r.Id == link.LinkId));

            // 用户报障根因：详情页动作的作用对象 = 展示项 → 覆盖层打开时依然可用
            Assert.Same(vm.OpenDetailWebsiteCommand, vm.DetailPane.OpenCommand);
            Assert.Same(vm.RestoreDetailCommand, vm.DetailPane.RestoreCommand);
            Assert.Same(vm.RestoreDetailToRootCommand, vm.DetailPane.RestoreToRootCommand);
            Assert.Same(vm.PurgeDetailCommand, vm.DetailPane.DeleteCommand);
            Assert.True(vm.DetailPane.OpenCommand!.CanExecute(null));
            Assert.True(vm.DetailPane.RestoreCommand!.CanExecute(null));
            Assert.True(vm.DetailPane.RestoreToRootCommand!.CanExecute(null));
            Assert.True(vm.DetailPane.DeleteCommand!.CanExecute(null));
            // 而作用于"选中集合"的处置命令照旧让位（两层语义不再混为一谈）
            Assert.False(vm.RestoreSelectionCommand.CanExecute(null));
            Assert.False(vm.RestoreSelectionToRootCommand.CanExecute(null));
            Assert.False(vm.PurgeSelectionCommand.CanExecute(null));

            // 清空选中也不影响详情页动作（不看 HasSelection）
            var detailId = link.LinkId;
            vm.ClearSelectionCommand.Execute(null);
            Assert.False(vm.HasSelection);
            Assert.True(vm.RestoreDetailCommand.CanExecute(null));
            Assert.Contains(detailId, vm.DetailPane.Rows.Single(r => r.Label == "ID").Value);

            // 按展示项执行「还原到根目录」→ 条目离开回收站、覆盖层自动关闭
            vm.RestoreDetailToRootCommand.Execute(null);
            LinkDto? got = null;
            for (var i = 0; i < 100; i++)
            {
                try { got = await client.LinkGetAsync(link.LinkId); break; }
                catch { await Task.Delay(20); }
            }
            Assert.NotNull(got);
            Assert.Null(got!.ListId);                                  // 显式到根目录
            Assert.Contains("到根目录", vm.StatusText);
            await vm.LoadAsync();
            Assert.False(vm.IsDetailOverlayOpen);                       // 条目消失即关
            Assert.False(vm.RestoreDetailCommand.CanExecute(null));     // 没有展示项 → 一律禁用
            Assert.False(vm.PurgeDetailCommand.CanExecute(null));
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 点空白清选中_两档栏归属语义_主栏卡与页面空白()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://blank-click.example", title: "空白点",
                autoFetchMetadata: false)).Data!;
            await client.LinkTrashAsync(link.LinkId);

            var vm = NewVm(client);
            await vm.LoadAsync();
            vm.ActivatePane(TrashPane.Tree);
            vm.SetSelection(new[] { link.LinkId });
            Assert.True(vm.HasSelection);

            // 列表卡空白：主栏获得键盘语义归属 + 清选中
            vm.ClearMainPaneSelectionCommand.Execute(null);
            Assert.False(vm.HasSelection);
            Assert.Equal(TrashPane.Main, vm.ActivePane);

            // 页面其它空白：保持当前栏归属，只清选中
            vm.ActivatePane(TrashPane.Tree);
            vm.SetSelection(new[] { link.LinkId });
            vm.ClearPageSelectionCommand.Execute(null);
            Assert.False(vm.HasSelection);
            Assert.Equal(TrashPane.Tree, vm.ActivePane);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public void 键位表_无撤销无剪贴板无全局键()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var vm = NewVm(client);
            var registry = TrashShortcuts.CreateRegistry(vm);

            // 用户定稿：阉割 Ctrl+Z/Y（撤销/重做）与 Ctrl+X/C/V（剪贴板）、F2（重命名）
            Assert.Null(registry.Resolve(ShortcutScope.Trash, Key.Z, ModifierKeys.Control));
            Assert.Null(registry.Resolve(ShortcutScope.Trash, Key.Y, ModifierKeys.Control));
            Assert.Null(registry.Resolve(ShortcutScope.Trash, Key.X, ModifierKeys.Control));
            Assert.Null(registry.Resolve(ShortcutScope.Trash, Key.C, ModifierKeys.Control));
            Assert.Null(registry.Resolve(ShortcutScope.Trash, Key.V, ModifierKeys.Control));
            Assert.Null(registry.Resolve(ShortcutScope.Trash, Key.F2, ModifierKeys.None));

            // 不注册任何全局作用域键（Ctrl+E/F 只挂浏览页）——回收站内不应命中
            Assert.DoesNotContain(registry.Bindings, b => b.Scope == ShortcutScope.Global);

            // 保留的关键键位：F5 / Backspace / Alt+↑ / Delete / Ctrl+A / Esc
            Assert.NotNull(registry.Resolve(ShortcutScope.Trash, Key.F5, ModifierKeys.None));
            Assert.NotNull(registry.Resolve(ShortcutScope.Trash, Key.Back, ModifierKeys.None));
            Assert.NotNull(registry.Resolve(ShortcutScope.Trash, Key.Up, ModifierKeys.Alt));
            Assert.NotNull(registry.Resolve(ShortcutScope.Trash, Key.Delete, ModifierKeys.None));
            Assert.NotNull(registry.Resolve(ShortcutScope.TrashMain, Key.A, ModifierKeys.Control));
            Assert.NotNull(registry.Resolve(ShortcutScope.Trash, Key.Escape, ModifierKeys.None));

            // 还原键位（D1 拍板）：Ctrl+R 到原位置 / Ctrl+Shift+R 到根目录
            Assert.NotNull(registry.Resolve(ShortcutScope.Trash, Key.R, ModifierKeys.Control));
            Assert.NotNull(registry.Resolve(ShortcutScope.Trash, Key.R, ModifierKeys.Control | ModifierKeys.Shift));
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}
