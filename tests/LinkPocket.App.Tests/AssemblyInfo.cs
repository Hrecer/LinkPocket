using Xunit;

// 本程序集的用例**一律串行**（xunit 缺省按类并行）。
//
// 为什么必须这样，而不是"给几个类挂 Collection"：
// xunit 的并行单位是"集合"，而**没挂 [Collection] 的类各自成为一个集合**，彼此并行、也与自定义集合并行。
// 换句话说"只给碰全局状态的类挂集合"挡不住它们与其余类的竞争——实测症状就是三条
// 「Expected: 无法复制 / Actual: Cannot copy」式的**语言串味假红**。
//
// 本程序集有两类**进程级静态**状态：
//   ① `LocTable.Instance`（全站唯一取词表，"切语言"是它的全局状态）；
//   ② `ThemeService` / `FontCatalog` / `UiPreferenceStore`（主题、字体、偏好文件）。
// 两者都是"设计如此"的单例（不能为测试改成可注入），所以正确处置是**让测试串行**——
// 与 WARNINGS 68 同一条纪律：共享进程级静态的测试必须显式串行。
// 代价可接受：整套 250+ 用例串行约 3 秒。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
