using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// **碰"当前界面语言"的测试**的集合标记（xunit 缺省按类并行）。
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
/// ⚠️ <b>本集合只是标记，真正的串行由程序集级 <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c>
/// 保证</b>（见 <c>AssemblyInfo.cs</c>）：xunit 里没挂集合的类各自成为集合，
/// 只给几个类挂 <c>[Collection]</c> 挡不住它们与其余类的竞争——第一版就是这么做而继续假红的。
/// 保留本标记是为了让"这个类碰全局语言状态"这件事在代码里看得见。
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class LocaleStateCollection
{
    /// <summary>集合名（各类必须逐字一致）。</summary>
    public const string Name = "LocaleState";
}
