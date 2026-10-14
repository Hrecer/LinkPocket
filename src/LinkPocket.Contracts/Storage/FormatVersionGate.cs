namespace LinkPocket.Contracts;

/// <summary>
/// 「家族/主版本.次版本」格式版本串的**唯一判定实现**（`lpbackup` 与 `lptrash` 同族条款）。
///
/// <para>判据按**兼容性**分档，而不是"长得像就算"：
/// <list type="bullet">
/// <item><b>次版本（`.N`）</b>= 只增字段 / 只放宽约束 → **向后兼容**，读取端可以照收（只要 ≤ 本实现的次版本）；</item>
/// <item><b>主版本（`N.`）</b>= 结构变了 → 只有列在"读取端认识的主版本集合"里的才收
/// （拒绝而不是"尽力而为地猜"——猜必然猜错，与零兼容红线一致）。</item>
/// </list></para>
///
/// <para><b>为什么必须是唯一实现</b>：读取路径的版本口味只许有一处——两个格式（备份 / 回收站）各写一份
/// "前缀匹配"曾经把 `2abc` / `20.0` / `2.9.9-beta` 这类串放进了导入器，那是缺陷（见
/// <c>BackupRobustnessTests</c> 的版本门矩阵）。</para>
/// </summary>
public static class FormatVersionGate
{
    /// <summary>解析：`family/N.N`（family 大小写不敏感；次版本可省略 = 0）。</summary>
    public static bool TryParse(string? version, out string family, out int major, out int minor)
    {
        family = string.Empty;
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(version)) return false;

        var text = version.Trim();
        var slash = text.IndexOf('/');
        if (slash <= 0) return false;
        family = text[..slash];

        var parts = text[(slash + 1)..].Split('.');
        if (parts.Length is < 1 or > 2) return false;
        if (!int.TryParse(parts[0], out major)) return false;
        if (parts.Length > 1 && !int.TryParse(parts[1], out minor)) return false;
        return true;
    }

    /// <summary>
    /// 该版本串是否能被"家族 = <paramref name="family"/>、认识 <paramref name="readableMajors"/>、
    /// 自身版本 = <paramref name="selfFormatVersion"/>"的实现读取。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不是"主版本必须相等"</b>：这类文件是**用户数据跨版本迁移的通道**——将来应用大版本升级时，
    /// 用户手里的旧包就是他的数据。写死"相等"会让新版本把所有旧包一律拒之门外。
    /// 正确形态 = 本实现**认识**的（即已实现升级路径的）主版本都收，见各格式的 `ReadableMajors`。
    /// </para>
    /// <para>
    /// <b>加主版本（把格式版本改成 `N.0`）时必须做的事</b>：① 在读取路径上实现"旧主版本包 → 新结构"的
    /// 升级换算；② 把旧主版本加进该格式的 `ReadableMajors`（必须始终包含自己的主版本）；
    /// ③ 同步开发文档与 `ProtocolSmoke` 的往返断言。只改版本号而不加升级路径 = 把所有旧包变成不可读。
    /// </para>
    /// </remarks>
    public static bool Accepts(string? version, string family, IReadOnlyList<int> readableMajors, string selfFormatVersion)
    {
        if (!TryParse(version, out var actualFamily, out var major, out var minor)) return false;
        if (!string.Equals(actualFamily, family, StringComparison.OrdinalIgnoreCase)) return false;
        if (!readableMajors.Contains(major)) return false;
        if (!TryParse(selfFormatVersion, out _, out var selfMajor, out var selfMinor)) return false;
        return major < selfMajor || minor <= selfMinor;   // 旧主版本走升级路径；同主版本吃到本实现的次版本
    }
}
