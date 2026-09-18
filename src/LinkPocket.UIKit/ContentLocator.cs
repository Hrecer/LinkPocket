using System;
using System.Threading.Tasks;
using LinkPocket.Api;
using LinkPocket.Contracts;

namespace LinkPocket.Services;

/// <summary>定位目标的类型。ID 在链接与文件夹之间唯一，因此无需调用方指定。</summary>
public enum ContentKind
{
    Link,
    Folder,
}

/// <summary>定位结果状态。</summary>
public enum LocateStatus
{
    /// <summary>已定位：进入了目标所在目录并选中了目标行。</summary>
    Success,
    /// <summary>ID 不存在（链接与文件夹都没查到）。</summary>
    NotFound,
    /// <summary>ID 为空。</summary>
    EmptyId,
    /// <summary>界面宿主未注册（例如无 UI 的会话），定位无法执行。</summary>
    NoHost,
    /// <summary>目标存在但所在目录里找不到对应行（数据竞态：刚被移走/删除）。</summary>
    RowMissing,
    /// <summary>执行过程中出错（数据服务异常等）。</summary>
    Failed,
}

/// <summary>
/// 一次定位的完整结果，便于调用方给出准确反馈，而不是只知道"成功/失败"。
/// </summary>
public readonly record struct LocateResult(
    LocateStatus Status,
    ContentKind? Kind,
    string? ContainerFolderId,
    string? TargetId,
    string? Message)
{
    public bool IsSuccess => Status == LocateStatus.Success;

    public static LocateResult Empty() => new(LocateStatus.EmptyId, null, null, null, "请输入ID");

    public static LocateResult Missing(ContentKind? kind = null) => new(
        LocateStatus.NotFound, kind, null, null,
        kind == ContentKind.Folder ? "未找到匹配的文件夹ID" : "未找到匹配的链接ID");
}

/// <summary>
/// 浏览页宿主端口：定位组件与界面之间的唯一对接点。
/// 宿主只需提供两个原语——切到浏览页、进入目录并选中一行；
/// 定位算法（类型判别、容器目录推导）全部在 <see cref="ContentLocator"/> 内，界面不参与。
/// 这样任何页面都能调用定位能力，且组件不认识任何具体页面类型（零耦合）。
/// </summary>
public interface IBrowserLocateHost
{
    /// <summary>切到浏览页（纯导航，不涉及定位细节）。</summary>
    void ShowBrowser();

    /// <summary>
    /// 进入指定目录（null = 根「全部书签」）并选中其中一行（可为链接行或文件夹行），
    /// 选中后由视图滚动到可见。返回该行是否真的被选中。
    /// </summary>
    Task<bool> EnterAndSelectAsync(string? folderId, string rowId);
}

/// <summary>
/// 「跳转」的标准化契约（与界面实现无关，可被任何页面/工具复用）：
/// 跳转 = 进入目标所在目录 → 选中目标那一行。
/// - 链接：进入其所属文件夹（ListId，null = 根），选中链接行；
/// - 文件夹：进入其父目录，选中该文件夹行。
/// 不再有"展开详情页"这条岔路——需要详情请显式调用详情能力。
/// </summary>
public interface IContentLocator
{
    /// <summary>按 ID 定位（自动判别链接/文件夹）。<paramref name="kindHint"/> 仅用于消歧提示。</summary>
    Task<LocateResult> LocateAsync(string id, ContentKind? kindHint = null);

    /// <summary>按链接 ID 定位：进入其所属目录并选中该链接行。</summary>
    Task<LocateResult> LocateLinkAsync(string linkId);

    /// <summary>按文件夹 ID 定位：进入其父目录并选中该文件夹行。</summary>
    Task<LocateResult> LocateFolderAsync(string folderId);
}

