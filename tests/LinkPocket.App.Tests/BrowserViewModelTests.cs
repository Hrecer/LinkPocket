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
    public async Task 移动链接后_不自行刷新_刷新归事件链_无订阅宿主需显式刷新()
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

            // 口径（与删除流一致，WARNINGS #18）：写操作**不自行刷新**——界面刷新由后端事件链
            // （MainViewModel 300ms 防抖 → RefreshPreservingSelectionAsync）负责；
            // 显式 + 事件双重刷新就是"移动/粘贴后主栏刷两遍"的根因。
            Assert.Contains(vm.Rows, r => r.Id == link.LinkId);       // 行仍是旧状态（未被显式刷新）

            // 无事件订阅的宿主（单测/无头）自己调刷新即可取到新状态
            await vm.RefreshPreservingSelectionAsync();
            Assert.DoesNotContain(vm.Rows, r => r.Id == link.LinkId);  // 已移出目录 A 的行
            Assert.Empty(vm.Rows);                                     // A 目录此刻为空

            // 遮罩口径（区分"刷新次数"与"要不要给用户看"两件事）：
            // 写操作与随后的后台刷新都不亮遮罩；只有"导航加载"亮，且完成即收。
            Assert.False(vm.IsNavigating);                             // 移动（写操作）不亮遮罩
            await vm.RefreshPreservingSelectionAsync();
            Assert.False(vm.IsNavigating);                             // 后台事件口径的刷新也不亮遮罩

            // 导航加载（navigating: true）完成后遮罩必须收回——不留僵住的遮罩
            //（引擎在本环境可能同步完成整个加载，故只锁"终态必为收起"，不锁中途瞬时值）
            await vm.RefreshAsync(navigating: true);
            Assert.False(vm.IsNavigating);

            // 行入场动画的触发口径：只有导航加载的那次刷新链才允许播（后台刷新一律静默）——
            // 界面的行错峰入场动画订阅 RefreshCompleted，参数即"是不是导航加载"。
            var refreshEvents = new List<bool>();
            vm.RefreshCompleted += (_, wasNavigation) => refreshEvents.Add(wasNavigation);
            await vm.LoadAsync(null);                        // 打开目录（导航）→ 播
            await vm.RefreshPreservingSelectionAsync();      // 后台刷新（写操作后的防抖口径）→ 不播
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

            // 行序列（Id + 顺序）没变 ⇒ 差分刷新走 ApplyFrom **原地写回**，行对象不换、一次 Reset 都不发
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
    public async Task 目录树_全量链接叶子_文件夹在前链接在后各自名称升序_子目录链接注入其下()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            // 纯 ASCII 名称：CurrentCulture 升序结果确定；验证树与主区同口径（升序：文件夹组在前、链接组在后）
            var linkA = (await client.LinkCreateAsync("https://a-root.example", title: "Alpha 根级", autoFetchMetadata: false)).Data!;
            var linkZ = (await client.LinkCreateAsync("https://z-root.example", title: "Zulu 根级", autoFetchMetadata: false)).Data!;
            await client.FolderCreateAsync("Charlie 文件夹");
            var delta = (await client.FolderCreateAsync("Delta 文件夹")).Data!;
            var bravo = (await client.LinkCreateAsync("https://sub.example", title: "Bravo 子级",
                listId: delta.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var root = Assert.Single(vm.FolderTree);
            Assert.True(root.IsRoot);
            // 链接叶子**按需加载**（展开才注入）：虚根默认展开 ⇒ 等它加载完再断言
            await vm.WaitForTreeLinksAsync(root);

            // 文件夹组在前（名称升序）、链接组在后（名称升序）——链接绝不骑在文件夹之前
            var children = root.Children;
            Assert.Equal(4, children.Count);
            Assert.Equal(new[] { "Charlie 文件夹", "Delta 文件夹", "Alpha 根级", "Zulu 根级" },
                children.Select(c => c.Name).ToArray());
            Assert.False(children[0].IsLink);
            Assert.False(children[1].IsLink);
            // 根级链接叶子：ParentId = null（所属目录 = 根）
            Assert.True(children[2].IsLink && children[2].Id == linkA.LinkId && children[2].ParentId == null);
            Assert.True(children[3].IsLink && children[3].Id == linkZ.LinkId && children[3].ParentId == null);

            // 子目录（Delta）的直接链接在其**展开时**注入（名称升序叶子；ParentId = 所属目录）
            var deltaNode = children[1];
            Assert.Equal(delta.FolderId, deltaNode.FolderId);
            Assert.Empty(deltaNode.Children);          // 未展开 = 没有叶子（懒加载）
            deltaNode.IsExpanded = true;
            await vm.WaitForTreeLinksAsync(deltaNode);
            var deltaLeaf = Assert.Single(deltaNode.Children);
            Assert.True(deltaLeaf.IsLink && deltaLeaf.Id == bravo.LinkId && deltaLeaf.ParentId == delta.FolderId);
            Assert.Equal("Bravo 子级", deltaLeaf.Name);
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
            var node = Assert.Single(vm.FolderTree[0].Children, c => !c.IsLink);
            Assert.Equal(a.FolderId, node.FolderId);

            await vm.SelectTreeNodeAsync(node);

            Assert.Equal(a.FolderId, vm.CurrentFolderId);                 // 已进入该目录
            Assert.True(vm.IsSelectedId(a.FolderId));                     // 该文件夹进入选中集合（树高亮唯一事实源）
            var nodeNow = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            Assert.True(nodeNow.IsSelected);                              // 树节点投影高亮（重建后仍成立）
            Assert.Equal("One", Assert.Single(vm.Rows).Name);             // 主栏显示该目录内容
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 目录树_链接叶子单击_定位到父目录并选中该行()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var link = (await client.LinkCreateAsync("https://a.example/1", title: "One",
                listId: a.FolderId, autoFetchMetadata: false)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var aNode = vm.FolderTree[0].Children.Single(c => !c.IsLink);
            aNode.IsExpanded = true;                       // 叶子按需加载：展开该目录
            await vm.WaitForTreeLinksAsync(aNode);
            var leaf = Assert.Single(aNode.Children);
            Assert.True(leaf.IsLink && leaf.Id == link.LinkId && leaf.ParentId == a.FolderId);

            await vm.SelectTreeNodeAsync(leaf);

            Assert.Equal(a.FolderId, vm.CurrentFolderId);     // 进入链接所在父目录
            Assert.True(vm.IsSelectedId(link.LinkId));        // 该链接进入选中集合（主栏 + 树叶子同时投影）
            Assert.Equal(link.LinkId, Assert.Single(vm.SelectedRows).Id);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 目录树_根节点单击_进入根目录且虚根永不选中()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            await client.LinkCreateAsync("https://a.example/1", title: "One", listId: a.FolderId, autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            // 已在根：单击「全部书签」= 重载根目录（点"当前所在位置"同样刷新，Windows 口径），虚根永不写选中
            var root = Assert.Single(vm.FolderTree);
            Assert.True(root.IsRoot);
            var rowBefore = vm.Rows.First();
            await vm.SelectTreeNodeAsync(root);
            Assert.Null(vm.CurrentFolderId);                    // 仍在根目录
            // "刷新发生过"的证据 = **树确实重建**（新节点实例）；**不能**再用"行实例不同"当代理——
            // 内容一致的刷新会**保留行集**（避免整表重建的等价跳过，见 BrowserViewModel.Refresh）。
            var rootAfterReload = Assert.Single(vm.FolderTree);
            Assert.NotSame(root, rootAfterReload);
            Assert.Equal(rowBefore.Id, vm.Rows.First().Id);
            Assert.False(rootAfterReload.IsSelected);            // 虚根不显示选中（重建后的新节点同样如此）
            Assert.False(vm.IsSelectedId(rootAfterReload.Id));   // 虚根无身份可选中（Id 为空，永不入集合）

            // 先进入子目录，再单击「全部书签」= 回根目录（导航；位置由面包屑表达，不产生选中高亮）
            var aNode = vm.FolderTree[0].Children.Single(c => !c.IsLink);
            await vm.SelectTreeNodeAsync(aNode);
            Assert.Equal(a.FolderId, vm.CurrentFolderId);

            var rootNow = Assert.Single(vm.FolderTree);
            await vm.SelectTreeNodeAsync(rootNow);
            Assert.Null(vm.CurrentFolderId);                      // 已回到根目录
            Assert.False(Assert.Single(vm.FolderTree).IsSelected); // 虚根仍不显示选中（重建后的新节点）
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

            // 写操作不显式刷新（WARNINGS #18）：模拟事件链的刷新取新状态
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