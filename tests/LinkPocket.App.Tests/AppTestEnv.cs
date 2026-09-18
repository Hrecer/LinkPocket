using System.IO;
using LinkPocket.Composition;
using LinkPocket.Contracts;

namespace LinkPocket.App.Tests;

/// <summary>
/// 页面 VM 单测的引擎支撑：与 App 同口径的组合根（审计/幂等落库 + 编排层 + wire），
/// 临时库落 <c>LP_TEMP_ROOT</c>（CI 指工作区，不污染用户临时区），用例结束自行清理。
/// </summary>
internal static class AppTestEnv
{
    public static (EngineClient Client, LinkPocket.Engine.EngineCore Engine, string DbPath) Create()
    {
        var dbPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpapp_{Guid.NewGuid():N}.db");
        var composed = EngineComposer.Compose(dbPath);
        return (composed.Client!, composed.Engine, dbPath);
    }

    /// <summary>清理临时库（含 WAL/SHM 副本）与派生暂存区根；连接池句柄滞留时尽力而为。</summary>
    public static void Delete(string dbPath)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        catch
        {
            // 池清理失败不阻断文件删除
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = dbPath + suffix;
            try { if (File.Exists(file)) File.Delete(file); } catch { /* 句柄滞留 → 系统清理 */ }
        }

        var staging = Path.Combine(Path.GetDirectoryName(dbPath)!,
            $"linkpocket_staging_{Path.GetFileNameWithoutExtension(dbPath)}");
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* 尽力而为 */ }
    }
}