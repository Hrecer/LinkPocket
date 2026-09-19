using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 定位模块（<c>locate.resolve</c>）：一个 ID 在目录里的位置——类型 / 容器目录 / 显示路径。
/// 判据 = 可观测结果（容器与路径逐字断言、缺失 ID 的错误码），不比"调用没抛异常"。
/// </summary>
public class LocateModuleTests
{
    [Fact]
    public async Task 链接_解析出容器目录与显示路径()
    {
        var (engine, _, _) = TestHost.Create();
        var a = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "资料" })).Data!;
        var b = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "子层", parent_id = a.FolderId })).Data!;
        var link = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://locate.example/a", title = "定位目标", list_id = b.FolderId })).Data!;

        var resolved = await engine.QueryAsync<LocateResolveDto>("locate.resolve", new { id = link.LinkId });

        Assert.Equal("link", resolved.Kind);
        Assert.Equal(link.LinkId, resolved.Id);
        Assert.Equal("定位目标", resolved.Name);
        Assert.Equal(b.FolderId, resolved.ContainerFolderId);            // 进这一层才能看到它
        Assert.Equal("全部书签 / 资料 / 子层", resolved.ContainerPath);
        Assert.Equal("全部书签 / 资料 / 子层 / 定位目标", resolved.Path);
    }

    [Fact]
    public async Task 文件夹_解析出父目录与显示路径()
    {
        var (engine, _, _) = TestHost.Create();
        var a = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "资料" })).Data!;
        var b = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "子层", parent_id = a.FolderId })).Data!;

        var resolved = await engine.QueryAsync<LocateResolveDto>("locate.resolve", new { id = b.FolderId });

        Assert.Equal("folder", resolved.Kind);
        Assert.Equal(b.FolderId, resolved.Id);
        Assert.Equal(a.FolderId, resolved.ContainerFolderId);            // 文件夹的容器 = 它的父目录
        Assert.Equal("全部书签 / 资料", resolved.ContainerPath);
        Assert.Equal("全部书签 / 资料 / 子层", resolved.Path);
    }

    [Fact]
    public async Task 根级目标_容器为null_路径以根名开头()
    {
        var (engine, _, _) = TestHost.Create();
        var link = (await engine.ExecuteAsync<LinkDto>("links.create",
            new { url = "https://locate.example/root", title = "根级链接" })).Data!;

        var resolved = await engine.QueryAsync<LocateResolveDto>("locate.resolve", new { id = link.LinkId });

        Assert.Null(resolved.ContainerFolderId);                          // 根 = null（零哨兵）
        Assert.Equal("全部书签", resolved.ContainerPath);
        Assert.Equal("全部书签 / 根级链接", resolved.Path);
    }

    [Fact]
    public async Task 单层文件夹在根_容器为null()
    {
        var (engine, _, _) = TestHost.Create();
        var a = (await engine.ExecuteAsync<FolderDto>("folders.create", new { name = "顶层夹" })).Data!;

        var resolved = await engine.QueryAsync<LocateResolveDto>("locate.resolve", new { id = a.FolderId });

        Assert.Equal("folder", resolved.Kind);
        Assert.Null(resolved.ContainerFolderId);
        Assert.Equal("全部书签 / 顶层夹", resolved.Path);
    }

    [Fact]
    public async Task ID不存在_报ENTITY_NOT_FOUND_不返回null()
    {
        var (engine, _, _) = TestHost.Create();

        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            engine.QueryAsync<LocateResolveDto>("locate.resolve", new { id = "000000000000" }));
        Assert.Equal(EngineErrors.EntityNotFound, ex.Error.Code);
    }
}
