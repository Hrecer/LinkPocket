using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 浏览页 VM 金标准（P4）：可观测行为断言——行渲染形态（文件夹在前/名称升序）、
/// 导航与面包屑父链、跳转并选中。引擎用共享 Composition 全量装配（App 同口径），
/// 数据全部经引擎命令读写（黑盒），不开 internal 后门；渲染回归仍由 渲染检查 承担。
/// </summary>
public class BrowserViewModelTests
{
    [Fact]
    public async Task 根目录加载_文件夹在前链接在后_各自按名称升序()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.FolderCreateAsync("B 文件夹");
            await client.FolderCreateAsync("A 文件夹");
            await client.LinkCreateAsync("https://z.example", title: "Z 链接", autoFetchMetadata: false);
            await client.LinkCreateAsync("https://a.example", title: "A 链接", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            // 升序口径（Windows）：文件夹在前（按名称）、链接在后（按名称）
            Assert.Equal(4, vm.Rows.Count);
            Assert.Equal(new[] { true, true, false, false }, vm.Rows.Select(r => r.IsFolder).ToArray());
            Assert.Equal(new[] { "A 文件夹", "B 文件夹" }, vm.Rows.Take(2).Select(r => r.Name).ToArray());
            Assert.Equal(new[] { "A 链接", "Z 链接" }, vm.Rows.Skip(2).Select(r => r.Name).ToArray());

            // 根目录面包屑 = 仅「全部书签」且为最后一级（可点击跳转）
            Assert.Single(vm.Breadcrumbs);
            Assert.Equal("@root", vm.Breadcrumbs[0].Name);   // 模型存身份，显示名由模板投影
            Assert.True(vm.Breadcrumbs[0].IsLast);

            Assert.Equal("共 4 项（2 个文件夹 / 2 个链接）", vm.StatusText.Resolve());
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 进入子目录_面包屑与父链_返回根恢复()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var a1 = (await client.FolderCreateAsync("A1", parentId: a.FolderId)).Data!;
            await client.LinkCreateAsync("https://c.example", title: "C", listId: a.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            Assert.Null(vm.CurrentFolderId);
            Assert.Single(vm.Rows);                       // 根 = 只有 A

            await vm.LoadAsync(a.FolderId);
            Assert.Equal(a.FolderId, vm.CurrentFolderId);
            Assert.Equal(new[] { "@root", "A" }, vm.Breadcrumbs.Select(b => b.Name).ToArray());   // 根段 = 身份
            Assert.True(vm.Breadcrumbs[1].IsLast);        // 当前目录高亮（最后一级）
            Assert.Equal(2, vm.Rows.Count);               // A1（文件夹）+ C（链接）
            Assert.Equal("A1", vm.Rows[0].Name);
            Assert.True(vm.Rows[0].IsFolder);
            Assert.Equal("C", vm.Rows[1].Name);
            Assert.False(vm.Rows[1].IsFolder);

            // 完整路径展示（详情栏口径，含自身）："全部书签 / A / A1"
            Assert.Equal("全部书签 / A / A1", vm.GetFolderPathDisplay(a1.FolderId));

            await vm.LoadAsync(null);
            Assert.Null(vm.CurrentFolderId);
            Assert.Single(vm.Rows);                       // 回到根：又只剩 A
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 退出子目录_清过选中或选中过链接_返回上级仍选中刚退出的文件夹()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            await client.LinkCreateAsync("https://a.example/1", title: "One", listId: a.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            await vm.LoadAsync(a.FolderId);

            // ① 在子目录里点空白清选中（与 Esc 同一语义）→ 点面包屑「全部书签」退出
            vm.ClearSelection();
            Assert.False(vm.HasSelection);
            vm.CrumbClickCommand.Execute(vm.Breadcrumbs[0]);
            var exited = await WaitUntilAsync(() => vm.HasSelection, TimeSpan.FromSeconds(5));
            Assert.True(exited, "返回上级后应选中刚退出的文件夹");
            Assert.Equal(a.FolderId, Assert.Single(vm.SelectedRows).Id);
            Assert.True(vm.IsSelectedId(a.FolderId));

            // 侧栏高亮 = 当前位置（根）；主栏选中的来源文件夹不改变树高亮（两者语义不同）
            var root = Assert.Single(vm.FolderTree);
            Assert.True(root.IsSelected);
            Assert.False(root.Children.Single().IsSelected);

            // ② 在子目录里选中过链接（选中集合被链接占住），退出后来源文件夹仍被选中
            await vm.LoadAsync(a.FolderId);
            var linkRow = Assert.Single(vm.Rows, r => !r.IsFolder);
            vm.SelectRowWithModifiers(linkRow, ModifierKeys.None);
            Assert.Equal(linkRow.Id, Assert.Single(vm.SelectedRows).Id);

            await vm.LoadAsync(null, selectExitedChild: true);   // 等价于返回上级 / 后退 / 点面包屑祖先段
            Assert.Equal(a.FolderId, Assert.Single(vm.SelectedRows).Id);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 空态条件_仅当整个书签库为空_进入空子文件夹不显示()
    {
        // 用户实测回归：进入一个**空的子文件夹**曾显示"这里还空空如也"——库非空，只是这一格为空。
        // 空态占位的 XAML 判据 = IsAtRoot + Rows.Count == 0（整个库为空 ⇔ 根视图无行，任何条目都挂在根下），
        // 本用例把两个输入钉死（渲染可见性由探针 gesture 套件复核）。
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var vm = new BrowserViewModel(client);

            // ① 全新库（还没建过任何东西）：根视图 + 无行 = 空态成立（首次安装的引导态）
            await vm.LoadAsync(null);
            Assert.True(vm.IsAtRoot);
            Assert.Empty(vm.Rows);

            // ② 建一个空文件夹 → 根视图有行 ⇒ 空态不成立
            var empty = (await client.FolderCreateAsync("空夹")).Data!;
            await vm.LoadAsync(null);
            Assert.True(vm.IsAtRoot);
            Assert.Single(vm.Rows);

            // ③ 进入空文件夹：无行，但**不在根** ⇒ 空态不成立（这就是修掉的误报场景）
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            await vm.LoadAsync(empty.FolderId);
            Assert.False(vm.IsAtRoot);
            Assert.Empty(vm.Rows);
            Assert.Contains(nameof(BrowserViewModel.IsAtRoot), raised);   // XAML 触发器依赖的通知

            // ④ 回根：有行、在根 ⇒ 空态仍不成立
            await vm.LoadAsync(null);
            Assert.True(vm.IsAtRoot);
            Assert.Single(vm.Rows);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 移动链接后_立即真刷新_行随查询结果消失_遮罩与入场动画仍静默()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var link = (await client.LinkCreateAsync("https://move.example", title: "M",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(a.FolderId);
            Assert.Contains(vm.Rows, r => r.Id == link.LinkId);

            await vm.DropItemsAsync(new[] { new DragItem(link.LinkId, false, "M") }, b.FolderId, TransferMode.Move);

            // 口径（2026-10-15 起）：写操作完成后**立即真刷新**（不等 300ms 事件防抖）——
            // 行随查询结果消失，用户不再等"防抖 + 整链"那一拍。
            Assert.DoesNotContain(vm.Rows, r => r.Id == link.LinkId);  // 已移出目录 A 的行
            Assert.Empty(vm.Rows);                                     // A 目录此刻为空

            // 遮罩口径（区分"刷新次数"与"要不要给用户看"两件事）：
            // 写操作与随后的刷新都不亮遮罩；只有"导航加载"亮，且完成即收。
            Assert.False(vm.IsNavigating);                             // 移动（写操作）不亮遮罩
            await vm.RefreshPreservingSelectionAsync();
            Assert.False(vm.IsNavigating);                             // 后台口径的刷新也不亮遮罩

            // 导航加载（navigating: true）完成后遮罩必须收回——不留僵住的遮罩
            //（引擎在本环境可能同步完成整个加载，故只锁"终态必为收起"，不锁中途瞬时值）
            await vm.RefreshAsync(navigating: true);
            Assert.False(vm.IsNavigating);

            // 行入场动画的触发口径：只有导航加载的那次刷新链才允许播（写后刷新/后台刷新一律静默）——
            // 界面的行错峰入场动画订阅 RefreshCompleted，参数即"是不是导航加载"。
            var refreshEvents = new List<bool>();
            vm.RefreshCompleted += (_, wasNavigation) => refreshEvents.Add(wasNavigation);
            await vm.LoadAsync(null);                        // 打开目录（导航）→ 播
            await vm.RefreshPreservingSelectionAsync();      // 后台口径的刷新 → 不播
            Assert.Equal(new[] { true, false }, refreshEvents);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 后台刷新_原地写回行时_派生展示列也必须发通知_否则那些列永远停在旧值()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://visit.example", title: "V",
                autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var row = Assert.Single(vm.Rows);

            Assert.Equal(0, row.ViewCount);
            Assert.Null(row.LastViewedAt);
            var viewsBefore = row.ViewCountText.Resolve();
            var lastViewedBefore = row.LastViewedAdaptive.Resolve();

            // 记下本行发出的属性通知名——绑定就是靠"名字对得上"驱动那一格重画的
            var raised = new List<string>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

            // 查看一次：引擎把 LastVisitedAt / VisitCount 落库并推 links.changed
            await client.LinkVisitRecordAsync(link.LinkId);
            // 事件链口径的后台刷新（界面侧由 MainViewModel 300ms 防抖触发同一个入口）
            await vm.RefreshPreservingSelectionAsync();

            // 行序列（Id + 顺序）没变 ⇒ 差分刷新走 ApplyLink **原地写回**，行对象不换、一次 Reset 都不发
            Assert.Same(row, vm.Rows[0]);
            // 值确实变了（否则下面的通知断言就是空转）
            Assert.Equal(1, row.ViewCount);
            Assert.NotNull(row.LastViewedAt);
            Assert.NotEqual(viewsBefore, row.ViewCountText.Resolve());
            Assert.NotEqual(lastViewedBefore, row.LastViewedAdaptive.Resolve());

            // ⚠️ 回归闸：列表四列绑的是派生投影（{loc:FitValue ModifiedText / LastViewedAdaptive /
            //    ViewCountText / CreatedText}），不是原始字段。原地写回不换对象、不发 Reset，
            //    只发原始字段名（ViewCount / LastViewedAt）时绑定收不到通知 ⇒ 列表全部停在旧值，
            //    而右栏走 LinkGetAsync 重新取数看着是新的——症状正是"只有列表不更新"。
            Assert.Contains("LastViewedAt", raised);
            Assert.Contains("LastViewedAdaptive", raised);
            Assert.Contains("ViewCount", raised);
            Assert.Contains("ViewCountText", raised);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 跳转导航_进入目标目录并选中目标行_已在该目录不重载()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("F")).Data!;
            var link = (await client.LinkCreateAsync("https://one.example", title: "One",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            var navigated = await vm.NavigateAndSelectAsync(folder.FolderId, link.LinkId);

            Assert.True(navigated);
            Assert.Equal(folder.FolderId, vm.CurrentFolderId);
            var selected = Assert.Single(vm.SelectedRows);
            Assert.Equal(link.LinkId, selected.Id);
            Assert.False(selected.IsFolder);

            // 已在目标目录：再跳一次不会整目录重载（行对象保持不变 → 选中仍唯一）
            var again = await vm.NavigateAndSelectAsync(folder.FolderId, link.LinkId);
            Assert.True(again);
            Assert.Equal(link.LinkId, Assert.Single(vm.SelectedRows).Id);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>
    /// 文件夹跳转（ID 跳转需要）：文件夹的容器 = 它的**父目录** → 进入父目录 + 选中该文件夹行。
    /// 与链接跳转同一条原语（<see cref="BrowserViewModel.NavigateAndSelectAsync"/>），
    /// 但落点是**文件夹行**（主栏行与树高亮都要落到它）——此前只有链接跳转有覆盖。
    /// </summary>
    [Fact]
    public async Task 跳转导航_文件夹目标_进入父目录并选中该文件夹行()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var parent = (await client.FolderCreateAsync("父层")).Data!;
            var child = (await client.FolderCreateAsync("子层", parentId: parent.FolderId)).Data!;

            var vm = new BrowserViewModel(client);
            var navigated = await vm.NavigateAndSelectAsync(parent.FolderId, child.FolderId);

            Assert.True(navigated);
            Assert.Equal(parent.FolderId, vm.CurrentFolderId);   // 文件夹的容器 = 它的父目录
            var selected = Assert.Single(vm.SelectedRows);
            Assert.Equal(child.FolderId, selected.Id);
            Assert.True(selected.IsFolder);                      // 选中的是文件夹行本身（不是链接行）
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 编辑模式打开_预填字段携带链接数据()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://edit.example/zh-cn/3",
                title: "编辑目标", description: "描述文本", autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            vm.OpenEditorForEdit(link.LinkId);

            // fire-and-forget 预填：轮询直到字段就位（带超时，等的是引擎异步取数 + 续体）
            var urlReady = await WaitUntilAsync(
                () => vm.EditorPage is { Url: "https://edit.example/zh-cn/3" }, TimeSpan.FromSeconds(3));
            Assert.True(urlReady, "预填未在超时内完成（编辑页字段空白根因复现点）");
            Assert.True(vm.IsEditorPageOpen);
            Assert.Equal("编辑链接", vm.EditorPage!.TitleText.Resolve());
            Assert.Equal("编辑目标", vm.EditorPage.LinkTitle);
            Assert.Equal("描述文本", vm.EditorPage.Description);
            Assert.True(vm.EditorPage.IsEditMode);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 编辑模式打开_带图标的链接_加载完成后可清除图标()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            // 内嵌 data: 图标：解码全在本地（不触网），LoadFromCache 必然命中
            const string inlineIcon = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
            var link = (await client.LinkCreateAsync("https://icon.example/", title: "带图标", autoFetchMetadata: false,
                faviconUrl: inlineIcon)).Data!;

            var vm = new BrowserViewModel(client);
            vm.OpenEditorForEdit(link.LinkId);

            // 回归：预填完成（IsLoading 收尾）后清除按钮必须重新评估为可用——
            // 曾因 IsLoading 不重发 CanClearFavicon 通知，按钮可见性停在加载期的 false（取消再进编辑后按钮消失）。
            var canClear = await WaitUntilAsync(() => vm.EditorPage is { CanClearFavicon: true }, TimeSpan.FromSeconds(3));
            Assert.True(canClear, "带图标的链接进入编辑后，清除图标按钮应可用");
            Assert.True(vm.EditorPage!.HasFavicon);
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
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task 目录树_只显示文件夹_展开也不注入链接叶子()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://a-root.example", title: "Alpha 根级", autoFetchMetadata: false);
            await client.LinkCreateAsync("https://z-root.example", title: "Zulu 根级", autoFetchMetadata: false);
            await client.FolderCreateAsync("Charlie 文件夹");
            var delta = (await client.FolderCreateAsync("Delta 文件夹")).Data!;
            await client.LinkCreateAsync("https://sub.example", title: "Bravo 子级",
                listId: delta.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var root = Assert.Single(vm.FolderTree);
            Assert.True(root.IsRoot);
            // 左栏 = Windows 导航窗格口径：只列文件夹，根级直挂链接不入树
            Assert.Equal(new[] { "Charlie 文件夹", "Delta 文件夹" },
                root.Children.Select(c => c.Name).ToArray());
            Assert.All(root.Children, c => Assert.False(c.IsLink));

            // 展开文件夹也不注入链接（没有懒加载叶子这回事了）；Delta 没有子文件夹 → 无展开箭头
            var deltaNode = root.Children.Single(c => c.FolderId == delta.FolderId);
            Assert.False(deltaNode.ShowExpander);
            deltaNode.IsExpanded = true;
            Assert.Empty(deltaNode.Children);

            // 刷新后依旧只有文件夹（展开态照旧保留）
            await vm.LoadAsync(null);
            var rootNow = Assert.Single(vm.FolderTree);
            Assert.All(rootNow.Children, c => Assert.False(c.IsLink));
            Assert.Empty(rootNow.Children.Single(c => c.FolderId == delta.FolderId).Children);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 目录树_文件夹行单击_选中该文件夹并进入()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            await client.LinkCreateAsync("https://a.example/1", title: "One", listId: a.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var node = Assert.Single(vm.FolderTree[0].Children);
            Assert.Equal(a.FolderId, node.FolderId);

            await vm.SelectTreeNodeAsync(node);

            Assert.Equal(a.FolderId, vm.CurrentFolderId);                 // 已进入该目录
            Assert.True(vm.IsSelectedId(a.FolderId));                     // 该文件夹写入选中集合（沿用右键/拖拽语义）
            var nodeNow = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            Assert.True(nodeNow.IsSelected);                              // 树行高亮 = 当前位置（重建后仍成立）
            Assert.Equal("One", Assert.Single(vm.Rows).Name);             // 主栏显示该目录内容
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 目录树_根节点恒显当前位置_虚根永不进选中集合()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            await client.LinkCreateAsync("https://a.example/1", title: "One", listId: a.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            // 在根：根节点 = 当前所在目录 → 恒高亮；但虚根不是实体，永不写入选中集合
            var root = Assert.Single(vm.FolderTree);
            Assert.True(root.IsRoot);
            Assert.True(root.IsSelected);
            Assert.False(vm.IsSelectedId(root.Id));

            // 已在根：单击「全部书签」= 重载根目录（点"当前所在位置"同样刷新，Windows 口径）
            var rowBefore = vm.Rows.First();
            await vm.SelectTreeNodeAsync(root);
            Assert.Null(vm.CurrentFolderId);                    // 仍在根目录
            // "刷新发生过"的证据 = **树确实重建**（新节点实例）；**不能**再用"行实例不同"当代理——
            // 内容一致的刷新会**保留行集**（避免整表重建的等价跳过，见 BrowserViewModel.Refresh）。
            var rootAfterReload = Assert.Single(vm.FolderTree);
            Assert.NotSame(root, rootAfterReload);
            Assert.Equal(rowBefore.Id, vm.Rows.First().Id);
            Assert.True(rootAfterReload.IsSelected);             // 位置仍在根 → 根行仍高亮
            Assert.False(vm.IsSelectedId(rootAfterReload.Id));   // 虚根无身份可选中（Id 为空，永不入集合）

            // 进入子目录：高亮转移到当前目录（根行熄灭）
            var aNode = vm.FolderTree[0].Children.Single();
            await vm.SelectTreeNodeAsync(aNode);
            Assert.Equal(a.FolderId, vm.CurrentFolderId);
            var rootInA = Assert.Single(vm.FolderTree);
            Assert.False(rootInA.IsSelected);
            Assert.True(rootInA.Children.Single().IsSelected);

            // 再单击「全部书签」= 回根目录：高亮回到根行
            var rootNow = Assert.Single(vm.FolderTree);
            await vm.SelectTreeNodeAsync(rootNow);
            Assert.Null(vm.CurrentFolderId);
            Assert.True(Assert.Single(vm.FolderTree).IsSelected);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 多选_Ctrl翻转_Shift区间_CtrlA全选与清空()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://a.example/1", title: "A", autoFetchMetadata: false);
            await client.LinkCreateAsync("https://b.example/2", title: "B", autoFetchMetadata: false);
            await client.LinkCreateAsync("https://c.example/3", title: "C", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var a = vm.Rows[0];
            var b = vm.Rows[1];
            var c = vm.Rows[2];

            // 单选 → Ctrl 加选：两行同时留在集合（曾因 mutate 从空集合起步退化成单选 = 多选永远做不到）
            vm.SelectRowWithModifiers(a, ModifierKeys.None);
            vm.SelectRowWithModifiers(b, ModifierKeys.Control);
            Assert.True(vm.IsSelectedId(a.Id) && vm.IsSelectedId(b.Id));
            Assert.Equal(2, vm.SelectionCount);
            Assert.Equal(2, vm.Rows.Count(r => r.IsSelected));   // 行投影同步（主栏高亮同源）

            // Ctrl 再点已选中行 = 只翻掉它，其余保留
            vm.SelectRowWithModifiers(a, ModifierKeys.Control);
            Assert.False(vm.IsSelectedId(a.Id));
            Assert.True(vm.IsSelectedId(b.Id));
            Assert.Equal(1, vm.SelectionCount);

            // Shift 区间选：以当前锚点（b）扩到 c → {b, c}
            vm.SelectRowWithModifiers(b, ModifierKeys.None);
            vm.SelectRowWithModifiers(c, ModifierKeys.Shift);
            Assert.Equal(
                new[] { b.Id, c.Id }.OrderBy(x => x, StringComparer.Ordinal),
                vm.Rows.Where(r => r.IsSelected).Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal));

            // Ctrl+A 全选 → Esc/空白清空
            vm.SelectAllRows();
            Assert.Equal(3, vm.SelectionCount);
            vm.ClearSelection();
            Assert.Equal(0, vm.SelectionCount);
            Assert.False(vm.HasSelection);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 粘贴_复制语义_新项临时置尾并被选中_真刷新后才归位()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://a.example/1", title: "A", autoFetchMetadata: false);
            await client.LinkCreateAsync("https://b.example/2", title: "B", autoFetchMetadata: false);
            await client.LinkCreateAsync("https://c.example/3", title: "C", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            Assert.Equal(new[] { "A", "B", "C" }, vm.Rows.Select(r => r.Name).ToArray());

            // 复制 A → 粘贴（命令是 fire-and-forget：轮询状态栏确认完成）
            vm.SelectRowWithModifiers(vm.Rows[0], ModifierKeys.None);
            vm.CopyCommand.Execute(null);
            vm.PasteCommand.Execute(null);
            Assert.True(await WaitUntilAsync(() => vm.StatusText.Resolve().StartsWith("已粘贴"), TimeSpan.FromSeconds(5)),
                $"粘贴未在超时内完成（状态：{vm.StatusText.Resolve()}）");

            // 写操作已由内部立即刷新落地；这里再刷一次（后台事件链同一条链）确认置尾不受影响
            await vm.RefreshPreservingSelectionAsync();

            // 置尾（Windows）：新项临时排在末尾（不参与排序），不再紧跟原项。
            // 链接标题**不做唯一化**（链接身份 = URL，标题只是标签）→ 复制出来的仍是 "A"。
            Assert.Equal(new[] { "A", "B", "C", "A" }, vm.Rows.Select(r => r.Name).ToArray());
            // 新项被选中（粘贴后选中新内容）
            var newRow = vm.Rows[3];
            Assert.True(newRow.IsSelected);
            Assert.Equal(newRow.Id, Assert.Single(vm.SelectedRows).Id);

            // 后台事件刷新（写操作后的防抖口径）不归位：置尾保持不变（否则粘贴后那次刷新就抹掉置尾效果）
            await vm.RefreshPreservingSelectionAsync();
            Assert.Equal(new[] { "A", "B", "C", "A" }, vm.Rows.Select(r => r.Name).ToArray());

            // 点列头排序 = 真刷新 → 置尾归位（按名称升序）
            vm.ApplySort("title", true);
            Assert.True(await WaitUntilAsync(
                    () => vm.Rows.Count == 4 && vm.Rows[3].Name == "C", TimeSpan.FromSeconds(5)),
                $"点列头排序后置尾未归位：{string.Join(",", vm.Rows.Select(r => r.Name))}");
            Assert.Equal(new[] { "A", "A", "B", "C" }, vm.Rows.Select(r => r.Name).ToArray());

            // 重新进入目录（导航）= 真刷新：置尾同样归位
            vm.SelectRowWithModifiers(vm.Rows[0], ModifierKeys.None);
            vm.CopyCommand.Execute(null);
            vm.PasteCommand.Execute(null);
            Assert.True(await WaitUntilAsync(() => vm.StatusText.Resolve().StartsWith("已粘贴"), TimeSpan.FromSeconds(5)));
            await vm.RefreshPreservingSelectionAsync();
            // 置尾生效：新项（同标题，按 ID 分辨）在末尾
            var pasted = Assert.Single(vm.SelectedRows);
            Assert.Equal(pasted.Id, vm.Rows[^1].Id);

            await vm.LoadAsync(null);
            Assert.Equal(new[] { "A", "A", "A", "B", "C" }, vm.Rows.Select(r => r.Name).ToArray());
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 剪切_同目录粘贴无操作且明确提示_跨目录粘贴移动并置尾选中()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var link = (await client.LinkCreateAsync("https://x.example", title: "X",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(a.FolderId);
            Assert.Equal("X", Assert.Single(vm.Rows).Name);

            vm.SelectRowWithModifiers(vm.Rows[0], ModifierKeys.None);
            vm.CutCommand.Execute(null);
            Assert.True(vm.Clipboard.BrowserPayload is { IsCut: true });   // 剪切载荷就位
            Assert.True(vm.Rows.Single(r => r.Id == link.LinkId).IsCut);   // 半透明视觉就位

            // 同目录粘贴 = 无操作 + 明确提示（载荷保留）——
            // 静默早退曾让"剪切后粘贴没反应"看起来像数据不一致
            vm.PasteCommand.Execute(null);
            Assert.Equal("剪切的项目已在当前文件夹中（先进入目标文件夹再粘贴）", vm.StatusText.Resolve());
            Assert.True(vm.Clipboard.BrowserPayload is { IsCut: true });   // 剪切态未被消费

            // 跨目录粘贴：移动 + 置尾 + 选中 + 剪切态遗忘
            await vm.LoadAsync(b.FolderId);
            vm.PasteCommand.Execute(null);
            Assert.True(await WaitUntilAsync(() => vm.Clipboard.BrowserPayload == null, TimeSpan.FromSeconds(5)),
                $"粘贴未消费剪切载荷（状态：{vm.StatusText.Resolve()}）");
            Assert.Equal("已粘贴 1 项", vm.StatusText.Resolve());

            await vm.RefreshPreservingSelectionAsync();
            Assert.Equal("X", Assert.Single(vm.Rows).Name);                // 已移动到 B 并置尾于列表末尾（唯一项）
            Assert.True(vm.Rows[0].IsSelected);                            // 新落点被选中
            Assert.False(vm.Rows[0].IsCut);                                // 剪切视觉已复位

            // 源目录 A 里不再有 X（剪切 = 移动语义）
            await vm.LoadAsync(a.FolderId);
            Assert.Empty(vm.Rows);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Esc_有剪切态先取消剪切_无剪切态才清空选中()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://a.example/1", title: "A", autoFetchMetadata: false);
            await client.LinkCreateAsync("https://b.example/2", title: "B", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            vm.SelectRowWithModifiers(vm.Rows[0], ModifierKeys.None);
            vm.CutCommand.Execute(null);
            Assert.True(vm.Rows[0].IsCut);

            // 第一层：取消剪切（清载荷 + 复位半透明视觉 + 状态栏反馈），选中不动
            vm.EscapeCommand.Execute(null);
            Assert.Null(vm.Clipboard.BrowserPayload);
            Assert.False(vm.Rows[0].IsCut);
            Assert.True(vm.Rows[0].IsSelected);
            Assert.Equal("已取消剪切", vm.StatusText.Resolve());
            Assert.False(vm.PasteCommand.CanExecute(null));                // 粘贴随之禁用

            // 第二层：无剪切态 → 清空选中
            vm.EscapeCommand.Execute(null);
            Assert.False(vm.HasSelection);
            Assert.Equal(0, vm.SelectionCount);

            // 复制载荷不受 Esc 影响（Windows：Esc 只取消剪切态），此时 Esc 走"清空选中"分支
            vm.SelectRowWithModifiers(vm.Rows[1], ModifierKeys.None);
            vm.CopyCommand.Execute(null);
            vm.EscapeCommand.Execute(null);
            Assert.NotNull(vm.Clipboard.BrowserPayload);                   // 复制载荷保留
            Assert.False(vm.HasSelection);                                 // 选中被清（Esc 第二层）
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 剪贴板矩阵_新复制剪切覆盖旧载荷_源失效项粘贴时跳过()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var keep = (await client.LinkCreateAsync("https://keep.example", title: "保留",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;
            var gone = (await client.LinkCreateAsync("https://gone.example", title: "失效",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(a.FolderId);

            // 覆盖：剪切「保留」→ 再复制「失效」→ 载荷整体替换为复制（旧剪切半透明视觉同时复位）
            vm.SelectRowWithModifiers(vm.Rows.Single(r => r.Id == keep.LinkId), ModifierKeys.None);
            vm.CutCommand.Execute(null);
            Assert.True(vm.Rows.Single(r => r.Id == keep.LinkId).IsCut);
            vm.SelectRowWithModifiers(vm.Rows.Single(r => r.Id == gone.LinkId), ModifierKeys.None);
            vm.CopyCommand.Execute(null);
            Assert.True(vm.Clipboard.BrowserPayload is { IsCut: false });   // 载荷已整体替换
            Assert.False(vm.Rows.Single(r => r.Id == keep.LinkId).IsCut);   // 旧剪切视觉复位

            // 源失效：被复制的链接移入回收站 → 粘贴时单项失败（**如实报数**，不再含混成"没有可粘贴的项目"），
            // 目标目录零写入（不是数据不一致）；失败同时写日志（观测面铁律）
            await client.LinkTrashAsync(gone.LinkId);
            await vm.LoadAsync(b.FolderId);
            vm.PasteCommand.Execute(null);
            Assert.True(await WaitUntilAsync(() => vm.StatusText.Resolve().Contains("失败"), TimeSpan.FromSeconds(5)),
                $"期望如实报告失败项，实际：{vm.StatusText.Resolve()}");
            var inB = await client.LinkListAsync(listId: b.FolderId, perPage: 0);
            Assert.Empty(inB.Links);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 路径编辑态_新建链接与新建文件夹一并禁用()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            Assert.True(vm.NewLinkCommand.CanExecute(null));

            vm.EnterPathEditCommand.Execute(null);                 // 地址栏进入编辑态
            Assert.True(vm.IsPathEditing);
            Assert.False(vm.NewLinkCommand.CanExecute(null));      // 编辑地址时「新建链接」必须禁用
            Assert.False(vm.NewFolderCommand.CanExecute(null));

            vm.CancelPathEditCommand.Execute(null);
            Assert.True(vm.NewLinkCommand.CanExecute(null));
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>
    /// 侧栏动作面：链接 = 两行排布 + 独立「重命名」图标钮（就地改标题，
    /// 命令 = 本页就地改名命令**同一实例**）；文件夹不开该钮（铅笔即重命名，同一动作不摆两枚）。
    /// </summary>
    [Fact]
    public async Task 侧栏动作面_链接开重命名图标钮_文件夹不开_一律两行排布()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://a.example", title: "A 链接", autoFetchMetadata: false);
            await client.FolderCreateAsync("A 文件夹");

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);                        // 根级：链接 + 文件夹

            var linkRow = vm.Rows.First(r => !r.IsFolder);
            vm.SelectRowWithModifiers(linkRow, ModifierKeys.None);
            Assert.True(vm.Details.IsLink);
            Assert.True(vm.Details.StackedActions);          // 两行排布（药丸一行 / 图标钮一行靠右）
            Assert.True(vm.Details.ShowRenameAction);
            Assert.Equal("common.rename", vm.Details.RenameActionLabel.Key);
            Assert.Same(vm.RenameSelectionCommand, vm.Details.RenameActionCommand);   // 复用同一命令实例
            Assert.True(vm.Details.RenameActionCommand!.CanExecute(null));

            var folderRow = vm.Rows.First(r => r.IsFolder);
            vm.SelectRowWithModifiers(folderRow, ModifierKeys.None);
            Assert.True(vm.Details.IsFolder);
            Assert.False(vm.Details.ShowRenameAction);       // 文件夹由铅笔承担重命名
            Assert.Equal("common.rename", vm.Details.EditLabel.Key);

            vm.ClearSelection();
            Assert.False(vm.Details.ShowRenameAction);       // 清空选中：动作面不复用上一次的位
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}