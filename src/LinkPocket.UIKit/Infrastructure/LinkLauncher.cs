using System.Diagnostics;
using LinkPocket.Contracts;

namespace LinkPocket.Services;

/// <summary>
/// 「打开链接」的**唯一出口**：只放行 http/https，其余一律拒绝并留痕。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要收成一个出口</b>：ShellExecute 会按 Windows 的默认关联执行目标 —— 一个
/// <c>file:///C:/…exe</c>、<c>\\host\share\x.exe</c> 或其它任意协议的地址，用户点一下就等于运行程序。
/// 而书签的 <c>url</c> 可以来自导入的书签文件 / 备份包（**其中的地址不是用户敲进去的**），
/// 所以"打开"这件事必须在同一条链上做协议判定，六处各写一遍 try/catch 是挡不住的。
/// </para>
/// <para>
/// 拒绝不是静默兜底：调用方拿到结果后应当告诉用户"这一项不是网页地址"（<c>common.linkNotOpenable</c>），
/// 日志侧同样留痕。
/// </para>
/// </remarks>
public static class LinkLauncher
{
    /// <summary>打开结果：调用方据此决定要不要提示（拒绝与失败都不是静默的成功）。</summary>
    public enum Result
    {
        /// <summary>已交给系统默认浏览器。</summary>
        Opened,

        /// <summary>地址不是 http/https（含空地址）—— 已拒绝，未执行任何进程。</summary>
        NotWebAddress,

        /// <summary>是网页地址但系统打开失败（无默认浏览器 / 被策略拦下等）。</summary>
        Failed,
    }

    /// <summary>用系统默认浏览器打开一个网页地址；非 http/https 一律拒绝。</summary>
    public static Result Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Result.NotWebAddress;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            LpLog.Warn($"refused to open a non-web link: {url}");
            return Result.NotWebAddress;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return Result.Opened;
        }
        catch (Exception ex)
        {
            LpLog.Error($"failed to open the site: {uri}", ex);
            return Result.Failed;
        }
    }
}
