using System;
using System.Threading.Tasks;
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
/// **解析在内核**（引擎查询 <c>locate.resolve</c>：类型判别 / 容器目录推导 / 路径都在引擎里，一切读取皆查询），
/// 本组件只把解析结果翻译成界面端口上的三个原语——切到浏览页、进入容器目录、选中目标行；
/// 「ID 不存在」由引擎以 <c>ENTITY_NOT_FOUND</c> 表达（零兼容：查询就报错，不返回 null），
/// 本组件把它映射为 <see cref="LocateStatus.NotFound"/> 反馈——不发散业务码。
/// 因此任何消费者（界面 / 无头宿主 / 未来的 AI）都能用同一套语义定位。
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

        try
        {
            var resolved = await _client.LocateResolveAsync(id.Trim());
            var kind = resolved.Kind == "folder" ? ContentKind.Folder : ContentKind.Link;

            var host = _hostProvider();
            if (host == null)
                return new LocateResult(LocateStatus.NoHost, kind, resolved.ContainerFolderId, resolved.Id, "界面宿主不可用");

            host.ShowBrowser();

            // 跳转 = 进入容器目录（null = 根「全部书签」）+ 选中目标行本身
            var selected = await host.EnterAndSelectAsync(resolved.ContainerFolderId, resolved.Id);
            return selected
                ? new LocateResult(LocateStatus.Success, kind, resolved.ContainerFolderId, resolved.Id, null)
                : new LocateResult(LocateStatus.RowMissing, kind, resolved.ContainerFolderId, resolved.Id, "目标行未出现在所在目录");
        }
        catch (LinkPocket.Contracts.EngineException ex) when (ex.Error.Code == EngineErrors.EntityNotFound)
        {
            return kindHint switch
            {
                ContentKind.Folder => LocateResult.Missing(ContentKind.Folder),
                ContentKind.Link => LocateResult.Missing(ContentKind.Link),
                _ => new LocateResult(LocateStatus.NotFound, null, null, null, "未找到匹配的 ID"),
            };
        }
        catch (Exception ex)
        {
            LpLog.Error("定位失败", ex);
            return new LocateResult(LocateStatus.Failed, null, null, id, ex.Message);
        }
    }

    /// <summary>按链接 ID 定位（解析为该类型才成功；指向文件夹时按"未找到该 ID"如实反馈）。</summary>
    public async Task<LocateResult> LocateLinkAsync(string linkId)
    {
        var result = await LocateAsync(linkId, ContentKind.Link);
        return result.IsSuccess && result.Kind != ContentKind.Link ? LocateResult.Missing(ContentKind.Link) : result;
    }

    /// <summary>按文件夹 ID 定位（解析为该类型才成功；指向链接时按"未找到该 ID"如实反馈）。</summary>
    public async Task<LocateResult> LocateFolderAsync(string folderId)
    {
        var result = await LocateAsync(folderId, ContentKind.Folder);
        return result.IsSuccess && result.Kind != ContentKind.Folder ? LocateResult.Missing(ContentKind.Folder) : result;
    }
}
