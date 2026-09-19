using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LinkPocket.Kernel;

/// <summary>
/// 文件夹**同层唯一命名**的统一入口（Kernel 职责：命名）。
///
/// <para>凡是会写入 <c>folders.name</c> 的命令都必须经此解析——不允许任何模块自行拼编号，
/// 也不允许"调用方/UI 层负责编号"这种分工（实测事故：一半入口不编号 + UI 另有一份大小写敏感的
/// 算法，结果同一个目录里出现 6 个同名文件夹，路径解析与人工辨识全部失效）。</para>
///
/// <para>语义 = Windows：同层（同一父目录，根级也算一层）内撞名一律自动编号「名 (2)」，
/// 不同目录可同名。根用 <c>null</c> 表示（零哨兵）。</para>
/// </summary>
public static class FolderNaming
{
    /// <summary>
    /// 把 <paramref name="desired"/> 解析为 <paramref name="parentId"/> 下的唯一名。
    /// <paramref name="excludeId"/> = 需要排除自身的场景（改名 / 原地移动：自身不算占用者）。
    /// </summary>
    public static async Task<string> ResolveAsync(IUnitOfWork uow, string? parentId, string desired,
        string? excludeId = null, CancellationToken ct = default)
    {
        var siblings = await uow.Folders.ChildrenOfAsync(parentId == null ? null : new FolderId(parentId), ct);
        var taken = siblings
            .Where(f => excludeId == null || !string.Equals(f.FolderId, excludeId, StringComparison.Ordinal))
            .Select(f => f.Name);
        return WindowsNamingPolicy.Instance.Resolve(desired, taken);
    }
}

/// <summary>
/// 批量导入场景的**同层占用表**（内存累积）：一次导入内部逐项累积已用名，跨项不撞。
///
/// <para>为什么需要它：<see cref="FolderNaming.ResolveAsync"/> 每次只查库，而导入是"一次批量写入"——
/// 同一批内后面写入的文件夹还不在库里，查库查不到，于是整批会算出同一个编号名（这正是历史上
/// "同一目录 6 个「python (6)」"的成因之一）。导入类命令必须用本表：**先预置既有同层名，再逐项累积**。</para>
///
/// <para>不是命名算法本身——编号口径仍在 <see cref="WindowsNamingPolicy"/>（唯一实现），本表只负责"占用集合"。</para>
/// </summary>
public sealed class SiblingNameTable
{
    private readonly Dictionary<string, HashSet<string>> _byParent = new(StringComparer.Ordinal);

    /// <summary>预置某层既有占用名（导入前把库里已有的同层名灌进来）。</summary>
    public void Seed(string? parentId, IEnumerable<string> names)
    {
        var set = TakeSet(parentId);
        foreach (var name in names) set.Add(name);
    }

    /// <summary>解析并**登记**唯一名（同层后续项自动避开它）。</summary>
    public string Resolve(string? parentId, string desired)
    {
        var set = TakeSet(parentId);
        var resolved = WindowsNamingPolicy.Instance.Resolve(desired ?? string.Empty, set);
        set.Add(resolved);
        return resolved;
    }

    /// <summary>取（必要时创建）某层的占用集合——比较器取自命名策略，命名口径只有一处。parentId = null 表示根层。</summary>
    private HashSet<string> TakeSet(string? parentId)
    {
        var key = parentId ?? string.Empty;   // 空串只是字典键，不是实体 ID 形状（零哨兵）
        if (!_byParent.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(WindowsNamingPolicy.Instance.Comparer);
            _byParent[key] = set;
        }
        return set;
    }
}
