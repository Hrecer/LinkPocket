namespace LinkPocket.Kernel;

/// <summary>页面元数据抓取结果（&lt;title&gt;/og:/description/favicon）。</summary>
public sealed record PageMetadata(
    string? Title,
    string? Description,
    string? FaviconUrl);

/// <summary>
/// 元数据解析（方案 4.1，自 LinkService 正则组下沉）：解析 HTML 提取标题/描述/favicon。
/// 网络抓取在模块层（闸外），本契约只做纯解析。
/// </summary>
public interface IMetadataParser
{
    PageMetadata Parse(string html, Uri pageUri);
}
