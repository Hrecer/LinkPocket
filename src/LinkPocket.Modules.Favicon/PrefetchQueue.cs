using System.Collections.Concurrent;
using LinkPocket.Contracts;

namespace LinkPocket.Modules.Favicon;

/// <summary>
/// 后台预取队列（internal）：并发 4、按解析后地址去重、失败跳过（下次 prefetch 再试）。
/// fire-and-forget：任务异常被吞（图标缺失不阻断任何业务）；进程退出时未完成任务自然丢弃。
/// </summary>
internal static class PrefetchQueue
{
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(4, 4);
    private static Task? _drainTask;

    /// <summary>入队缺失项并确保排水任务在跑；返回本次新入队数。</summary>
    public static int EnqueueMissing(IReadOnlyList<string> faviconUrls)
    {
        var queued = 0;
        foreach (var raw in faviconUrls)
        {
            var resolved = FaviconCache.ResolveFaviconUrl(raw);
            if (resolved.Length == 0) continue;
            if (File.Exists(FaviconCache.GetCacheFilePath(resolved))) continue;
            if (!Pending.TryAdd(resolved, 0)) continue;
            queued++;
        }

        if (queued > 0)
        {
            _drainTask ??= Task.Run(DrainAsync);
        }

        return queued;
    }

    private static async Task DrainAsync()
    {
        while (TryIterate(out var url))
        {
            await Gate.WaitAsync();
            try
            {
                if (Pending.TryRemove(url, out _))
                    _ = await FaviconCache.EnsureCachedAsync(url, CancellationToken.None);
            }
            catch
            {
                // 预取失败不重试不阻断（下次 prefetch 再试）
            }
            finally
            {
                Gate.Release();
            }
        }

        _drainTask = null;
    }

    /// <summary>安全迭代当前队列快照（逐项取出）。</summary>
    private static bool TryIterate(out string url)
    {
        foreach (var key in Pending.Keys)
        {
            url = key;
            return true;
        }

        url = string.Empty;
        return false;
    }
}
