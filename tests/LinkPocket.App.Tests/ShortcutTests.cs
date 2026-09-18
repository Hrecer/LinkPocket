using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using LinkPocket.Input;
using LinkPocket.ViewModels;
using LinkPocket.Views.Browser;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 快捷键子系统金标准（Phase 3）：作用域仲裁（同键两栏不同语义）+ 键位表完整性
/// + 键盘导航行为（主栏 ↑/↓/End、左栏 ↑/↓ 可见顺序）+ 新增命令（撤销/重做可用性、展开到当前位置、复制路径）。
/// 键位表 = <see cref="BrowserShortcuts"/>（唯一事实源）；本文件断言它的**可观测行为**，
/// 不复制一份键位清单（避免"测试与实现各写一份、改一处忘一处"）。
/// </summary>
public class ShortcutTests
{
    // —— 作用域仲裁（子系统纯逻辑，不需要 VM）——

    [Fact]
    public void 作用域解析_最近的活跃作用域胜出_同键两栏语义不同()
    {
        var registry = new ShortcutRegistry();
        var mainCmd = new ProbeCommand();
        var treeCmd = new ProbeCommand();
        registry.Register(new ShortcutBinding { Key = Key.Up, Scope = ShortcutScope.BrowserMain, Command = mainCmd, Description = "主栏上移" });
        registry.Register(new ShortcutBinding { Key = Key.Up, Scope = ShortcutScope.BrowserTree, Command = treeCmd, Description = "树上移" });

        // 主栏活跃 → 命中主栏那条；左栏活跃 → 命中左栏那条（同一键，语义各自不同）
        Assert.Same(mainCmd, registry.Resolve(ShortcutScope.BrowserMain, Key.Up, ModifierKeys.None)?.Command);
        Assert.Same(treeCmd, registry.Resolve(ShortcutScope.BrowserTree, Key.Up, ModifierKeys.None)?.Command);

        // 未在子作用域声明的键 → 由内向外回退到 Browser
        var browserCmd = new ProbeCommand();
        registry.Register(new ShortcutBinding { Key = Key.C, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Browser, Command = browserCmd, Description = "复制" });
        Assert.Same(browserCmd, registry.Resolve(ShortcutScope.BrowserMain, Key.C, ModifierKeys.Control)?.Command);
        Assert.Same(browserCmd, registry.Resolve(ShortcutScope.BrowserTree, Key.C, ModifierKeys.Control)?.Command);

        // 全局键在任何活跃作用域都能命中（Global ⊂ Browser ⊂ 两栏）
        var globalCmd = new ProbeCommand();
        registry.Register(new ShortcutBinding { Key = Key.E, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Global, Command = globalCmd, Description = "搜索" });
        foreach (var scope in new[] { ShortcutScope.BrowserMain, ShortcutScope.BrowserTree, ShortcutScope.Browser, ShortcutScope.Global })
            Assert.Same(globalCmd, registry.Resolve(scope, Key.E, ModifierKeys.Control)?.Command);

        // 未声明的键 → 未命中（绝不误吞）
        Assert.Null(registry.Resolve(ShortcutScope.BrowserMain, Key.Q, ModifierKeys.None));
    }

    [Fact]
    public void 注册表_同作用域重复键_装配即抛()
    {
        var registry = new ShortcutRegistry();
        registry.Register(new ShortcutBinding { Key = Key.N, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Browser, Command = new ProbeCommand(), Description = "A" });

        var ex = Assert.Throws<System.InvalidOperationException>(() =>
            registry.Register(new ShortcutBinding { Key = Key.N, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Browser, Command = new ProbeCommand(), Description = "B" }));
        Assert.Contains("快捷键冲突", ex.Message);
        Assert.Contains("Ctrl+N", ex.Message);   // 提示文案含键位（GestureText 唯一生成处）

        // 不同作用域同键不冲突（正是"↑/↓ 两栏各绑一条"的合法前提）
        registry.Register(new ShortcutBinding { Key = Key.N, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.BrowserTree, Command = new ProbeCommand(), Description = "树上新建" });
    }

