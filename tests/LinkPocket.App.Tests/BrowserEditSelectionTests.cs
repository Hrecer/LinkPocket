using System.Windows.Input;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 命令栏「编辑」的可用性矩阵（整页编辑器）：**只有链接**能编辑，且必须恰好选中一个。
/// <para>
/// 之所以把这组前提逐条钉住：命令的 Execute **不复查 CanExecute**（<c>RelayCommand.Execute</c> 直调委托），
/// 所以"按钮灰着"只是第一道门；双击重入、探针、自动化都能绕过它。判据必须同时落在
/// ① CanExecute（界面灰不灰）与 ② 方法体内的守卫（真被调到时开不开）两处。
/// </para>
/// </summary>
public class BrowserEditSelectionTests
{
    [Fact]
    public async Task 编辑_单选链接可用_执行后打开整页编辑器()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://a.example/1", title: "L1", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var link = Assert.Single(vm.Rows, r => !r.IsFolder);

            vm.SelectRowWithModifiers(link, ModifierKeys.None);

            Assert.True(vm.EditSelectionCommand.CanExecute(null));
            vm.EditSelectionCommand.Execute(null);
            Assert.True(vm.IsEditorPageOpen);
            Assert.NotNull(vm.EditorPage);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 编辑_未选中时不可用_直接调用也不开编辑器()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://a.example/1", title: "L1", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            vm.ClearSelection();

            Assert.False(vm.EditSelectionCommand.CanExecute(null));
            vm.EditSelectionCommand.Execute(null);   // 旁路：命令面必须自己挡住
            Assert.False(vm.IsEditorPageOpen);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 编辑_选中文件夹不可用_直接调用也不开编辑器()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.FolderCreateAsync("A");

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var folder = Assert.Single(vm.Rows, r => r.IsFolder);

            vm.SelectRowWithModifiers(folder, ModifierKeys.None);

            Assert.False(vm.EditSelectionCommand.CanExecute(null));
            vm.EditSelectionCommand.Execute(null);
            Assert.False(vm.IsEditorPageOpen);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 编辑_多选链接不可用_直接调用也不开编辑器()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://a.example/1", title: "L1", autoFetchMetadata: false);
            await client.LinkCreateAsync("https://a.example/2", title: "L2", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var rows = vm.Rows.Where(r => !r.IsFolder).ToList();
            vm.SelectRowWithModifiers(rows[0], ModifierKeys.None);
            vm.SelectRowWithModifiers(rows[1], ModifierKeys.Control);
            Assert.Equal(2, vm.SelectionCount);

            // 多选没有"要编辑的那一项"——与「重命名」多选不可用同口径
            Assert.False(vm.EditSelectionCommand.CanExecute(null));
            vm.EditSelectionCommand.Execute(null);
            Assert.False(vm.IsEditorPageOpen);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 编辑_文件夹加链接混合选择不可用()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.FolderCreateAsync("A");
            await client.LinkCreateAsync("https://a.example/1", title: "L1", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var folder = vm.Rows.First(r => r.IsFolder);
            var link = vm.Rows.First(r => !r.IsFolder);
            vm.SelectRowWithModifiers(folder, ModifierKeys.None);
            vm.SelectRowWithModifiers(link, ModifierKeys.Control);

            Assert.False(vm.EditSelectionCommand.CanExecute(null));
            vm.EditSelectionCommand.Execute(null);
            Assert.False(vm.IsEditorPageOpen);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 编辑_路径编辑态让位_退出后恢复()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://a.example/1", title: "L1", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            vm.SelectRowWithModifiers(vm.Rows.First(r => !r.IsFolder), ModifierKeys.None);
            Assert.True(vm.EditSelectionCommand.CanExecute(null));

            vm.EnterPathEditCommand.Execute(null);   // 地址栏进入编辑态：命令栏动作整体让位
            Assert.True(vm.IsPathEditing);
            Assert.False(vm.EditSelectionCommand.CanExecute(null));

            vm.CancelPathEditCommand.Execute(null);
            Assert.False(vm.IsPathEditing);
            Assert.True(vm.EditSelectionCommand.CanExecute(null));
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>
    /// 连点两下：第二下必须落在守卫上，不能把用户刚输的内容顶掉（编辑器实例被整体替换）。
    /// </summary>
    [Fact]
    public async Task 编辑_重复执行复用同一次开页_不产生第二个编辑器()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://a.example/1", title: "L1", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            vm.SelectRowWithModifiers(vm.Rows.First(r => !r.IsFolder), ModifierKeys.None);

            vm.EditSelectionCommand.Execute(null);
            var first = vm.EditorPage;
            Assert.NotNull(first);

            vm.EditSelectionCommand.Execute(null);   // 第二击（双击 / 自动化）：CanExecute 已为假，但仍直接调
            Assert.Same(first, vm.EditorPage);
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }
}
