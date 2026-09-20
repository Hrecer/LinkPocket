using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// **共用「界面偏好文件」的测试必须串行**（xunit 默认按类并行）。
/// </summary>
/// <remarks>
/// <para>
/// <c>UiPreferenceStore</c> 读写的是**同一个进程级文件**（<c>{BaseDirectory}/ui-preferences.json</c>），
/// 且保存走"临时文件 + <c>File.Replace</c>"的原子替换。两个测试类并行时：
/// 一个类刚把文件替换掉/删掉，另一个类正好在 Load —— 于是出现
/// 「原子写不留tmp」这类**看似随机的失败**，以及文件句柄竞争带来的长时间等待。
/// </para>
/// <para>
/// 这正是 <c>WARNINGS 68</c> 的同族纪律：**共享进程级状态的测试必须显式串行**，
/// 不能靠"类内串行"（类内串行 ≠ 全程序集串行）。凡新增"碰这个文件"的测试类，
/// 一律挂 <see cref="UiPreferencesCollection"/>。
/// </para>
/// </remarks>
[CollectionDefinition(UiPreferencesCollection.Name)]
public sealed class UiPreferencesCollection
{
    /// <summary>集合名（两个类必须逐字一致）。</summary>
    public const string Name = "UiPreferences";
}