    [Fact]
    public void 键位显示文本_生成口径()
    {
        Assert.Equal("Ctrl+Shift+N", new ShortcutBinding { Key = Key.N, Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Command = new ProbeCommand() }.GestureText);
        Assert.Equal("Alt+←", new ShortcutBinding { Key = Key.Left, Modifiers = ModifierKeys.Alt, Command = new ProbeCommand() }.GestureText);
        Assert.Equal("Esc", new ShortcutBinding { Key = Key.Escape, Command = new ProbeCommand() }.GestureText);
        Assert.Equal("Del", new ShortcutBinding { Key = Key.Delete, Command = new ProbeCommand() }.GestureText);
    }

    // —— 键位表完整性（不复制清单：断言"每条命令都被真实声明过"）——

    [Fact]
    public async System.Threading.Tasks.Task 浏览页键位表_可装配且覆盖两栏与全局()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var registry = BrowserShortcuts.CreateRegistry(vm);   // 重复键会在此抛（键位表自身的冲突检测）

            Assert.NotEmpty(registry.Bindings);
            foreach (var scope in new[] { ShortcutScope.Global, ShortcutScope.Browser, ShortcutScope.BrowserMain, ShortcutScope.BrowserTree })
                Assert.Contains(registry.Bindings, b => b.Scope == scope);

            // 两栏同键（↑）必须各自有声明——这是"同一键两栏语义不同"的结构前提
            Assert.Contains(registry.Bindings, b => b.Scope == ShortcutScope.BrowserMain && b.Key == Key.Up);
            Assert.Contains(registry.Bindings, b => b.Scope == ShortcutScope.BrowserTree && b.Key == Key.Up);
            Assert.NotSame(
                registry.Resolve(ShortcutScope.BrowserMain, Key.Up, ModifierKeys.None)!.Command,
                registry.Resolve(ShortcutScope.BrowserTree, Key.Up, ModifierKeys.None)!.Command);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    // —— 键盘导航行为 ——

