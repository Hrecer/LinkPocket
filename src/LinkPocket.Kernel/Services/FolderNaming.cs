using System;
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
