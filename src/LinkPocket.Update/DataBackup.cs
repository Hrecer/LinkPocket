using System.IO.Compression;

namespace LinkPocket.Update;

/// <summary>
/// 更新前的**数据快照**（§5.3）：把 <c>data\</c> 整目录打成 zip 放到**安装目录旁**
/// （<c>&lt;安装名&gt;.data-backup-&lt;时间戳&gt;.zip</c>，与 <c>data.pending-*</c> 同族），供用户确认数据后自删。
/// 只做字节级打包（数据区唯一读面；不做任何格式解析）；调用方在**动任何东西之前**调用，失败即中止且零触碰。
/// 刻意**不自动清理**：每次更新生成新的一份，旧的留给用户处置。
/// </summary>
internal static class DataBackup
{
    /// <summary>生成数据备份；无 <c>data\</c> = 无备份可做（返回 null）。失败抛 <see cref="UpdateFailure"/>。</summary>
    public static string? Create(UpdateContext ctx, Action<string> emit)
    {
        if (!Directory.Exists(ctx.DataDir))
        {
            emit("no data directory; nothing to back up");
            return null;
        }

        var backupPath = Path.Combine(ctx.ParentDir, $"{ctx.InstallName}.data-backup-{ctx.Timestamp}.zip");
        try
        {
            ZipFile.CreateFromDirectory(ctx.DataDir, backupPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(backupPath)) File.Delete(backupPath); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* 尽力而为 */ }
            throw new UpdateFailure($"failed to create the data backup ({backupPath}): {ex.Message}");
        }

        emit($"data backup created: {backupPath}");
        return backupPath;
    }
}
