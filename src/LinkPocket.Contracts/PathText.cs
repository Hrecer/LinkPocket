using System.Collections.Generic;

namespace LinkPocket.Contracts;

/// <summary>
/// 路径段的转义 / 切分——**唯一实现**（地址栏、canonical 路径、面包屑共用）。
/// <remarks>放在契约层是因为它有三类互不能引用的消费者：引擎/数据层用它拼 canonical 路径、
/// UIKit 用它解析地址栏、UI 用它投影显示串。</remarks>
/// 分隔符 `/` 与名字里的字面 `/` 冲突：名内 `/` 以 `\/` 转义（`\\` 转义 `\`），
/// 任何名字都能在地址栏无损往返（写回时转义、解析时解码）。
/// </summary>
public static class PathText
{
    /// <summary>段名 → 地址栏文本（先 \\ 后 /，避免转义序列互相污染）。</summary>
    public static string Escape(string name)
        => name.Replace("\\", "\\\\").Replace("/", "\\/");

    /// <summary>地址栏段 → 真实名字（先 \/ 后 \\）。</summary>
    public static string Unescape(string seg)
        => seg.Replace("\\/", "/").Replace("\\\\", "\\");

    /// <summary>最后一次"未转义的 /"分隔符的位置（前面反斜杠数为偶）；无则 -1。</summary>
    public static int LastSeparatorIndex(string text)
    {
        var slash = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '/') continue;
            var bs = 0;
            for (var j = i - 1; j >= 0 && text[j] == '\\'; j--) bs++;
            if (bs % 2 == 0) slash = i;
        }
        return slash;
    }

    /// <summary>按转义规则切分并解码路径段（'\/' = 名字里的字面斜杠，'\\' = 字面反斜杠），剔除空段并修饰空白。</summary>
    public static IReadOnlyList<string> Split(string text)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '\\' || text[i + 1] == '/'))
            {
                current.Append(text[i + 1]);   // 转义对 → 字面字符
                i++;
                continue;
            }
            if (c == '/')
            {
                if (current.Length > 0) segments.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) segments.Add(current.ToString().Trim());
        return segments;
    }
}
