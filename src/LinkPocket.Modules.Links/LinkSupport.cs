using LinkPocket.Contracts;
using LinkPocket.Api;
using LinkPocket.Data;
using LinkPocket.Kernel;

namespace LinkPocket.Modules.Links;

/// <summary>链接域内部支撑：日期入参解析 / LinkCount 缓存回填。</summary>
internal static class LinkSupport
{
    /// <summary>日期入参解析（ISO 字符串；解析失败 = LP.VAL.002）。</summary>
    public static DateTime ParseDate(string raw, string context)
    {
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var value))
            return value;
        throw new EngineException(EngineErrors.Of(
            EngineErrors.TypeMismatch, $"「{context}」不是可识别的日期：{raw}"));
    }

    /// <summary>直接子链接计数缓存回填（LinkCount 列的既有维护口径）。</summary>
    public static async Task RefreshLinkCountAsync(Kernel.IUnitOfWork uow, string folderId, CancellationToken ct)
    {
        var counts = await uow.Links.CountByFolderAsync(ct);
        var folder = await uow.Folders.FindAsync(new FolderId(folderId), ct);
        if (folder != null) folder.LinkCount = counts.GetValueOrDefault(new FolderId(folderId));
    }
}
