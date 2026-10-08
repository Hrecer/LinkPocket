using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Data;

/// <summary>
/// 附属库（操作记录库）的 EF 上下文：只承载**非书签数据**（审计 <c>audit_log</c> / 幂等 <c>idempotency</c>），
/// 与 <see cref="LinkPocketDbContext"/> 无关——附属库没有 EF 实体映射，一律走原生参数化 SQL
/// （与 <c>audit_log</c> 的既有形态一致），因此这里只是一个连接壳。
///
/// <para><b>为什么单独一个库</b>：用户数据库只放书签数据（links / folders / 回收站）。
/// 操作记录搬出去之后：用户库的体积、损坏面、备份语义都只与书签有关；审计历史可以单独清理/归档；
/// 附属库**可以随时整个删掉**——删掉只丢历史与 24h 幂等去重，不动任何书签数据。</para>
/// </summary>
public sealed class OpsDbContext(DbContextOptions<OpsDbContext> options) : DbContext(options);