/// <summary>
/// 内容定位器（前端服务层组件，独立于任何界面）：
/// 依赖两项抽象——引擎客户端门面 <see cref="EngineClient"/> 与界面端口 <see cref="IBrowserLocateHost"/>。
/// 界面只负责"执行"，算法（目标类型判别 / 容器目录推导 / 结果建模）都在这里。
/// 「ID 不存在」由引擎以 <c>LP.STATE.001</c> 表达（零兼容：查询就报错，不返回 null），
/// 本组件把它映射为 <see cref="LocateStatus.NotFound"/> 反馈——不发散业务码。
/// </summary>
public sealed class ContentLocator : IContentLocator
{
    private readonly EngineClient _client;
    private readonly Func<IBrowserLocateHost?> _hostProvider;

    public ContentLocator(EngineClient client, Func<IBrowserLocateHost?> hostProvider)
    {
        _client = client;
        _hostProvider = hostProvider;
    }

    public async Task<LocateResult> LocateAsync(string id, ContentKind? kindHint = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return LocateResult.Empty();

        var targetId = id.Trim();

        // ID 在链接与文件夹之间唯一：先按提示类型查，未命中再用另一类型兜底（最多两次单条查询）。
        var order = kindHint == ContentKind.Folder
            ? new[] { ContentKind.Folder, ContentKind.Link }
            : new[] { ContentKind.Link, ContentKind.Folder };

        LocateResult? lastMiss = null;
        foreach (var kind in order)
        {
            var result = kind == ContentKind.Link
                ? await LocateLinkAsync(targetId)
                : await LocateFolderAsync(targetId);

            if (result.Status != LocateStatus.NotFound) return result;
            lastMiss = result;
        }

        return lastMiss ?? LocateResult.Missing();
    }

    public async Task<LocateResult> LocateLinkAsync(string linkId)
    {
        if (string.IsNullOrWhiteSpace(linkId)) return LocateResult.Empty();

        try
        {
            var link = await _client.LinkGetAsync(linkId.Trim());
            var host = _hostProvider();
            if (host == null)
                return new LocateResult(LocateStatus.NoHost, ContentKind.Link, link.ListId, link.LinkId, "界面宿主不可用");

            host.ShowBrowser();

            // 链接：容器目录 = 它所属的文件夹（null = 根「全部书签」），选中链接行本身
            var selected = await host.EnterAndSelectAsync(link.ListId, link.LinkId);
            return selected
                ? new LocateResult(LocateStatus.Success, ContentKind.Link, link.ListId, link.LinkId, null)
                : new LocateResult(LocateStatus.RowMissing, ContentKind.Link, link.ListId, link.LinkId, "目标行未出现在所在目录");
        }
        catch (LinkPocket.Contracts.EngineException ex) when (ex.Error.Code == EngineErrors.EntityNotFound)
        {
            return LocateResult.Missing(ContentKind.Link);
        }
        catch (Exception ex)
        {
            Logger.Error("定位链接失败", ex);
            return new LocateResult(LocateStatus.Failed, ContentKind.Link, null, linkId, ex.Message);
        }
    }

    public async Task<LocateResult> LocateFolderAsync(string folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId)) return LocateResult.Empty();

        try
        {
            var folder = await _client.FolderGetAsync(folderId.Trim());
            var host = _hostProvider();
            if (host == null)
                return new LocateResult(LocateStatus.NoHost, ContentKind.Folder, folder.ParentId, folder.FolderId, "界面宿主不可用");

            host.ShowBrowser();

            // 文件夹：容器目录 = 它的父目录（null = 根），选中文件夹行本身
            var selected = await host.EnterAndSelectAsync(folder.ParentId, folder.FolderId);
            return selected
                ? new LocateResult(LocateStatus.Success, ContentKind.Folder, folder.ParentId, folder.FolderId, null)
                : new LocateResult(LocateStatus.RowMissing, ContentKind.Folder, folder.ParentId, folder.FolderId, "目标行未出现在父目录");
        }
        catch (LinkPocket.Contracts.EngineException ex) when (ex.Error.Code == EngineErrors.EntityNotFound)
        {
            return LocateResult.Missing(ContentKind.Folder);
        }
        catch (Exception ex)
        {
            Logger.Error("定位文件夹失败", ex);
            return new LocateResult(LocateStatus.Failed, ContentKind.Folder, null, folderId, ex.Message);
        }
    }
}
