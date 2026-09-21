using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// **碰"当前界面语言"的测试必须串行**（xunit 默认按类并行）。
/// </summary>
/// <remarks>
/// <para>
/// <c>LocTable.Instance</c> 是**进程级单例**（全站唯一取词表，设计如此），
/// 而"切语言"是它的全局状态。两个测试类并行时：一个类刚切到英文，另一个类正在断言中文文案 →
/// 报出「Expected: 无法复制 / Actual: Cannot copy」这类**看起来像产品缺陷**的失败
/// （实测踩到：<c>TransferPipelineTests</c> / <c>InlineRenameTests</c> / <c>TrashViewModelTests</c>
/// 三条"语言串味"假红）。
/// </para>
/// <para>
/// 这正是 <c>WARNINGS 68</c> 的纪律：**共享进程级静态的测试必须显式串行**——
/// "类内串行"不等于"全程序集串行"。凡新增"切语言 / 读当前语言"的测试类，一律挂本集合。
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class LocaleStateCollection
{
    /// <summary>集合名（各类必须逐字一致）。</summary>
    public const string Name = "LocaleState";
}
