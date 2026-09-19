using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LinkPocket.Kernel;

/// <summary>
/// Windows 风格同名自动编号（**唯一实现**）：
/// 「abc」撞名 → 「abc (2)」→「abc (3)」…；输入名本身已带「(N)」尾缀时先剥掉再编号。
///
/// <para>比较口径由本实现自己持有（<see cref="Comparer"/> = 大小写不敏感），调用方不得另立口径：
/// 曾因 UI 自持一份大小写敏感的算法 + 一半写入口根本不编号，导致同一目录里出现 6 个同名文件夹（实测事故）。</para>
///
/// <para>跨场景复用点（全部经 <see cref="IFolderNaming"/> 统一入口）= 文件夹新建/改名/移动/批量移动/复制/导入/还原单元。</para>
///
/// <para>**本类型程序集内可见**：编号算法只有 Kernel 自己能碰，模块连"顺手用一下策略"都做不到——
/// 命名口径不可能出现第二份实现（编译期即挡死）。</para>
/// </summary>
internal sealed class WindowsNamingPolicy : INamingPolicy
{
    /// <summary>无状态单例（纯函数线程安全）。</summary>
    internal static readonly WindowsNamingPolicy Instance = new();

    /// <summary>兜底上限：999 个编号之后按时间戳命名（几乎不可达）；毫秒粒度防同秒撞名。</summary>
    private const int MaxNumberedAttempts = 999;

    /// <summary>同层名比较口径：大小写不敏感（Windows 口径）。DB 唯一索引用 NOCASE（ASCII 折叠，比本口径更宽松），
    /// 因此本策略只会更严格——不存在"策略放行、索引拒绝"的方向。</summary>
    public StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <inheritdoc />
    public string Resolve(string desired, IEnumerable<string> siblings)
    {
        var original = (desired ?? string.Empty).Trim();
        if (original.Length == 0) original = "未命名";

        var taken = new HashSet<string>(siblings ?? Array.Empty<string>(), Comparer);
        if (!taken.Contains(original)) return original;

        var baseName = Regex.Replace(original, @"\s*\(\d+\)$", string.Empty);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "未命名";

        for (var i = 2; i <= MaxNumberedAttempts; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }

        return $"{baseName} ({DateTime.Now:HHmmssff})";   // 毫秒粒度，几乎不可达
    }
}
