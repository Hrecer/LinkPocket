namespace LinkPocket.Kernel;

/// <summary>
/// Windows 风格同名自动编号——唯一出处（自 BrowserViewModel 内联算法下沉）。
/// 纯函数、零依赖、可表驱动单测：重名时生成「name (2)」「name (3)」…。
/// </summary>
public interface INamingPolicy
{
    /// <summary>desired 期望名；siblings = 同层已占用名集合（大小写不敏感，Windows 口径）。</summary>
    string Resolve(string desired, IReadOnlySet<string> siblings);
}
