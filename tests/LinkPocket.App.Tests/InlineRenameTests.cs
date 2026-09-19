using System;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 就地重命名（Windows 口径）金标准：**唯一事实来源 = VM 重命名会话状态**，
/// 行（BrowserRowViewModel.IsRenaming）与树（FolderNode.IsRenaming）都是它的投影。
/// 覆盖：进入/提交/取消/空名还原、链接改标题、同层撞名由引擎编号、编辑面（主栏 vs 左栏树）互斥、
/// 新建文件夹（去弹窗 → 默认名 + 置尾 + 选中 + 直接进入改名）。
/// </summary>
public class InlineRenameTests
{
    [Fact]
    public async Task 改名_文件夹_提交后名称已改_且会话结束()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("旧名")).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var row = Assert.Single(vm.Rows);

            vm.BeginRenameRow(row);
            Assert.True(vm.IsRenaming);
            Assert.True(row.IsRenaming);
            Assert.Equal("旧名", vm.EditingName);          // 进入即带出原名（编辑框全选覆盖输入）

            vm.EditingName = "新名";
            await vm.CommitRenameAsync();

            Assert.False(vm.IsRenaming);                    // 会话结束
            Assert.Equal("", vm.EditingName);
            Assert.False(row.IsRenaming);                   // 投影随会话归零
            Assert.Equal("已重命名为「新名」", vm.StatusText);

            var stored = await client.FolderGetAsync(a.FolderId);
            Assert.Equal("新名", stored.Name);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 改名_链接_改的是标题()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://a.example", title: "旧标题", autoFetchMetadata: false)).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            vm.BeginRenameRow(vm.Rows[0]);
            vm.EditingName = "新标题";
            await vm.CommitRenameAsync();

            Assert.Equal("已重命名为「新标题」", vm.StatusText);
            var stored = await client.LinkGetAsync(link.LinkId);
            Assert.Equal("新标题", stored.Title);
            Assert.Equal("https://a.example", stored.Url);   // 只动标题，地址不变
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 改名_取消与空名_都是还原且不写库()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("原名")).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var row = vm.Rows[0];

            // Esc：只收会话
            vm.BeginRenameRow(row);
            vm.EditingName = "临时输入";
            vm.CancelRename();
            Assert.False(vm.IsRenaming);
            Assert.Equal("原名", (await client.FolderGetAsync(a.FolderId)).Name);

            // 空名 = 还原（Windows 口径：空名不落库）
            vm.BeginRenameRow(row);
            vm.EditingName = "   ";
            await vm.CommitRenameAsync();
            Assert.False(vm.IsRenaming);
            Assert.Equal("原名", (await client.FolderGetAsync(a.FolderId)).Name);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 改名_同层撞名_由引擎自动编号并如实展示()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("工作")).Data!;
            var b = (await client.FolderCreateAsync("资料")).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            vm.BeginRenameRow(vm.Rows.First(r => r.Id == b.FolderId));
            vm.EditingName = "工作";                         // 撞上同层的「工作」
            await vm.CommitRenameAsync();

            Assert.Equal("已重命名为「工作 (2)」", vm.StatusText);   // 展示引擎返回的最终名
            Assert.Equal("工作 (2)", (await client.FolderGetAsync(b.FolderId)).Name);
            Assert.Equal("工作", (await client.FolderGetAsync(a.FolderId)).Name);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 改名_编辑面互斥_同一实体只在一处显示编辑框()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var row = vm.Rows.First(r => r.Id == a.FolderId);
            var node = vm.FolderTree.SelectMany(Flatten).First(n => n.FolderId == a.FolderId);

            // 主栏进入：行亮、树不亮
            vm.BeginRenameRow(row);
            Assert.True(row.IsRenaming);
            Assert.False(node.IsRenaming);

            // 换成左栏树进入：树亮、行不亮（同一实体只在一处编辑，避免两个编辑框互相抢焦点）
            vm.BeginRenameNode(node);
            Assert.False(row.IsRenaming);
            Assert.True(node.IsRenaming);

            // 取消：两处都归零
            vm.CancelRename();
            Assert.False(row.IsRenaming);
            Assert.False(node.IsRenaming);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 改名_虚根不可进入_根节点永不进入改名()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.FolderCreateAsync("A");
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            vm.BeginRenameNode(vm.FolderTree[0]);            // 「全部书签」虚根不是实体
            Assert.False(vm.IsRenaming);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 新建文件夹_默认名_置尾选中并直接进入改名()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.FolderCreateAsync("A");
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            vm.NewFolderCommand.Execute(null);
            Assert.True(await WaitUntilAsync(() => vm.StatusText.StartsWith("已创建文件夹"), TimeSpan.FromSeconds(5)),
                $"新建未在超时内完成（状态：{vm.StatusText}）");

            // 写操作不显式刷新：模拟事件链刷新取新状态
            await vm.RefreshPreservingSelectionAsync();

            // 临时置尾（Windows：新建/复制项首次显示在末位）+ 选中 + 已进入就地改名
            var created = Assert.Single(vm.SelectedRows);
            Assert.Equal("新建文件夹", created.Name);
            Assert.Equal(created.Id, vm.Rows[^1].Id);
            Assert.True(vm.IsRenaming);
            Assert.True(created.IsRenaming);
            Assert.Equal("新建文件夹", vm.EditingName);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 新建文件夹_同层已有同名_默认名自动编号()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.FolderCreateAsync("新建文件夹");
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            vm.NewFolderCommand.Execute(null);
            Assert.True(await WaitUntilAsync(() => vm.StatusText.StartsWith("已创建文件夹"), TimeSpan.FromSeconds(5)));
            await vm.RefreshPreservingSelectionAsync();

            var created = Assert.Single(vm.SelectedRows);
            Assert.Equal("新建文件夹 (2)", created.Name);
            Assert.Equal("新建文件夹 (2)", vm.EditingName);   // 改名编辑框里也是编号后的名
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>
    /// 切换改名目标（右键另一项改名 / 新建第二个文件夹）→ 旧会话按 Windows 口径**提交**，输入不丢；
    /// 新会话就位（这正是"新建第二个文件夹时第一个退出改名、第二个正常进入"的 VM 侧口径）。
    /// </summary>
    [Fact]
    public async Task 改名_切换目标_旧会话提交且输入不丢()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var rowA = vm.Rows.First(r => r.Id == a.FolderId);
            var rowB = vm.Rows.First(r => r.Id == b.FolderId);

            vm.BeginRenameRow(rowA);
            vm.EditingName = "A 改名";        // 用户已输入内容
            vm.BeginRenameRow(rowB);           // 直接切到另一个目标

            Assert.True(vm.IsRenaming);
            Assert.True(rowB.IsRenaming);      // 新会话就位
            Assert.False(rowA.IsRenaming);     // 旧会话已收（投影归零）
            Assert.Equal("B", vm.EditingName);

            Assert.True(await WaitUntilAsyncAsync(
                    async () => (await client.FolderGetAsync(a.FolderId)).Name == "A 改名",
                    TimeSpan.FromSeconds(5)),
                "切换目标后旧会话应被提交（用户输入不得丢失）");
            Assert.Equal("B", (await client.FolderGetAsync(b.FolderId)).Name);   // 新会话尚未提交
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>
    /// 页面级收尾（右键菜单打开 → <see cref="BrowserViewModel.CommitActiveRename"/>）：
    /// 编辑态**立即退出**（输入保留），提交**挂起到菜单关闭**（<see cref="BrowserViewModel.FlushDeferredCommit"/>）才落库
    /// ——推迟是为了不让提交触发的刷新把承载菜单的行销毁（菜单"一闪就没了"）。
    /// 之后任何**迟到**的重复提交（旧编辑框的失焦等）都必须是空操作，绝不影响其它实体。
    /// </summary>
    [Fact]
    public async Task 改名_页面级收尾_提交当前会话_迟到提交为空操作()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var a = (await client.FolderCreateAsync("A")).Data!;
            var b = (await client.FolderCreateAsync("B")).Data!;
            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);

            var rowA = vm.Rows.First(r => r.Id == a.FolderId);
            vm.BeginRenameRow(rowA);
            vm.EditingName = "A 已改名";

            vm.CommitActiveRename();           // 右键菜单打开时的收尾
            Assert.False(vm.IsRenaming);       // 编辑态立即退出
            Assert.Equal("A", (await client.FolderGetAsync(a.FolderId)).Name);   // 菜单未关闭 → 尚未落库

            vm.FlushDeferredCommit();          // 菜单关闭 → 挂起的提交落地
            Assert.True(await WaitUntilAsyncAsync(
                    async () => (await client.FolderGetAsync(a.FolderId)).Name == "A 已改名",
                    TimeSpan.FromSeconds(5)),
                "菜单关闭后应落地提交（保留输入）");

            // 迟到/重复提交：会话已空 → 空操作（既不报错也不改任何实体）
            await vm.CommitRenameAsync();
            vm.CancelRename();
            vm.FlushDeferredCommit();
            Assert.False(vm.IsRenaming);
            Assert.Equal("B", (await client.FolderGetAsync(b.FolderId)).Name);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    private static System.Collections.Generic.IEnumerable<FolderNode> Flatten(FolderNode node)
    {
        yield return node;
        foreach (var child in node.Children.SelectMany(Flatten)) yield return child;
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

    /// <summary>异步条件版（条件本身要 await 引擎查询时用）。</summary>
    private static async Task<bool> WaitUntilAsyncAsync(Func<Task<bool>> condition, TimeSpan timeout)
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
