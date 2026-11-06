using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Data.Sqlite;

namespace UiBench;

/// <summary>
/// 真冷盘的“清缓存”动作：清空 Windows standby list（文件缓存页随之丢弃）+ 系统缓存工作集
/// + 当前进程的 SQLite 连接池/托管缓冲。只动缓存，不动库文件；需要提升权限。
/// </summary>
/// <remarks>
/// 进程和界面本身保持热态（程序集、模板、JIT 都已就位），清掉的是**数据**的每一层缓存：
/// ① Microsoft.Data.Sqlite 连接池里的原生页缓存（ClearAllPools）；② 托管读缓冲（强制 GC）；
/// ③ Windows 文件缓存（SetSystemFileCacheSize 收缩 + NtSetSystemInformation 清 standby）。
/// 三者都清掉后，下一次读库只能落盘。
/// </remarks>
internal static class ColdCache
{
    private const int SystemMemoryListInformation = 0x50;
    private const int MemoryPurgeStandbyList = 4;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const int SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public Luid Luid;
        public int Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetSystemFileCacheSize(IntPtr minimumFileCacheSize, IntPtr maximumFileCacheSize,
        uint flags);

    /// <summary>当前进程是否已提升权限。</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>启用清缓存所需的两条特权；返回每一项的结果给报告留痕。</summary>
    public static bool EnablePrivileges(out string detail)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenAdjustPrivileges, out var token))
        {
            detail = $"OpenProcessToken 失败 ({Marshal.GetLastWin32Error()})";
            return false;
        }

        try
        {
            var parts = new List<string>();
            var allOk = true;
            foreach (var name in new[] { "SeProfileSingleProcessPrivilege", "SeIncreaseQuotaPrivilege" })
            {
                if (!LookupPrivilegeValue(null, name, out var luid))
                {
                    parts.Add($"{name}=Lookup 失败({Marshal.GetLastWin32Error()})");
                    allOk = false;
                    continue;
                }

                var state = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SePrivilegeEnabled,
                };
                if (!AdjustTokenPrivileges(token, false, ref state,
                        (uint)Marshal.SizeOf<TokenPrivileges>(), IntPtr.Zero, IntPtr.Zero))
                {
                    parts.Add($"{name}=Adjust 失败({Marshal.GetLastWin32Error()})");
                    allOk = false;
                }
                else if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
                {
                    parts.Add($"{name}=未授予");
                    allOk = false;
                }
                else
                {
                    parts.Add($"{name}=ok");
                }
            }

            detail = string.Join("；", parts);
            return allOk;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>清空三层数据缓存，返回耗时与 standby 页数（证明缓存真的被清掉了）。</summary>
    public static PurgeResult Purge()
    {
        var sw = Stopwatch.StartNew();

        // ① + ②：连接池里的 SQLite 原生页缓存 + 托管读缓冲。
        SqliteConnection.ClearAllPools();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        var standbyBefore = StandbyPages();

        // ③：收缩系统文件缓存工作集，再清空 standby list（-1/-1 = 不改变大小上限，仅触发收缩）。
        var fileCacheOk = SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0);
        var fileCacheError = fileCacheOk ? 0 : Marshal.GetLastWin32Error();
        var command = MemoryPurgeStandbyList;
        var purgeStatus = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));

        var standbyAfter = StandbyPages();
        sw.Stop();
        return new PurgeResult(sw.ElapsedMilliseconds, standbyBefore, standbyAfter,
            fileCacheOk, fileCacheError, purgeStatus);
    }

    /// <summary>standby list 的页数（SYSTEM_MEMORY_LIST_INFORMATION.PageCountByPriority 之和）。</summary>
    private static long StandbyPages()
    {
        // 结构 = 5 个 ULONG_PTR + PageCountByPriority[8] + RepurposedPagesByPriority[8] + 1 个 ULONG_PTR。
        const int fieldsBeforePriority = 5;
        const int priorityBuckets = 8;
        var size = (fieldsBeforePriority + priorityBuckets + priorityBuckets + 1) * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = NtQuerySystemInformation(SystemMemoryListInformation, buffer, size, out _);
            if (status != 0) return -1;
            long pages = 0;
            for (var i = 0; i < priorityBuckets; i++)
                pages += Marshal.ReadIntPtr(buffer, (fieldsBeforePriority + i) * IntPtr.Size).ToInt64();
            return pages;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>一次清缓存的读数；standby 页数 -1 = 查询失败。</summary>
internal readonly record struct PurgeResult(
    long ElapsedMs,
    long StandbyBeforePages,
    long StandbyAfterPages,
    bool FileCacheOk,
    int FileCacheError,
    int StandbyStatus)
{
    public long StandbyBeforeMb => StandbyBeforePages < 0 ? -1 : StandbyBeforePages * 4096 / 1048576;
    public long StandbyAfterMb => StandbyAfterPages < 0 ? -1 : StandbyAfterPages * 4096 / 1048576;
}
