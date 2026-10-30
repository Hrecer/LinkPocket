namespace LinkPocket.Kernel;

/// <summary>页面元数据抓取结果（&lt;title&gt;/og:/description/favicon）。</summary>
/// <param name="BlockedByVerification">
/// 全部路径走完后仍然只拿到反爬 / 人机校验中间页（<see cref="AntiBotInterstitial"/>）——
/// 标题与描述的确拿不到，且原因是"站点拒绝自动化访问"，不是"页面没写"。
/// 搬运这一位是为了让界面说得出真实原因（否则只有一片空白）。
/// </param>
public sealed record PageMetadata(
    string? Title,
    string? Description,
    string? FaviconUrl,
    bool BlockedByVerification = false);

/// <summary>
/// 元数据解析（自 LinkService 正则组下沉）：解析 HTML 提取标题/描述/favicon。
/// 网络抓取在模块层（闸外），本契约只做纯解析。
/// </summary>
public interface IMetadataParser
{
    PageMetadata Parse(string html, Uri pageUri);
}
