namespace ProtocolSmoke;

/// <summary>冒烟断言 helper（失败即抛，中止全部后续节）。</summary>
internal static class Asserts
{
    public static void That(bool cond, string msg)
    {
        if (!cond) throw new Exception("断言失败: " + msg);
    }

    /// <summary>性能断言：DEBUG 构建按倍数放宽（方案 7.3 门槛按 Release/CI 口径标定）。</summary>
    public static void Within(long elapsedMs, long limitMs, string what)
    {
#if DEBUG
        limitMs *= 5;
#endif
        That(elapsedMs <= limitMs, $"{what} 应 ≤ {limitMs}ms，实际 {elapsedMs}ms");
    }
}
