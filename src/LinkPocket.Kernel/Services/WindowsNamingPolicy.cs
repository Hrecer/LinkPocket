using System.Text.RegularExpressions;

namespace LinkPocket.Kernel;

/// <summary>
/// Windows 风格同名自动编号（方案 4.1，唯一出处）：
/// 「abc」撞名 → 「abc (2)」→「abc (3)」…；输入名本身已带「(N)」尾缀时先剥掉再编号
/// （与既有前端内联算法 <c>GenerateUniqueName</c> 逐字等价——行为等价项）。
/// 纯函数、零依赖、可表驱动单测；跨场景复用点 = 文件夹移动/复制/粘贴/批量移动/导入。
/// </summary>
public sealed class WindowsNamingPolicy : INamingPolicy
{
    /// <summary>无状态单例（纯函数线程安全）。</summary>
    public static readonly WindowsNamingPolicy Instance = new();

    /// <summary>兜底上限：999 个编号之后按时间戳命名（与既有算法一致，几乎不可达）；毫秒粒度防同秒撞名。</summary>
    private const int MaxNumberedAttempts = 999;

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="siblings"/> = 同层已占用名集合。<b>比较口径由调用方决定</b>
    /// （现状口径 = <see cref="StringComparer.CurrentCulture"/>，调用方构造集合时选定），
    /// 本实现只对集合做包含判断，不再套用任何自己的比较器。
    /// </remarks>
    public string Resolve(string desired, IReadOnlySet<string> siblings)
    {
        var original = (desired ?? string.Empty).Trim();
        if (original.Length == 0) original = "未命名";

        if (!siblings.Contains(original)) return original;

        var baseName = Regex.Replace(original, @"\s*\(\d+\)$", string.Empty);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "未命名";

        for (var i = 2; i <= MaxNumberedAttempts; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!siblings.Contains(candidate)) return candidate;
        }

        return $"{baseName} ({DateTime.Now:HHmmssff})";   // 毫秒粒度：与前端 GenerateUniqueName 逐字等价
    }
}