    [Fact]
    public async System.Threading.Tasks.Task 主栏上下键_移动选中并在边界停住_End选中末项()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://a.example/1", title: "A", autoFetchMetadata: false);
            await client.LinkCreateAsync("https://b.example/2", title: "B", autoFetchMetadata: false);
            await client.LinkCreateAsync("https://c.example/3", title: "C", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var focused = new List<string>();
            vm.FocusRowRequested += (_, row) => focused.Add(row.Id);

            // 无选中时 ↓ → 首项；再 ↓ → 第二项（单选，且每次都请求滚入视口）
            vm.MoveSelectionCommand.Execute("down");
            Assert.Equal("A", Assert.Single(vm.SelectedRows).Name);
            vm.MoveSelectionCommand.Execute("down");
            Assert.Equal("B", Assert.Single(vm.SelectedRows).Name);
            Assert.Equal(2, focused.Count);

            // ↑ → 回上一项
            vm.MoveSelectionCommand.Execute("up");
            Assert.Equal("A", Assert.Single(vm.SelectedRows).Name);

            // 边界停住（Explorer 口径）：首项再 ↑ 仍是首项，末项再 ↓ 仍是末项
            vm.MoveSelectionCommand.Execute("up");
            Assert.Equal("A", Assert.Single(vm.SelectedRows).Name);

            vm.SelectLastCommand.Execute(null);
            Assert.Equal("C", Assert.Single(vm.SelectedRows).Name);
            vm.MoveSelectionCommand.Execute("down");
            Assert.Equal("C", Assert.Single(vm.SelectedRows).Name);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task 左栏上下键_按可见视觉顺序_未展开不进子级_展开后进入子级()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var a1 = (await client.FolderCreateAsync("A1", parentId: a.FolderId)).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            // 初始焦点锚 = 当前位置（根 = 虚根行）→ 第一次 ↓ 落到根下第一项 A（Windows 导航窗格口径）
            // 可见序列：虚根 → A → B（A 未展开，A1 不在可见序列里）
            vm.MoveTreeSelectionCommand.Execute("down");     // A → 选中 + 进入
            Assert.Equal(a.FolderId, vm.CurrentFolderId);
            Assert.True(vm.IsSelectedId(a.FolderId));

            vm.MoveTreeSelectionCommand.Execute("down");     // B（A 未展开 → A1 被跳过，直接到 B）
            Assert.Equal(b.FolderId, vm.CurrentFolderId);
            Assert.True(vm.IsSelectedId(b.FolderId));

            vm.MoveTreeSelectionCommand.Execute("up");       // 回 A
            Assert.Equal(a.FolderId, vm.CurrentFolderId);

            // 展开 A → 可见序列插入 A1；从 A 往下第一站应为 A1（而不是 B）
            var aNode = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            aNode.IsExpanded = true;
            await vm.LoadAsync(null);
            var aNodeNow = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            Assert.True(aNodeNow.IsExpanded, "树展开状态必须跨重建保持");

            await vm.SelectTreeNodeAsync(aNodeNow);           // 定位到 A（选中 + 进入）
            Assert.Equal(a.FolderId, vm.CurrentFolderId);
            vm.MoveTreeSelectionCommand.Execute("down");
            Assert.Equal(a1.FolderId, vm.CurrentFolderId);    // 展开后才进入子级
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    // —— 新增命令 ——

    [Fact]
    public async System.Threading.Tasks.Task 展开到当前位置_只展开不选中_与位置选中正交()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var a1 = (await client.FolderCreateAsync("A1", parentId: a.FolderId)).Data!;

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(a1.FolderId);
            vm.ClearSelection();
            Assert.False(vm.HasSelection);

            vm.ExpandTreeToCurrentCommand.Execute(null);

            // 祖先链（含自身）已展开 → 当前目录在树里可见
            var aNode = vm.FolderTree[0].Children.Single(c => c.FolderId == a.FolderId);
            var a1Node = aNode.Children.Single(c => c.FolderId == a1.FolderId);
            Assert.True(aNode.IsExpanded);
            Assert.True(a1Node.IsExpanded);

            // 只展开、不选中：选中集合仍为空（位置 ≠ 选中）
            Assert.False(vm.HasSelection);
            Assert.False(a1Node.IsSelected);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task 撤销重做_可用性按撤销栈态_可撤销命令后可撤销()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            // 刷新链收尾会轻量同步 undo 栈态：空库无可撤销
            Assert.False(vm.CanUndo);

            // 内核只收纳「Reversible + 有 UndoInverse」的命令进撤销栈——当前目录面只有
            // links.trash（逆向 trash.restore）与 trash.restore（逆向 links.trash）两条。
            // 故"删链接"才是可撤销写操作（新建/移动/复制/重命名都不在撤销栈里，见契约 §9）。
            var link = (await client.LinkCreateAsync("https://undo.example", title: "撤销目标", autoFetchMetadata: false)).Data!;
            await client.LinkTrashAsync(link.LinkId);
            await vm.RefreshUndoStateAsync();
            Assert.True(vm.CanUndo);

            // 撤销 → 链接回到原目录（ID 不变：恢复站保留原 ID）；重做转为可用（本地事实）
            vm.UndoCommand.Execute(null);
            Assert.True(await WaitUntilAsync(() => vm.CanRedo, System.TimeSpan.FromSeconds(5)),
                "执行过撤销后重做应转为可用");

            await vm.LoadAsync(null);
            Assert.Contains(vm.Rows, r => r.Id == link.LinkId);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task 复制路径_写系统剪贴板为面包屑文本()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(a.FolderId);

            // 剪贴板是 STA 资源（WPF 应用主线程天然满足；测试宿主线程是 MTA）→ 在专用 STA 线程上执行
            var (status, text) = RunOnSta(() =>
            {
                vm.CopyPathCommand.Execute(null);
                return (vm.StatusText, System.Windows.Clipboard.GetText());
            });

            Assert.Equal("已复制路径", status);
            Assert.Equal("全部书签 / A", text);   // 面包屑文本（可被 Alt+D 地址栏解析）
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>在专用 STA 线程上执行（剪贴板等 WPF 资源要求 STA；异常原样抛回测试线程）。</summary>
    private static T RunOnSta<T>(Func<T> action)
    {
        T result = default!;
        System.Exception? error = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { result = action(); }
            catch (System.Exception ex) { error = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
        return result;
    }

    private static async System.Threading.Tasks.Task<bool> WaitUntilAsync(Func<bool> condition, System.TimeSpan timeout)
    {
        var deadline = System.DateTime.UtcNow + timeout;
        while (System.DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await System.Threading.Tasks.Task.Delay(50);
        }
        return condition();
    }

    /// <summary>可辨识的命令桩（断言"解析到的是哪一条"）。</summary>
    private sealed class ProbeCommand : ICommand
    {
        public event System.EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}