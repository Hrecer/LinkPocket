using System;
using System.Collections.Generic;

namespace LinkPocket.Kernel;

/// <summary>
/// Windows 风格同名自动编号——**唯一出处**（自 BrowserViewModel 内联算法下沉）。
/// 纯函数、零依赖、可表驱动单测：重名时生成「name (2)」「name (3)」…。
/// </summary>
public interface INamingPolicy
{
    /// <summary>
    /// 同层名比较口径（**大小写不敏感**，Windows 口径：<c>Python</c> 与 <c>python</c> 视为同名）。
    /// 调用方构造"已占用名集合"时必须使用本比较器，**不得自定**——
    /// 口径只能有一处，否则策略放行而 DB 唯一索引拒绝（两处口径不一致 = 未定义行为）。
    /// </summary>
    StringComparer Comparer { get; }

    /// <summary>把 desired 解析为同层唯一名；siblings = 同层已占用名集合。</summary>
    string Resolve(string desired, IEnumerable<string> siblings);
}
