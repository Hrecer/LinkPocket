using LinkPocket.Composition;
using LinkPocket.Data;
using LinkPocket.Engine;

namespace LinkPocket.Modules.Tests;

/// <summary>测试支撑：临时文件库（真实 WAL 语义）+ 九模块全量注册（组合由共享 Composition 收敛）。
/// 裸引擎口径：无审计落库、无幂等落库、无编排层（本工程断言 Describe==58 条业务命令）、无 wire。</summary>
internal static class TestHost
{
    public static (EngineCore Engine, LinkPocketDbContextFactory Factory, string DbPath) Create()
    {
        var dbPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpmod_{Guid.NewGuid():N}.db");
        // 工厂构造内已完成 WAL 启用 + schema 建库（SchemaMigrator），无需 EnsureCreated
        var composed = EngineComposer.Compose(dbPath, new ComposeOptions
        {
            SqlAudit = false,               // 模块测试保持引擎默认（纯内存审计，加速）
            SqlIdempotency = false,         // 模块测试保持引擎默认（内存幂等表）
            IncludeOrchestration = false,   // 模块测试只管 58 条业务命令（编排语义归 Engine.Tests）
            BuildWire = false,              // 模块测试不需要 wire
        });
        return (composed.Engine, composed.Factory!, dbPath);
    }

    /// <summary>带**审计落库**的宿主（audit.query / audit.prune 的测试用：查询读的就是 audit_log 表）。
    /// 其余口径同上（无幂等落库、无编排、无 wire）。</summary>
    public static (EngineCore Engine, LinkPocketDbContextFactory Factory, string DbPath) CreateWithAudit()
    {
        var dbPath = Path.Combine(LinkPocket.Engine.TempArea.Resolve(), $"lpmod_{Guid.NewGuid():N}.db");
        var composed = EngineComposer.Compose(dbPath, new ComposeOptions
        {
            SqlAudit = true,
            SqlIdempotency = false,
            IncludeOrchestration = false,
            BuildWire = false,
        });
        return (composed.Engine, composed.Factory!, dbPath);
    }
}
