using LinkPocket.Composition;
using LinkPocket.Contracts;
using LinkPocket.Engine;

namespace ProtocolSmoke;

/// <summary>
/// 冒烟组装根（定稿，2026-09-18 组合抽取）：全新临时库（SchemaMigrator 经工厂建库）+
/// 引擎组合根（由共享 Composition 收敛：九模块全量注册 + EngineCore 审计/幂等落库 + 编排层）。
/// 「整库重置」语义 = 丢弃当前引擎与库文件、另起全新实例（引擎无删文件命令，等价终态：全新空库）。
/// </summary>
internal static class ProbeEnv
{
    public static (EngineClient Client, EngineWire Wire, string DbPath) Create()
    {
        var dbPath = Path.Combine(TempArea.Resolve(), $"lpsmoke_{Guid.NewGuid():N}.db");
        var composed = EngineComposer.Compose(dbPath, new ComposeOptions { StagingRoot = StagingRootFor(dbPath) });
        return (composed.Client, composed.Wire!, dbPath);
    }

    public static EngineClient CreateEngineOn(string dbPath)
        => EngineComposer.Compose(dbPath, new ComposeOptions
        {
            StagingRoot = StagingRootFor(dbPath),
            BuildWire = false,   // 性能/专项路径只消费引擎客户端，不需要 wire
        }).Client;

    /// <summary>暂存区根 = 库文件同目录下的派生目录（由 <see cref="TryDelete"/> 一并清理，不留残留）。</summary>
    internal static string StagingRootFor(string dbPath)
        => Path.Combine(Path.GetDirectoryName(dbPath)!, $"lpsmoke_staging_{Path.GetFileNameWithoutExtension(dbPath)}");

    /// <summary>尽力清理临时库与其暂存区（连接池句柄滞留会阻止删除，失败不打断流程）。</summary>
    public static void TryDelete(string dbPath)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
        catch
        {
            // 临时文件交给系统清理
        }

        // 暂存区（staging 会往里拷文件）与 WAL 副本一并清掉：测试不得在临时根留垃圾
        try
        {
            var staging = StagingRootFor(dbPath);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch
        {
            // 尽力而为
        }
    }
}
